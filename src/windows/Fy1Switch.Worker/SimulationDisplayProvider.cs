using Fy1Switch.Contracts;
using PowerDisplay.Common.Models;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace Fy1Switch.Worker;

internal sealed class SimulationDisplayProvider : IDisplayProvider
{
    public static string TargetKey { get; } = new('a', 64);
    public static string Fingerprint { get; } = new('b', 64);
    private DisplayPower state = DisplayPower.On;
    private bool discovered;
    private readonly Dictionary<DisplayControl, int> controlValues = new()
    {
        [DisplayControl.Brightness] = 65, [DisplayControl.Contrast] = 50,
        [DisplayControl.InputSource] = 0x11, [DisplayControl.ColorTemperature] = 0x05,
        [DisplayControl.Rotation] = 0,
    };
    private readonly DisplayMonitor controlsMonitor = new()
    {
        Id = "simulation-monitor", Name = "模拟显示器", GdiDeviceName = "simulation-source",
        Capabilities = MonitorCapabilities.Brightness | MonitorCapabilities.Contrast | MonitorCapabilities.DdcCi,
        SupportsColorTemperature = true,
        VcpCapabilitiesInfo = new()
        {
            SupportedVcpCodes = new()
            {
                [0x10] = new(0x10, "Brightness"), [0x12] = new(0x12, "Contrast"),
                [0x14] = new(0x14, "Select Color Preset", [1, 2, 4, 5, 6, 8, 11]),
                [0x60] = new(0x60, "Input Source", [1, 3, 4, 15, 16, 17, 18]),
                [0xD6] = new(0xD6, "Power Mode", [1, 4, 5]),
            },
        },
    };

    public Task<DiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        discovered = true;
        return Task.FromResult(new DiscoverySnapshot(Fingerprint,
            [new(TargetKey, "模拟显示器", "simulation")], DateTimeOffset.UtcNow));
    }

    public Task<PowerObservation> ReadAsync(string targetKey, string fingerprint, CancellationToken cancellationToken)
    {
        Validate(targetKey, fingerprint, cancellationToken);
        return Task.FromResult(Observe());
    }

    public Task<PowerWriteResult> SetAsync(string targetKey, string fingerprint, PowerIntent intent, CancellationToken cancellationToken)
    {
        Validate(targetKey, fingerprint, cancellationToken);
        state = intent switch
        {
            PowerIntent.On => DisplayPower.On,
            PowerIntent.Off => DisplayPower.Off,
            _ => throw new DisplayProviderException("invalid_intent", false),
        };
        return Task.FromResult(new PowerWriteResult(true, Observe()));
    }

    public Task<DisplayControlsSnapshot> ReadControlsAsync(string targetKey, string fingerprint, CancellationToken cancellationToken)
    {
        Validate(targetKey, fingerprint, cancellationToken);
        var controls = Enum.GetValues<DisplayControl>().Select(control => new DisplayControlState(control,
            DisplayControlPolicy.Supports(controlsMonitor, control), controlValues.TryGetValue(control, out var value) ? value : null,
            DisplayControlPolicy.Values(controlsMonitor, control))).ToArray();
        return Task.FromResult(new DisplayControlsSnapshot(TargetKey, controlsMonitor.Name, controlsMonitor.Id,
            controls, DisplayControlPolicy.VcpCodes(controlsMonitor), DateTimeOffset.UtcNow));
    }

    public Task<DisplayControlWriteResult> SetControlAsync(string targetKey, string fingerprint, DisplayControl control, int value, CancellationToken cancellationToken)
    {
        Validate(targetKey, fingerprint, cancellationToken);
        DisplayControlPolicy.Validate(controlsMonitor, control, value);
        controlValues[control] = value;
        return Task.FromResult(new DisplayControlWriteResult(true, value, true));
    }

    private void Validate(string targetKey, string fingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!discovered) throw new DisplayProviderException("discovery_required", false);
        if (fingerprint != Fingerprint) throw new DisplayProviderException("topology_changed", false);
        if (targetKey != TargetKey) throw new DisplayProviderException("target_mismatch", false);
    }

    private PowerObservation Observe() => new(state, state == DisplayPower.On ? 1 : 4, DateTimeOffset.UtcNow, "simulation");

    public void Dispose() { }
}
