using PowerDisplay.Common;
using PowerDisplay.Common.Drivers;
using PowerDisplay.Common.Drivers.DDC;
using PowerDisplay.Common.Services;
using PowerDisplay.Models;
using Fy1Switch.PowerDisplay;
using Fy1Switch.Contracts;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace Fy1Switch.Worker;

internal interface IPowerDisplayBackend : IDisposable
{
    TopologyDiagnosticReport CaptureTopology();
    Task<IReadOnlyList<DisplayMonitor>> DiscoverAsync(TopologyDiagnosticReport topology, CancellationToken cancellationToken);
    Task<PowerReadObservation> ReadAsync(DisplayMonitor monitor, CancellationToken cancellationToken);
    Task<bool> WriteAsync(DisplayMonitor monitor, int value, CancellationToken cancellationToken);
    Task<int?> ReadControlAsync(DisplayMonitor monitor, DisplayControl control, CancellationToken cancellationToken);
    Task<bool> WriteControlAsync(DisplayMonitor monitor, DisplayControl control, int value, CancellationToken cancellationToken);
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

    public async Task<int?> ReadControlAsync(DisplayMonitor monitor, DisplayControl control, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (control == DisplayControl.Rotation)
        {
            var orientation = await Task.Run(() => new DisplayRotationService().GetCurrentOrientation(monitor.GdiDeviceName), cancellationToken);
            return orientation is >= 0 and <= 3 ? orientation : null;
        }
        var reading = await (control switch
        {
            DisplayControl.Brightness => controller!.GetBrightnessAsync(monitor, cancellationToken),
            DisplayControl.Contrast => controller!.GetContrastAsync(monitor, cancellationToken),
            DisplayControl.Volume => controller!.GetVolumeAsync(monitor, cancellationToken),
            DisplayControl.ColorTemperature => controller!.GetColorTemperatureAsync(monitor, cancellationToken),
            DisplayControl.InputSource => controller!.GetInputSourceAsync(monitor, cancellationToken),
            _ => throw new DisplayProviderException("invalid_control", false),
        });
        // Discrete VCP reads may report a zero maximum. Current=-1 is the upstream
        // failure sentinel; a discrete value does not use the continuous range rules.
        if (!DisplayControlPolicy.IsContinuous(control)) return reading.Current >= 0 ? reading.Current : null;
        if (!reading.IsValid) return null;
        switch (control)
        {
            case DisplayControl.Brightness: monitor.BrightnessVcpMax = reading.Maximum; break;
            case DisplayControl.Contrast: monitor.ContrastVcpMax = reading.Maximum; break;
            case DisplayControl.Volume: monitor.VolumeVcpMax = reading.Maximum; break;
        }
        return reading.ToPercentage();
    }

    public async Task<bool> WriteControlAsync(DisplayMonitor monitor, DisplayControl control, int value, CancellationToken cancellationToken)
    {
        DisplayControlPolicy.Validate(monitor, control, value);
        var result = control switch
        {
            DisplayControl.Brightness => await controller!.SetBrightnessAsync(monitor, value, cancellationToken),
            DisplayControl.Contrast => await controller!.SetContrastAsync(monitor, value, cancellationToken),
            DisplayControl.Volume => await controller!.SetVolumeAsync(monitor, value, cancellationToken),
            DisplayControl.ColorTemperature => await controller!.SetColorTemperatureAsync(monitor, value, cancellationToken),
            DisplayControl.InputSource => await controller!.SetInputSourceAsync(monitor, value, cancellationToken),
            DisplayControl.Rotation => await Task.Run(() => new DisplayRotationService().SetRotation(monitor, value), cancellationToken),
            _ => throw new DisplayProviderException("invalid_control", false),
        };
        return result.IsSuccess;
    }

    public void Dispose() => controller?.Dispose();
}
