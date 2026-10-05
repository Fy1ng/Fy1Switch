using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Drivers;
using RemoteDisplaySwitch.PowerDisplay;

namespace RemoteDisplaySwitch.Probe.Tests;

[TestClass]
public class GdiOrderingExperimentTests
{
    private static readonly MonitorDisplayInfo Physical = new()
    {
        DevicePath = @"\\?\DISPLAY#SAC2563#physical-test#{guid}",
        FriendlyName = "G4Q",
        GdiDeviceName = @"\\.\DISPLAY1",
        MonitorNumber = 1,
    };
    private static readonly MonitorDisplayInfo Virtual = new()
    {
        DevicePath = @"\\?\DISPLAY#MTT1337#virtual-test#{guid}",
        FriendlyName = "VDD by MTT",
        GdiDeviceName = @"\\.\DISPLAY1",
        MonitorNumber = 2,
    };

    [TestMethod]
    public void ExperimentUsesGdiOrderWithoutRenamingTargets()
    {
        var result = GdiOrderingExperiment.Reorder([Physical, Virtual], ValidTopology());
        Assert.AreEqual(Virtual, result[0]);
        Assert.AreEqual(Physical, result[1]);
    }

    [TestMethod]
    public void ExtraTargetDoesNotGetSilentlyFiltered()
    {
        Assert.ThrowsException<InvalidOperationException>(() => GdiOrderingExperiment.Reorder([Physical, Virtual, Physical], ValidTopology()));
    }

    [TestMethod]
    public void UnknownModelDoesNotGetClassifiedAsVirtualByHdmiTechnology()
    {
        var topology = ValidTopology();
        var targets = topology.Targets.ToArray();
        targets[1] = targets[1] with { EdidModel = "OTHER123", OutputTechnology = 5 };
        Assert.ThrowsException<InvalidOperationException>(() => GdiOrderingExperiment.Reorder([Physical, Virtual], topology with { Targets = targets }));
    }

    [TestMethod]
    public void DuplicateDeviceKeyIsRejected()
    {
        var topology = ValidTopology();
        var devices = topology.GdiDevices.ToArray();
        devices[1] = devices[1] with { TargetKey = devices[0].TargetKey };
        Assert.ThrowsException<InvalidOperationException>(() => GdiOrderingExperiment.Reorder([Physical, Virtual], topology with { GdiDevices = devices }));
    }

    [TestMethod]
    public void MissingDeviceOrIndexGapIsRejected()
    {
        var topology = ValidTopology();
        Assert.ThrowsException<InvalidOperationException>(() => GdiOrderingExperiment.Reorder([Physical, Virtual], topology with { GdiDevices = [topology.GdiDevices[0]] }));
        var devices = topology.GdiDevices.ToArray();
        devices[1] = devices[1] with { Index = 2 };
        Assert.ThrowsException<InvalidOperationException>(() => GdiOrderingExperiment.Reorder([Physical, Virtual], topology with { GdiDevices = devices }));
    }

    [TestMethod]
    public void SourceChangeInvalidatesExperiment()
    {
        var topology = ValidTopology();
        var changed = Physical with { GdiDeviceName = @"\\.\DISPLAY2" };
        Assert.ThrowsException<InvalidOperationException>(() => GdiOrderingExperiment.Reorder([changed, Virtual], topology));
    }

    private static TopologyDiagnosticReport ValidTopology()
    {
        var physicalKey = TopologyDiagnostics.Key(Physical.DevicePath);
        var virtualKey = TopologyDiagnostics.Key(Virtual.DevicePath);
        return new(
            [new(0, physicalKey, "G4Q", "SAC2563", Physical.GdiDeviceName, 10, 8),
             new(1, virtualKey, "VDD by MTT", "MTT1337", Virtual.GdiDeviceName, 5, 0)],
            [new(Virtual.GdiDeviceName, 0, virtualKey, "Monitor0", "Generic PnP Monitor", 3),
             new(Physical.GdiDeviceName, 1, physicalKey, "Monitor1", "Generic PnP Monitor", 3)]);
    }
}
