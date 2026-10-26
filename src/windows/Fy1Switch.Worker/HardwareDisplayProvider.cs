using System.Diagnostics;
using Fy1Switch.Contracts;
using Fy1Switch.PowerDisplay;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace Fy1Switch.Worker;

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

    public async Task<DisplayControlsSnapshot> ReadControlsAsync(string targetKey, string fingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(targetKey, fingerprint);
        var controls = new List<DisplayControlState>();
        foreach (var control in Enum.GetValues<DisplayControl>())
        {
            var supported = DisplayControlPolicy.Supports(monitor!, control);
            int? value = null;
            if (supported)
            {
                ValidateTarget(targetKey, fingerprint);
                try { value = await backend!.ReadControlAsync(monitor!, control, cancellationToken); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                ValidateTopology(fingerprint, false);
            }
            controls.Add(new(control, supported, value, DisplayControlPolicy.Values(monitor!, control)));
        }
        ValidateTopology(fingerprint, false);
        return new(targetKey, monitor!.Name, monitor.Id, controls, DisplayControlPolicy.VcpCodes(monitor), DateTimeOffset.UtcNow);
    }

    public async Task<DisplayControlWriteResult> SetControlAsync(string targetKey, string fingerprint, DisplayControl control, int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(targetKey, fingerprint);
        DisplayControlPolicy.Validate(monitor!, control, value);
        bool? accepted = false;
        try
        {
            // A fresh continuous read supplies the device's actual maximum for the
            // upstream percentage-to-raw conversion, including monitors with max=50.
            if (DisplayControlPolicy.IsContinuous(control))
            {
                var current = await backend!.ReadControlAsync(monitor!, control, cancellationToken);
                ValidateTarget(targetKey, fingerprint);
                if (current is null) return new(false, null, false, "control_read_failed");
            }
            var expected = DisplayControlPolicy.ExpectedReadback(monitor!, control, value);
            accepted = null;
            accepted = await backend!.WriteControlAsync(monitor!, control, value, cancellationToken);
            ValidateTopology(fingerprint, accepted);
            if (!accepted.Value) return new(false, null, false, "control_write_failed");
            if (control != DisplayControl.Rotation) await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            int? readback = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ValidateTopology(fingerprint, accepted);
                try { readback = await backend.ReadControlAsync(monitor!, control, cancellationToken); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                ValidateTopology(fingerprint, accepted);
                if (readback is not null) break;
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
            var confirmed = readback == expected;
            return new(true, readback, confirmed, confirmed ? null : readback is null ? "control_read_failed" : "control_not_confirmed");
        }
        catch (DisplayProviderException exception)
        {
            throw new DisplayProviderException(exception.ErrorCode, accepted);
        }
        catch (Exception)
        {
            throw new DisplayProviderException("control_io_failed", accepted);
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
        foreach (var name in new[] { "PowerToys.PowerDisplay", "Fy1Switch.Probe", "Fy1Switch.Worker" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Any(process => process.Id != Environment.ProcessId))
                {
                    SafeWorkerLog.Write("display_process_conflict process=" + name);
                    return "display_process_conflict";
                }
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
