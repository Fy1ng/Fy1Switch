using Fy1Switch.Contracts;
using Fy1Switch.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Models;

namespace Fy1Switch.Worker.Tests;

[TestClass]
public sealed class DisplayControlTests
{
    private static FakeBackend ControlsBackend()
    {
        var backend = new FakeBackend();
        var monitor = backend.Monitors.Single();
        monitor.Capabilities = MonitorCapabilities.Brightness | MonitorCapabilities.Contrast | MonitorCapabilities.DdcCi;
        monitor.SupportsColorTemperature = true;
        monitor.GdiDeviceName = "fixture-source";
        monitor.VcpCapabilitiesInfo!.SupportedVcpCodes[0x10] = new(0x10, "Brightness");
        monitor.VcpCapabilitiesInfo.SupportedVcpCodes[0x12] = new(0x12, "Contrast");
        monitor.VcpCapabilitiesInfo.SupportedVcpCodes[0x14] = new(0x14, "Color", [1, 5]);
        monitor.VcpCapabilitiesInfo.SupportedVcpCodes[0x60] = new(0x60, "Input", [15, 17]);
        return backend;
    }

    private static WorkerRequest Request(string command, string fingerprint, DisplayControl control = DisplayControl.Contrast, int value = 75) =>
        new(Guid.NewGuid(), command, FakeBackend.TargetKey, fingerprint, Control: control, Value: value);

