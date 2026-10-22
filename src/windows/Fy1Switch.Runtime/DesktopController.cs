using Fy1Switch.Contracts;

namespace Fy1Switch.Runtime;

public sealed class DesktopController : IDesktopController, IAsyncDisposable
{
    private readonly IWorkerTransport _worker;
    private readonly IStartupRegistration _startup;
    private readonly StateStore _store;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private HostSnapshot _snapshot;
    private long _topologyGeneration;
    private long _displaySettingsGeneration;
    private long _workerRestartGeneration;
    private long _completedWorkerRestartGeneration;
    private readonly object _topologyGate = new();
    private volatile bool _invalidated;
    private int _disposed;
    public event Action? Changed;
    public bool Simulation { get; }
    public string DataDirectory { get; }

    public DesktopController(IWorkerTransport worker, IStartupRegistration startup, string dataDirectory, bool simulation)
    {
        _worker = worker;
        _startup = startup;
        DataDirectory = Path.GetFullPath(dataDirectory);
        Simulation = simulation;
        _store = new StateStore(DataDirectory);
        var binding = _store.LoadBinding();
        _invalidated = binding?.NeedsConfirmation ?? false;
        _snapshot = new(_store.DeviceId, binding, Unknown(), false, false, startup.Enabled, binding?.NeedsConfirmation == true ? "topology_changed" : null, simulation);
    }

