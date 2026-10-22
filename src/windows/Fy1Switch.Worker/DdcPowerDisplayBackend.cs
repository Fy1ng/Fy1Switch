using PowerDisplay.Common;
using PowerDisplay.Common.Drivers;
using PowerDisplay.Common.Drivers.DDC;
using PowerDisplay.Common.Services;
using PowerDisplay.Models;
using Fy1Switch.PowerDisplay;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace Fy1Switch.Worker;

internal interface IPowerDisplayBackend : IDisposable
{
    TopologyDiagnosticReport CaptureTopology();
    Task<IReadOnlyList<DisplayMonitor>> DiscoverAsync(TopologyDiagnosticReport topology, CancellationToken cancellationToken);
    Task<PowerReadObservation> ReadAsync(DisplayMonitor monitor, CancellationToken cancellationToken);
    Task<bool> WriteAsync(DisplayMonitor monitor, int value, CancellationToken cancellationToken);
}

internal sealed class DdcPowerDisplayBackend : IPowerDisplayBackend
{
    private DdcCiController? controller;

    public DdcPowerDisplayBackend(string dataDirectory) => PathConstants.Configure(dataDirectory);

    public TopologyDiagnosticReport CaptureTopology() => TopologyDiagnostics.Capture();

    public async Task<IReadOnlyList<DisplayMonitor>> DiscoverAsync(TopologyDiagnosticReport topology, CancellationToken cancellationToken)
    {
        if (BuiltInMonitorBlacklist.Entries.Count == 0) throw new DisplayProviderException("blacklist_resource_unavailable", false);
        var inventory = DisplayConfigInventory.GetAllMonitorDisplayInfo();
        IReadOnlyList<MonitorDisplayInfo> ordered;
        try
        {
            ordered = GdiOrderingExperiment.Reorder(inventory.Values.ToArray(), topology);
        }
        catch (InvalidOperationException)
        {
            throw new DisplayProviderException("unsupported_topology", false);
        }

        if (TopologyPolicy.Fingerprint(CaptureTopology()) != TopologyPolicy.Fingerprint(topology))
            throw new DisplayProviderException("topology_changed", false);
        controller ??= new DdcCiController(new MemoryVcpStore());
        return (await controller.DiscoverMonitorsAsync(ordered, cancellationToken)).ToArray();
    }

    public Task<PowerReadObservation> ReadAsync(DisplayMonitor monitor, CancellationToken cancellationToken) =>
        controller!.ReadPowerObservationAsync(monitor, cancellationToken);

    public async Task<bool> WriteAsync(DisplayMonitor monitor, int value, CancellationToken cancellationToken)
    {
        if (value is not (1 or 4)) throw new DisplayProviderException("invalid_intent", false);
        return (await controller!.SetPowerStateAsync(monitor, value, cancellationToken)).IsSuccess;
    }

    public void Dispose() => controller?.Dispose();
}
