using System.Security.Cryptography;
using System.Text;
using PowerDisplay.Common.Models;
using Fy1Switch.Contracts;
using Fy1Switch.PowerDisplay;
using DisplayMonitor = PowerDisplay.Common.Models.Monitor;

namespace Fy1Switch.Worker;

internal static class TopologyPolicy
{
    public const string Profile = "sac2563-mtt1337-gdi-v1";

    public static string Fingerprint(TopologyDiagnosticReport topology) =>
        Hash(Profile + "\n" + WireJson.Serialize(topology));

    public static string TargetKey(string devicePath) => Hash(MonitorIdentity.FromDevicePath(devicePath).ToUpperInvariant());

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static void Validate(TopologyDiagnosticReport topology)
    {
        var targets = topology.Targets;
        if (targets.Count != 2 ||
            targets.Count(target => target.EdidModel.Equals("SAC2563", StringComparison.OrdinalIgnoreCase)) != 1 ||
            targets.Count(target => target.EdidModel.Equals("MTT1337", StringComparison.OrdinalIgnoreCase)) != 1 ||
            targets.Any(target => string.IsNullOrWhiteSpace(target.Source)) ||
            targets.Select(target => target.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
            targets.Select(target => target.TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
        {
            throw new DisplayProviderException("unsupported_topology", false);
        }

        var activeDevices = topology.GdiDevices.Where(device => (device.StateFlags & 1) != 0).OrderBy(device => device.Index).ToArray();
        if (activeDevices.Length != 2 || activeDevices[0].Index != 0 || activeDevices[1].Index != 1 ||
            activeDevices.Select(device => device.TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            activeDevices.Any(device => !targets.Any(target => target.TargetKey == device.TargetKey &&
                target.Source.Equals(device.Source, StringComparison.OrdinalIgnoreCase))))
        {
            throw new DisplayProviderException("unsupported_topology", false);
        }
    }

    public static DisplayMonitor SelectCandidate(TopologyDiagnosticReport topology, IReadOnlyList<DisplayMonitor> monitors)
    {
        Validate(topology);
        var target = topology.Targets.Single(target => target.EdidModel.Equals("SAC2563", StringComparison.OrdinalIgnoreCase));
        if (monitors.Count != 1 || monitors[0].CommunicationMethod != "DDC/CI" ||
            monitors[0].Handle == IntPtr.Zero || TargetKey(monitors[0].Id) != target.TargetKey ||
            !MonitorIdentity.EdidIdFromMonitorId(monitors[0].Id).Equals("SAC2563", StringComparison.OrdinalIgnoreCase))
        {
            throw new DisplayProviderException("target_not_unique_or_not_ddc", false);
        }

        if (!monitors[0].SupportsPowerState || monitors[0].SupportedPowerStates is not { } states ||
            !states.Contains(1) || !states.Contains(4))
        {
            throw new DisplayProviderException("power_unsupported", false);
        }

        return monitors[0];
    }

    public static PowerObservation Observe(PowerReadObservation reading) => new(
        !reading.Succeeded ? DisplayPower.Unknown : reading.Value switch
        {
            1 => DisplayPower.On,
            >= 2 and <= 5 => DisplayPower.Off,
            _ => DisplayPower.Unknown,
        }, reading.Value, reading.ObservedAt, "hardware_read");
}
