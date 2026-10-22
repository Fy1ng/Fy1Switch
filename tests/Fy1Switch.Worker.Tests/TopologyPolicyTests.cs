using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.PowerDisplay;
using Fy1Switch.Worker;

namespace Fy1Switch.Worker.Tests;

[TestClass]
public sealed class TopologyPolicyTests
{
    [TestMethod]
    public void KeyMatchesUpstreamDefinitionWithGuidStrippedAndInvariantCase()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(@"\\?\DISPLAY#SAC2563#PHYSICAL-TEST")));
        Assert.AreEqual(expected, TopologyPolicy.TargetKey(FakeBackend.PhysicalId));
        Assert.AreEqual(expected, TopologyPolicy.TargetKey(FakeBackend.PhysicalId.ToLowerInvariant()));
    }

    [TestMethod]
    public void FingerprintCoversEntireOrderedReportAndFixedProfilePrefix()
    {
        var topology = FakeBackend.ValidTopology();
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TopologyPolicy.Profile + "\n" + WireJson.Serialize(topology))));
        Assert.AreEqual(expected, TopologyPolicy.Fingerprint(topology));
        var target = topology.Targets[0];
        var device = topology.GdiDevices[0];
        var targetChanges = new[]
        {
            target with { PathIndex = 3 }, target with { TargetKey = "other" }, target with { Name = "other" },
            target with { EdidModel = "other" }, target with { Source = "other" },
            target with { OutputTechnology = 99 }, target with { ConnectorInstance = 99 },
        };
        var deviceChanges = new[]
        {
            device with { Source = "other" }, device with { Index = 5 }, device with { TargetKey = "other" },
            device with { Name = "other" }, device with { Description = "other" }, device with { StateFlags = 99 },
        };
        foreach (var changed in targetChanges)
            Assert.AreNotEqual(expected, TopologyPolicy.Fingerprint(topology with { Targets = [changed, topology.Targets[1]] }));
        foreach (var changed in deviceChanges)
            Assert.AreNotEqual(expected, TopologyPolicy.Fingerprint(topology with { GdiDevices = [changed, topology.GdiDevices[1]] }));
        Assert.AreNotEqual(expected, TopologyPolicy.Fingerprint(topology with { Targets = topology.Targets.Reverse().ToArray() }));
        Assert.AreNotEqual(expected, TopologyPolicy.Fingerprint(topology with { GdiDevices = topology.GdiDevices.Reverse().ToArray() }));
        Assert.AreNotEqual(expected, TopologyPolicy.Fingerprint(topology with { GdiDevices = [.. topology.GdiDevices, device with { StateFlags = 0 }] }));
    }

    [TestMethod]
    [DataRow(1, true, DisplayPower.On)]
    [DataRow(2, true, DisplayPower.Off)]
    [DataRow(3, true, DisplayPower.Off)]
    [DataRow(4, true, DisplayPower.Off)]
    [DataRow(5, true, DisplayPower.Off)]
    [DataRow(0, true, DisplayPower.Unknown)]
    [DataRow(6, true, DisplayPower.Unknown)]
    [DataRow(-1, true, DisplayPower.Unknown)]
    [DataRow(1, false, DisplayPower.Unknown)]
    public void NativeObservationsNeverInventKnownPower(int raw, bool succeeded, DisplayPower expected)
    {
        var time = DateTimeOffset.UtcNow;
        var observation = TopologyPolicy.Observe(new(succeeded, raw, null, time));
        Assert.AreEqual(expected, observation.State);
        Assert.AreEqual(raw, observation.RawValue);
        Assert.AreEqual(time, observation.ObservedAt);
        Assert.AreEqual("hardware_read", observation.Source);
    }

    [TestMethod]
    public void UnknownOrAmbiguousTopologiesFailClosed()
    {
        var topology = FakeBackend.ValidTopology();
        var variants = new[]
        {
            topology with { Targets = [topology.Targets[0]] },
            topology with { Targets = [.. topology.Targets, topology.Targets[0]] },
            topology with { Targets = [topology.Targets[0], topology.Targets[1] with { EdidModel = "OTHER" }] },
            topology with { Targets = [topology.Targets[0], topology.Targets[1] with { Source = "other" }] },
            topology with { GdiDevices = [topology.GdiDevices[0]] },
            topology with { GdiDevices = [topology.GdiDevices[0], topology.GdiDevices[1] with { Index = 3 }] },
            topology with { GdiDevices = [topology.GdiDevices[0], topology.GdiDevices[1] with { TargetKey = topology.GdiDevices[0].TargetKey }] },
        };
        foreach (var variant in variants)
            Assert.AreEqual("unsupported_topology", Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.Validate(variant)).ErrorCode);
    }

    [TestMethod]
    public void UnknownCapabilitiesWrongIdentityAndMultipleCandidatesFailClosed()
    {
        var topology = FakeBackend.ValidTopology();
        Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.SelectCandidate(topology, []));
        Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.SelectCandidate(topology, [FakeBackend.ValidMonitor(), FakeBackend.ValidMonitor()]));
        var monitor = FakeBackend.ValidMonitor();
        monitor.Id = FakeBackend.VirtualId;
        Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.SelectCandidate(topology, [monitor]));
        monitor = FakeBackend.ValidMonitor();
        monitor.Handle = IntPtr.Zero;
        Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.SelectCandidate(topology, [monitor]));
        monitor = FakeBackend.ValidMonitor();
        monitor.CommunicationMethod = "WMI";
        Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.SelectCandidate(topology, [monitor]));
        monitor = FakeBackend.ValidMonitor();
        monitor.VcpCapabilitiesInfo = null;
        Assert.AreEqual("power_unsupported", Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.SelectCandidate(topology, [monitor])).ErrorCode);
        foreach (var states in new int[][] { [], [1], [4], [1, 5] })
        {
            monitor = FakeBackend.ValidMonitor();
            monitor.VcpCapabilitiesInfo!.SupportedVcpCodes[0xD6] = new(0xD6, "Power", states);
            Assert.AreEqual("power_unsupported", Assert.ThrowsException<DisplayProviderException>(() => TopologyPolicy.SelectCandidate(topology, [monitor])).ErrorCode);
        }
    }
}