    [TestMethod]
    public async Task ReadsActualCapabilitiesValuesAndVcpDescriptorsWithoutPowerWrites()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("read-controls", discovery.TopologyFingerprint));
        Assert.IsTrue(reply.Success, reply.ErrorCode);
        var snapshot = reply.Controls!;
        Assert.AreEqual(FakeBackend.TargetKey, snapshot.TargetKey);
        Assert.AreEqual(FakeBackend.PhysicalId, snapshot.MonitorId);
        Assert.AreEqual(6, snapshot.Controls.Count);
        var contrast = snapshot.Controls.Single(item => item.Control == DisplayControl.Contrast);
        Assert.IsTrue(contrast.Supported);
        Assert.AreEqual(50, contrast.Value);
        Assert.IsFalse(snapshot.Controls.Single(item => item.Control == DisplayControl.Volume).Supported);
        Assert.IsFalse(backend.ControlReads.Contains(DisplayControl.Volume));
        CollectionAssert.AreEqual(new[] { 15, 17 }, snapshot.VcpCodes.Single(item => item.Code == 0x60).SupportedValues.ToArray());
        CollectionAssert.AreEqual(new[] { 0x10, 0x12, 0x14, 0x60, 0xD6 }, snapshot.VcpCodes.Select(item => item.Code).ToArray());
        Assert.AreEqual(0, backend.Writes.Count);
        Assert.AreEqual(0, backend.ControlWrites.Count);
    }

    [TestMethod]
    [DataRow(DisplayControl.Contrast, 75)]
    [DataRow(DisplayControl.Brightness, 0)]
    [DataRow(DisplayControl.InputSource, 15)]
    [DataRow(DisplayControl.ColorTemperature, 1)]
    [DataRow(DisplayControl.Rotation, 3)]
    public async Task ExplicitWriteHappensOnceAndIsConfirmedByIndependentReadback(DisplayControl control, int value)
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint, control, value));
        Assert.IsTrue(reply.Success, reply.ErrorCode);
        Assert.AreEqual(true, reply.ControlWrite!.WriteAccepted);
        Assert.IsTrue(reply.ControlWrite.Confirmed);
        Assert.AreEqual(value, reply.ControlWrite.Value);
        Assert.AreEqual((control, value), backend.ControlWrites.Single());
        Assert.IsTrue(backend.ControlReads.Contains(control));
        Assert.AreEqual(0, backend.Writes.Count);
    }

    [TestMethod]
    [DataRow(DisplayControl.Contrast, -1, "invalid_control_value")]
    [DataRow(DisplayControl.Brightness, 101, "invalid_control_value")]
    [DataRow(DisplayControl.Volume, 50, "control_unsupported")]
    [DataRow(DisplayControl.InputSource, 18, "invalid_control_value")]
    [DataRow(DisplayControl.ColorTemperature, 6, "invalid_control_value")]
    [DataRow(DisplayControl.Rotation, 4, "invalid_control_value")]
    public async Task UnsupportedAndOutOfRangeRequestsNeverWrite(DisplayControl control, int value, string error)
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint, control, value));
        Assert.IsFalse(reply.Success);
        Assert.AreEqual(error, reply.ErrorCode);
        Assert.AreEqual(false, reply.ControlWrite!.WriteAccepted);
        Assert.AreEqual(0, backend.ControlWrites.Count);
    }

    [TestMethod]
    public async Task MissingDiscreteValuesAreNotGuessed()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        backend.Monitors.Single().VcpCapabilitiesInfo!.SupportedVcpCodes[0x60] = new(0x60, "Input");
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint, DisplayControl.InputSource, 17));
        Assert.AreEqual("control_unsupported", reply.ErrorCode);
        Assert.AreEqual(0, backend.ControlWrites.Count);
    }

    [TestMethod]
    public async Task InvalidControlEnvelopeNeverReachesProvider()
    {
        using var provider = new FakeProvider();
        var protocol = new WorkerProtocol(provider);
        var request = Request("set-control", SimulationDisplayProvider.Fingerprint);
        foreach (var invalid in new[] { request with { Control = null }, request with { Value = null }, request with { Control = (DisplayControl)99 }, request with { TargetKey = "bad" } })
        {
            var reply = await protocol.HandleAsync(invalid);
            Assert.AreEqual("invalid_request", reply.ErrorCode);
            Assert.AreEqual(false, reply.ControlWrite!.WriteAccepted);
        }
        Assert.AreEqual(0, provider.Calls);
    }

    [TestMethod]
    public async Task FailedRangeReadPreventsContinuousWrite()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        backend.ControlValues[DisplayControl.Contrast] = null;
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint));
        Assert.AreEqual("control_read_failed", reply.ErrorCode);
        Assert.AreEqual(false, reply.ControlWrite!.WriteAccepted);
        Assert.AreEqual(0, backend.ControlWrites.Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReadbackFailurePreservesAcceptanceWithoutReplaying(bool throwRead)
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        backend.OnControlWrite = () =>
        {
            backend.ApplyControlWrite = false;
            backend.ControlValues[DisplayControl.Contrast] = null;
            if (throwRead) backend.OnControlRead = () => throw new IOException("simulated read failure");
        };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint));
        Assert.IsTrue(reply.Success);
        Assert.AreEqual(true, reply.ControlWrite!.WriteAccepted);
        Assert.IsFalse(reply.ControlWrite.Confirmed);
        Assert.IsNull(reply.ControlWrite.Value);
        Assert.AreEqual("control_read_failed", reply.ErrorCode);
        Assert.AreEqual(1, backend.ControlWrites.Count);
        Assert.AreEqual(4, backend.ControlReads.Count);
    }

    [TestMethod]
    public async Task MismatchedReadbackDoesNotConfirmSuccessfulWrite()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        backend.ApplyControlWrite = false;
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint));
        Assert.AreEqual(true, reply.ControlWrite!.WriteAccepted);
        Assert.AreEqual(50, reply.ControlWrite.Value);
        Assert.IsFalse(reply.ControlWrite.Confirmed);
        Assert.AreEqual("control_not_confirmed", reply.ErrorCode);
        Assert.AreEqual(1, backend.ControlWrites.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TopologyChangeBeforeOrAfterWritePreservesBoundary(bool afterWrite)
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        if (afterWrite) backend.OnControlWrite = backend.ChangeTopology;
        else backend.ChangeTopology();
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint, DisplayControl.Rotation, 1));
        Assert.IsFalse(reply.Success);
        Assert.AreEqual("topology_changed", reply.ErrorCode);
        Assert.AreEqual(afterWrite, reply.ControlWrite!.WriteAccepted);
        Assert.IsFalse(reply.ControlWrite.Confirmed);
        Assert.AreEqual(afterWrite ? 1 : 0, backend.ControlWrites.Count);
    }

    [TestMethod]
    public async Task TopologyChangeDuringControlReadDiscardsSnapshot()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        backend.OnControlRead = backend.ChangeTopology;
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("read-controls", discovery.TopologyFingerprint));
        Assert.AreEqual("topology_changed", reply.ErrorCode);
        Assert.IsNull(reply.Controls);
        Assert.AreEqual(1, backend.ControlReads.Count);
    }

    [TestMethod]
    public async Task WriteExceptionHasUnknownAcceptance()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        backend.OnControlWrite = () => throw new IOException("simulated write failure");
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint));
        Assert.AreEqual("control_io_failed", reply.ErrorCode);
        Assert.IsNull(reply.ControlWrite!.WriteAccepted);
        Assert.IsFalse(reply.ControlWrite.Confirmed);
        Assert.AreEqual(1, backend.ControlWrites.Count);
    }

    [TestMethod]
    public async Task RejectedWriteIsNotRetriedOrReportedAsAccepted()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        backend.WriteAccepted = false;
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint));
        Assert.IsFalse(reply.Success);
        Assert.AreEqual(false, reply.ControlWrite!.WriteAccepted);
        Assert.AreEqual("control_write_failed", reply.ErrorCode);
        Assert.AreEqual(1, backend.ControlWrites.Count);
        Assert.AreEqual(1, backend.ControlReads.Count);
    }

    [TestMethod]
    public async Task DeviceRangeQuantizationIsConfirmedUsingActualReadback()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        backend.Monitors.Single().ContrastVcpMax = 50;
        backend.ApplyControlWrite = false;
        // 75 percent maps to raw 38/50, which reads back as 76 percent.
        backend.OnControlWrite = () => backend.ControlValues[DisplayControl.Contrast] = 76;
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint));
        Assert.IsTrue(reply.ControlWrite!.Confirmed);
        Assert.AreEqual(76, reply.ControlWrite.Value);
        Assert.IsNull(reply.ErrorCode);
    }

    [TestMethod]
    public async Task NewControlsRequireExactTargetAndNoConflictingProcess()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        string? conflict = null;
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => conflict);
        var discovery = await provider.DiscoverAsync(default);
        var protocol = new WorkerProtocol(provider);
        var request = Request("set-control", discovery.TopologyFingerprint);
        var wrongTarget = await protocol.HandleAsync(request with { TargetKey = new string('c', 64) });
        Assert.AreEqual("target_mismatch", wrongTarget.ErrorCode);
        conflict = "display_process_conflict";
        Assert.AreEqual(conflict, (await protocol.HandleAsync(request)).ErrorCode);
        Assert.AreEqual(conflict, (await protocol.HandleAsync(request with { Command = "read-controls" })).ErrorCode);
        Assert.AreEqual(0, backend.ControlWrites.Count);
        Assert.AreEqual(0, backend.ControlReads.Count);
    }

    [TestMethod]
    public async Task TopologyChangeWhileReadingContinuousRangePreventsWrite()
    {
        using var directory = new TestDirectory();
        var backend = ControlsBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(default);
        backend.OnControlRead = backend.ChangeTopology;
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set-control", discovery.TopologyFingerprint));
        Assert.AreEqual("topology_changed", reply.ErrorCode);
        Assert.AreEqual(false, reply.ControlWrite!.WriteAccepted);
        Assert.AreEqual(0, backend.ControlWrites.Count);
    }

    [TestMethod]
    public async Task SimulatedControlsRoundTripWithoutHardwareAndVolumeIsUnavailable()
    {
        using var provider = new SimulationDisplayProvider();
        var protocol = new WorkerProtocol(provider);
        await protocol.HandleAsync(ProtocolFixture.Request("discover"));
        var reply = await protocol.HandleAsync(ProtocolFixture.Request("read-controls"));
        Assert.IsTrue(reply.Success);
        Assert.IsFalse(reply.Controls!.Controls.Single(item => item.Control == DisplayControl.Volume).Supported);
        var changed = await protocol.HandleAsync(ProtocolFixture.Request("set-control") with { Control = DisplayControl.Contrast, Value = 83 });
        Assert.IsTrue(changed.ControlWrite!.Confirmed);
        var readback = await protocol.HandleAsync(ProtocolFixture.Request("read-controls"));
        Assert.AreEqual(83, readback.Controls!.Controls.Single(item => item.Control == DisplayControl.Contrast).Value);
        Assert.AreEqual("control_unsupported", (await protocol.HandleAsync(ProtocolFixture.Request("set-control") with { Control = DisplayControl.Volume, Value = 50 })).ErrorCode);
        Assert.AreEqual(DisplayPower.On, (await protocol.HandleAsync(ProtocolFixture.Request("read"))).Observation!.State);
    }
}
