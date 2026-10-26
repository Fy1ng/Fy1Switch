using System.Text.Json.Serialization;

namespace Fy1Switch.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<DisplayControl>))]
public enum DisplayControl { Brightness, Contrast, Volume, InputSource, ColorTemperature, Rotation }

public sealed record DisplayControlState(DisplayControl Control, bool Supported, int? Value, IReadOnlyList<int> SupportedValues);
public sealed record VcpCodeDescriptor(int Code, string Name, IReadOnlyList<int> SupportedValues);
public sealed record DisplayControlsSnapshot(string TargetKey, string MonitorName, string MonitorId,
    IReadOnlyList<DisplayControlState> Controls, IReadOnlyList<VcpCodeDescriptor> VcpCodes, DateTimeOffset ObservedAt);
public sealed record DisplayControlWriteResult(bool? WriteAccepted, int? Value, bool Confirmed, string? ErrorCode = null);

// These are presentation preferences. Hiding a monitor never revokes its binding or remote access.
public sealed record MonitorPreferences(bool ShowBrightness = true, bool ShowContrast = true, bool ShowVolume = false,
    bool ShowInputSource = false, bool ShowRotation = false, bool ShowColorTemperature = false,
    bool ShowPower = true, bool Hidden = false);