    public Task<HostSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = Volatile.Read(ref _snapshot) with { WorkerRunning = _worker.IsRunning };
        if (_invalidated && snapshot.Binding is { } binding) snapshot = snapshot with { Binding = binding with { NeedsConfirmation = true }, Observation = Unknown(), ErrorCode = "topology_changed" };
        return Task.FromResult(snapshot);
    }
    private static PowerObservation Unknown() => new(DisplayPower.Unknown, null, DateTimeOffset.UtcNow, "unavailable");
    private void Publish(HostSnapshot snapshot)
    {
        if (_invalidated && snapshot.Binding is not null) snapshot = snapshot with { Binding = snapshot.Binding with { NeedsConfirmation = true } };
        Volatile.Write(ref _snapshot, snapshot with { WorkerRunning = _worker.IsRunning });
        try { Changed?.Invoke(); } catch { }
    }
    private void HandleError(string? code)
    {
        if (code is "topology_changed" or "target_not_found" or "target_mismatch" or "target_ambiguous" or "profile_unsupported" or "unsupported_profile" or "unsupported_topology" or "target_not_unique_or_not_ddc")
        {
            _invalidated = true;
            if (_snapshot.Binding is { } binding) _store.SaveBinding(binding with { NeedsConfirmation = true });
        }
        Publish(_snapshot with { ErrorCode = code, Observation = Unknown() });
    }
    private async Task<DiscoverySnapshot> DiscoverCoreAsync(CancellationToken cancellationToken)
    {
        var reply = await _worker.SendAsync(new(Guid.NewGuid(), "discover"), cancellationToken);
        if (!reply.Success || reply.Discovery is null) { HandleError(reply.ErrorCode); throw new ControlException(reply.ErrorCode ?? "discovery_failed"); }
        return reply.Discovery;
    }
    public async Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        Publish(_snapshot with { Busy = true });
        try { await _worker.StopAsync(); return await DiscoverCoreAsync(cancellationToken); }
        finally { Publish(_snapshot with { Busy = false }); _commands.Release(); }
    }
    public async Task<HostSnapshot> BindAsync(string targetKey, string topologyFingerprint, string name, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        Publish(_snapshot with { Busy = true });
        try
        {
            var generation = Interlocked.Read(ref _topologyGeneration);
            var discovered = await DiscoverCoreAsync(cancellationToken);
            var candidate = discovered.Candidates.SingleOrDefault(candidate => candidate.TargetKey == targetKey) ?? throw new ControlException("target_not_found");
            if (discovered.TopologyFingerprint != topologyFingerprint) throw new ControlException("topology_changed");
            var binding = new MonitorBinding(Guid.NewGuid(), candidate.TargetKey, string.IsNullOrWhiteSpace(name) ? candidate.Name : name.Trim()[..Math.Min(name.Trim().Length, 80)], candidate.Profile, topologyFingerprint);
            lock (_topologyGate)
            {
                if (generation != Interlocked.Read(ref _topologyGeneration)) throw new ControlException("topology_changed");
                _store.SaveBinding(binding);
                _invalidated = false;
                Publish(_snapshot with { Binding = binding, Observation = Unknown(), ErrorCode = null });
            }
            return _snapshot with { Busy = false };
        }
        finally { Publish(_snapshot with { Busy = false }); _commands.Release(); }
    }
    public async Task<HostSnapshot> UnbindAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        try { _store.SaveBinding(null); _invalidated = false; Publish(_snapshot with { Binding = null, Observation = Unknown(), ErrorCode = null }); return _snapshot; }
        finally { _commands.Release(); }
    }
    private MonitorBinding RequireBinding(Guid? targetId = null)
    {
        var binding = _snapshot.Binding ?? throw new ControlException("not_bound");
        if (targetId is not null && binding.TargetId != targetId) throw new ControlException("target_changed");
        if (_invalidated || binding.NeedsConfirmation) throw new ControlException("topology_changed");
        return binding;
    }
    public async Task<HostSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        Publish(_snapshot with { Busy = true });
        try
        {
            var binding = RequireBinding();
            var generation = Interlocked.Read(ref _topologyGeneration);
            var reply = await _worker.SendAsync(new(Guid.NewGuid(), "read", binding.TargetKey, binding.TopologyFingerprint), cancellationToken);
            if (generation != Interlocked.Read(ref _topologyGeneration)) throw new ControlException("topology_changed");
            if (!reply.Success) { HandleError(reply.ErrorCode); throw new ControlException(reply.ErrorCode ?? "read_failed"); }
            Publish(_snapshot with { Observation = reply.Observation ?? Unknown(), ErrorCode = null });
            return _snapshot with { Busy = false };
        }
        catch (ControlException exception) { HandleError(exception.Code); throw; }
        finally { Publish(_snapshot with { Busy = false }); _commands.Release(); }
    }
    public async Task<OperationRecord> ExecuteAsync(Guid targetId, PowerIntent intent, Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || targetId == Guid.Empty || !Enum.IsDefined(intent)) throw new ControlException("invalid_request");
        await _commands.WaitAsync(cancellationToken);
        var issued = false;
        OperationRecord? record = null;
        try
        {
            var existing = _store.FindOperation(operationId);
            if (existing is not null)
            {
                if (existing.TargetId != targetId || existing.Requested != intent) throw new ControlException("operation_id_conflict");
                return existing;
            }
            record = new(operationId, targetId, intent, null, OperationOutcome.Pending, null, null, DateTimeOffset.UtcNow);
            _store.SaveOperation(record);
            var binding = RequireBinding(targetId);
            var generation = Interlocked.Read(ref _topologyGeneration);
            Publish(_snapshot with { Busy = true });
            var resolved = intent;
            if (intent == PowerIntent.Toggle)
            {
                var current = await _worker.SendAsync(new(Guid.NewGuid(), "read", binding.TargetKey, binding.TopologyFingerprint), cancellationToken);
                if (!current.Success) throw new ControlException(current.ErrorCode ?? "read_failed");
                resolved = current.Observation?.State switch { DisplayPower.On => PowerIntent.Off, DisplayPower.Off => PowerIntent.On, _ => throw new ControlException("state_unknown") };
            }
            if (_invalidated || generation != Interlocked.Read(ref _topologyGeneration)) throw new ControlException("topology_changed");
            record = record with { Resolved = resolved };
            _store.SaveOperation(record);
            issued = true;
            var reply = await _worker.SendAsync(new(Guid.NewGuid(), "set", binding.TargetKey, binding.TopologyFingerprint, resolved), cancellationToken);
            if (generation != Interlocked.Read(ref _topologyGeneration)) throw new ControlException("topology_changed");
            var expected = resolved == PowerIntent.On ? DisplayPower.On : DisplayPower.Off;
            var outcome = reply.Success && reply.WriteAccepted == true
                ? reply.Observation?.State == expected ? OperationOutcome.Confirmed : OperationOutcome.AcceptedUnconfirmed
                : reply.WriteAccepted != false ? OperationOutcome.Unknown : OperationOutcome.Failed;
            record = record with { Outcome = outcome, ErrorCode = reply.ErrorCode, Observation = reply.Observation, UpdatedAt = DateTimeOffset.UtcNow };
            if (reply.ErrorCode is not null) HandleError(reply.ErrorCode);
            else Publish(_snapshot with { Observation = reply.Observation ?? Unknown(), ErrorCode = null });
        }
        catch (Exception exception) when (record is not null)
        {
            var error = exception is ControlException control ? control.Code : exception is OperationCanceledException ? "request_cancelled" : "worker_unavailable";
            record = record with { Outcome = issued ? OperationOutcome.Unknown : OperationOutcome.Failed, ErrorCode = error, UpdatedAt = DateTimeOffset.UtcNow };
            HandleError(error);
        }
        finally
        {
            try { if (record is not null && record.Outcome != OperationOutcome.Pending) _store.SaveOperation(record); }
            finally { Publish(_snapshot with { Busy = false }); _commands.Release(); }
        }
        return record!;
    }
    public async Task<OperationRecord?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        try { return _store.FindOperation(operationId); } finally { _commands.Release(); }
    }
    public async Task<HostSnapshot> SetStartupAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        try { _startup.SetEnabled(enabled); Publish(_snapshot with { StartupEnabled = _startup.Enabled }); return _snapshot; }
        finally { _commands.Release(); }
    }
    public async Task RevalidateTopologyAsync(CancellationToken cancellationToken = default, bool restartWorker = false)
    {
        long generation;
        lock (_topologyGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            generation = Interlocked.Increment(ref _displaySettingsGeneration);
            if (restartWorker) Interlocked.Increment(ref _workerRestartGeneration);
        }
        await _commands.WaitAsync(cancellationToken);
        var checking = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || generation != Interlocked.Read(ref _displaySettingsGeneration) ||
                _invalidated || _snapshot.Binding is not { NeedsConfirmation: false } binding) return;
            var topologyGeneration = Interlocked.Read(ref _topologyGeneration);
            checking = true;
            Publish(_snapshot with { Busy = true, Observation = Unknown(), ErrorCode = null });
            // Ordinary display notifications can validate the retained handles with a fresh
            // full topology capture. Resume still releases handles before rediscovery.
            var restartGeneration = Interlocked.Read(ref _workerRestartGeneration);
            if (restartGeneration != _completedWorkerRestartGeneration)
            {
                await _worker.StopAsync();
                _completedWorkerRestartGeneration = restartGeneration;
            }
            var reply = await _worker.SendAsync(new(Guid.NewGuid(), "discover"), cancellationToken);
            lock (_topologyGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || generation != Interlocked.Read(ref _displaySettingsGeneration) ||
                    topologyGeneration != Interlocked.Read(ref _topologyGeneration) || _invalidated) return;
                if (!reply.Success || reply.Discovery is not { } discovery)
                {
                    HandleError(reply.ErrorCode ?? "discovery_failed");
                    return;
                }
                if (discovery.TopologyFingerprint != binding.TopologyFingerprint || discovery.Candidates.Count != 1 ||
                    discovery.Candidates[0].TargetKey != binding.TargetKey || discovery.Candidates[0].Profile != binding.Profile)
                {
                    HandleError("topology_changed");
                    return;
                }
            }
            var reading = await _worker.SendAsync(new(Guid.NewGuid(), "read", binding.TargetKey, binding.TopologyFingerprint), cancellationToken);
            lock (_topologyGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || generation != Interlocked.Read(ref _displaySettingsGeneration) ||
                    topologyGeneration != Interlocked.Read(ref _topologyGeneration) || _invalidated) return;
                if (!reading.Success)
                {
                    HandleError(reading.ErrorCode ?? "read_failed");
                    return;
                }
                Publish(_snapshot with { Observation = reading.Observation ?? Unknown(), ErrorCode = null });
            }
        }
        catch (Exception exception) when (checking)
        {
            lock (_topologyGate)
            {
                if (generation == Interlocked.Read(ref _displaySettingsGeneration) && Volatile.Read(ref _disposed) == 0 && !_invalidated)
                    HandleError(exception is ControlException control ? control.Code : exception is OperationCanceledException ? "request_cancelled" : "worker_unavailable");
            }
        }
        finally
        {
            if (checking) Publish(_snapshot with { Busy = false });
            _commands.Release();
        }
    }
    public void NotifyTopologyChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_topologyGate)
        {
            Interlocked.Increment(ref _topologyGeneration);
            _invalidated = true;
        }
        try { Changed?.Invoke(); } catch { }
        _ = PersistInvalidationAsync();
    }
    private async Task PersistInvalidationAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) == 0 && _invalidated && _snapshot.Binding is { } binding)
            {
                _store.SaveBinding(binding with { NeedsConfirmation = true });
                Publish(_snapshot with { Observation = Unknown(), ErrorCode = "topology_changed" });
            }
        }
        finally { _commands.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _commands.WaitAsync();
        try
        {
            try
            {
                if (_invalidated && _snapshot.Binding is { } binding) _store.SaveBinding(binding with { NeedsConfirmation = true });
            }
            finally
            {
                try { await _worker.DisposeAsync(); }
                finally { _store.Dispose(); }
            }
        }
        finally { _commands.Release(); }
    }
}
