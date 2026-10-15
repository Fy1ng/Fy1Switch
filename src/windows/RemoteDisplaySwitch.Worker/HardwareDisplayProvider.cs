using System.Diagnostics;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.PowerDisplay;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace RemoteDisplaySwitch.Worker;

internal sealed class HardwareDisplayProvider : IDisplayProvider
{
    private readonly string dataDirectory;
    private readonly Func<IPowerDisplayBackend> createBackend;
    private readonly Func<string?> checkConflict;
    private IPowerDisplayBackend? backend;
    private FileStream? instanceLease;
    private DiscoverySnapshot? snapshot;
    private DisplayMonitor? monitor;
    private bool discoveryAttempted;
    private bool topologyInvalidated;

    public HardwareDisplayProvider(string dataDirectory, Func<IPowerDisplayBackend>? createBackend = null, Func<string?>? checkConflict = null)
    {
        this.dataDirectory = dataDirectory;
        this.createBackend = createBackend ?? (() => new DdcPowerDisplayBackend(dataDirectory));
        this.checkConflict = checkConflict ?? CheckProcessConflict;
    }

    public async Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard();
        if (snapshot is not null)
        {
            ValidateTopology(snapshot.TopologyFingerprint, false);
            return snapshot;
        }

        if (discoveryAttempted) throw new DisplayProviderException("discovery_restart_required", false);
        AcquireLease();
        Guard();
        ArchiveInterruptedDiscovery();
        backend ??= createBackend();
        var topology = backend.CaptureTopology();
        TopologyPolicy.Validate(topology);
        var fingerprint = TopologyPolicy.Fingerprint(topology);
        discoveryAttempted = true;
        var monitors = await backend.DiscoverAsync(topology, cancellationToken);
        if (TopologyPolicy.Fingerprint(backend.CaptureTopology()) != fingerprint)
        {
            topologyInvalidated = true;
            throw new DisplayProviderException("topology_changed", false);
        }

        monitor = TopologyPolicy.SelectCandidate(topology, monitors);
        snapshot = new(fingerprint, [new(TopologyPolicy.TargetKey(monitor.Id), monitor.Name, TopologyPolicy.Profile)], DateTimeOffset.UtcNow);
        return snapshot;
    }

    public async Task<PowerObservation> ReadAsync(string targetKey, string fingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(targetKey, fingerprint);
        PowerReadObservation reading;
        try
        {
            reading = await backend!.ReadAsync(monitor!, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            reading = new(false, null, null, DateTimeOffset.UtcNow);
        }

        ValidateTopology(fingerprint, false);
        return TopologyPolicy.Observe(reading);
    }

    public async Task<PowerWriteResult> SetAsync(string targetKey, string fingerprint, PowerIntent intent, CancellationToken cancellationToken)
    {
        if (intent is not (PowerIntent.On or PowerIntent.Off)) throw new DisplayProviderException("invalid_intent", false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(targetKey, fingerprint);
        bool? accepted = null;
        try
        {
            accepted = await backend!.WriteAsync(monitor!, intent == PowerIntent.On ? 1 : 4, cancellationToken);
            ValidateTopology(fingerprint, accepted);
            if (accepted == true) await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            PowerReadObservation reading;
            try
            {
                reading = await backend.ReadAsync(monitor!, cancellationToken);
                for (var retry = 0; accepted == true && !reading.Succeeded && retry < 2; retry++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                    ValidateTopology(fingerprint, accepted);
                    reading = await backend.ReadAsync(monitor!, cancellationToken);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                reading = new(false, null, null, DateTimeOffset.UtcNow);
            }

            ValidateTopology(fingerprint, accepted);
            return new(accepted.Value, TopologyPolicy.Observe(reading));
        }
        catch (DisplayProviderException exception)
        {
            throw new DisplayProviderException(exception.ErrorCode, accepted);
        }
        catch (Exception)
        {
            throw new DisplayProviderException("power_io_failed", accepted);
        }
    }

    private void ValidateTarget(string targetKey, string fingerprint)
    {
        Guard();
        if (snapshot is null || monitor is null) throw new DisplayProviderException("discovery_required", false);
        ValidateTopology(fingerprint, false);
        if (!targetKey.Equals(snapshot.Candidates[0].TargetKey, StringComparison.Ordinal))
            throw new DisplayProviderException("target_mismatch", false);
    }

    private void ValidateTopology(string fingerprint, bool? writeAccepted)
    {
        if (topologyInvalidated) throw new DisplayProviderException("topology_changed", writeAccepted);
        string current;
        try
        {
            current = TopologyPolicy.Fingerprint(backend!.CaptureTopology());
        }
        catch (Exception)
        {
            throw new DisplayProviderException("topology_unavailable", writeAccepted);
        }

        if (snapshot is null || current != snapshot.TopologyFingerprint)
        {
            topologyInvalidated = true;
            throw new DisplayProviderException("topology_changed", writeAccepted);
        }

        if (fingerprint != snapshot.TopologyFingerprint)
            throw new DisplayProviderException("topology_changed", writeAccepted);
    }

    private void Guard()
    {
        if (topologyInvalidated) throw new DisplayProviderException("topology_changed", false);
        if (checkConflict() is { } conflict) throw new DisplayProviderException(conflict, false);
    }

    private void ArchiveInterruptedDiscovery()
    {
        var lockPath = Path.Combine(dataDirectory, "discovery.lock");
        try
        {
            if (File.Exists(lockPath))
                File.Move(lockPath, $"{lockPath}.{Guid.NewGuid():N}.previous");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Archiving is diagnostic only; the instance lease and conflict checks own exclusion.
            SafeWorkerLog.Write("discovery_archive_failed");
        }
    }

    private void AcquireLease()
    {
        if (instanceLease is not null) return;
        Directory.CreateDirectory(dataDirectory);
        try
        {
            instanceLease = new FileStream(Path.Combine(dataDirectory, "worker.instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new DisplayProviderException("worker_already_running", false);
        }
    }

    private static string? CheckProcessConflict()
    {
        foreach (var name in new[] { "PowerToys.PowerDisplay", "RemoteDisplaySwitch.Probe", "RemoteDisplaySwitch.Worker" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Any(process => process.Id != Environment.ProcessId)) return "display_process_conflict";
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            backend?.Dispose();
        }
        finally
        {
            instanceLease?.Dispose();
        }
    }
}
