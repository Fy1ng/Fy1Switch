using Fy1Switch.Contracts;
using PowerDisplay.Common.Models;
using PowerDisplay.Common.Utils;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace Fy1Switch.Worker;

internal static class DisplayControlPolicy
{
    public static bool IsContinuous(DisplayControl control) =>
        control is DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume;

    public static bool Supports(DisplayMonitor monitor, DisplayControl control) => control switch
    {
        DisplayControl.Brightness => monitor.SupportsBrightness,
        DisplayControl.Contrast => monitor.SupportsContrast,
        DisplayControl.Volume => monitor.SupportsVolume,
        DisplayControl.InputSource => monitor.SupportsInputSource && Values(monitor, control).Count > 0,
        DisplayControl.ColorTemperature => monitor.SupportsColorTemperature && Values(monitor, control).Count > 0,
        DisplayControl.Rotation => !string.IsNullOrWhiteSpace(monitor.GdiDeviceName),
        _ => false,
    };

    public static IReadOnlyList<int> Values(DisplayMonitor monitor, DisplayControl control) => control switch
    {
        DisplayControl.InputSource => monitor.SupportedInputSources?.ToArray() ?? [],
        DisplayControl.ColorTemperature => monitor.VcpCapabilitiesInfo?.GetSupportedValues(0x14)?.ToArray() ?? [],
        DisplayControl.Rotation => [0, 1, 2, 3],
        _ => [],
    };

    public static void Validate(DisplayMonitor monitor, DisplayControl control, int value)
    {
        if (!Enum.IsDefined(control)) throw new DisplayProviderException("invalid_control", false);
        if (!Supports(monitor, control)) throw new DisplayProviderException("control_unsupported", false);
        if (IsContinuous(control) ? value is < 0 or > 100 : !Values(monitor, control).Contains(value))
            throw new DisplayProviderException("invalid_control_value", false);
    }

    public static int ExpectedReadback(DisplayMonitor monitor, DisplayControl control, int value)
    {
        if (!IsContinuous(control)) return value;
        var maximum = control switch
        {
            DisplayControl.Brightness => monitor.BrightnessVcpMax,
            DisplayControl.Contrast => monitor.ContrastVcpMax,
            _ => monitor.VolumeVcpMax,
        };
        return new VcpFeatureValue(VcpFeatureValue.FromPercentage(value, maximum), maximum).ToPercentage();
    }

    public static IReadOnlyList<VcpCodeDescriptor> VcpCodes(DisplayMonitor monitor) =>
        monitor.VcpCapabilitiesInfo?.GetSortedVcpCodes()
            .Select(code => new VcpCodeDescriptor(code.Code, VcpNames.GetCodeName(code.Code), code.SupportedValues.ToArray()))
            .ToArray() ?? [];
}
