using System;
using System.Collections.Generic;
using System.Linq;
using PowerDisplay.Common.Drivers;
using PowerDisplay.Common.Services;

namespace RemoteDisplaySwitch.PowerDisplay;

public static class GdiOrderingExperiment
{
    public static IReadOnlyList<MonitorDisplayInfo> Reorder(
        IReadOnlyCollection<MonitorDisplayInfo> inventory,
        TopologyDiagnosticReport topology)
    {
        var targets = topology.Targets;
        if (inventory.Count != 2 || targets.Count != 2 ||
            targets.Count(target => target.EdidModel.Equals("SAC2563", StringComparison.OrdinalIgnoreCase)) != 1 ||
            targets.Count(target => target.EdidModel.Equals("MTT1337", StringComparison.OrdinalIgnoreCase)) != 1 ||
            targets.Select(target => target.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
            targets.Select(target => target.TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
        {
            throw new InvalidOperationException("The ordering experiment only supports the explicit G4Q plus MTT VDD clone configuration.");
        }

        var devices = topology.GdiDevices.Where(device => (device.StateFlags & 1) != 0).OrderBy(device => device.Index).ToArray();
        if (devices.Length != 2 || devices[0].Index != 0 || devices[1].Index != 1 ||
            devices.Select(device => device.TargetKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            devices.Any(device => !targets.Any(target => target.TargetKey.Equals(device.TargetKey, StringComparison.OrdinalIgnoreCase) && target.Source.Equals(device.Source, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("GDI monitor devices do not uniquely match the active CCD target set.");
        }

        var byKey = inventory.ToDictionary(display => TopologyDiagnostics.Key(display.DevicePath), StringComparer.OrdinalIgnoreCase);
        if (targets.Any(target => !byKey.TryGetValue(target.TargetKey, out var display) || !display.GdiDeviceName.Equals(target.Source, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The topology changed while preparing the experiment.");
        }

        var blacklist = new MonitorBlacklistService();
        var ordered = devices.Select(device => byKey[device.TargetKey]).ToArray();
        if (ordered.Any(display => blacklist.IsBlocked(display.DevicePath)))
        {
            throw new InvalidOperationException("A target is blacklisted; no DDC experiment is permitted.");
        }

        return ordered;
    }
}
