using Fy1Switch.Contracts;
using Fy1Switch.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Runtime.Tests;

[TestClass]
public sealed class DisplayControlTests
{
    private static DisplayControlsSnapshot Controls(string targetKey = FakeWorkerTransport.TargetKey) => new(targetKey,
        "Test monitor", "test-monitor-id", [new(DisplayControl.Contrast, true, 55, [])],
        [new(0x12, "Contrast", [])], DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task ControlsReadUsesConfirmedBindingAndDoesNotWriteOrInventPower()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var controls = Controls();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, true, Controls: controls));
        var count = fixture.Worker.Requests.Length;
        Assert.AreEqual(controls, await fixture.Controller.GetDisplayControlsAsync(binding.TargetId));
        var request = fixture.Worker.Requests.Skip(count).Single();
        Assert.AreEqual("read-controls", request.Command);
        Assert.AreEqual(binding.TargetKey, request.TargetKey);
        Assert.AreEqual(binding.TopologyFingerprint, request.TopologyFingerprint);
        Assert.AreEqual(DisplayPower.Unknown, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
    }

    [TestMethod]
    public async Task ControlsForWrongTargetCannotReadOrWrite()
    {
        await using var fixture = new ControllerFixture();
        await fixture.BindAsync();
        var count = fixture.Worker.Requests.Length;
        var readError = await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.GetDisplayControlsAsync(Guid.NewGuid()));
        var writeError = await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.SetDisplayControlAsync(Guid.NewGuid(), DisplayControl.Contrast, 50));
        Assert.AreEqual("target_changed", readError.Code);
        Assert.AreEqual("target_changed", writeError.Code);
        Assert.AreEqual(count, fixture.Worker.Requests.Length);
    }

    [TestMethod]
    public async Task ControlsReplyForDifferentTargetIsRejected()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, true, Controls: Controls("different-target")));
        var error = await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.GetDisplayControlsAsync(binding.TargetId));
        Assert.AreEqual("worker_protocol_error", error.Code);
    }

    [TestMethod]
    public async Task FeatureFailureDoesNotDiscardKnownPower()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        await fixture.Controller.RefreshAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, false, "controls_read_failed"));
        await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.GetDisplayControlsAsync(binding.TargetId));
        Assert.AreEqual(DisplayPower.On, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
    }

    [DataTestMethod]
    [DataRow(DisplayControl.Brightness, -1)]
    [DataRow(DisplayControl.Contrast, 101)]
    [DataRow(DisplayControl.Volume, 500)]
    [DataRow(DisplayControl.Rotation, 4)]
    [DataRow(DisplayControl.InputSource, 256)]
    [DataRow(DisplayControl.ColorTemperature, -1)]
    [DataRow((DisplayControl)999, 50)]
    public async Task InvalidValuesNeverReachWorker(DisplayControl control, int value)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var count = fixture.Worker.Requests.Length;
        var error = await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.SetDisplayControlAsync(binding.TargetId, control, value));
        Assert.AreEqual("invalid_control_value", error.Code);
        Assert.AreEqual(count, fixture.Worker.Requests.Length);
    }

    [DataTestMethod]
    [DataRow(true, true, 70, null)]
    [DataRow(true, false, 40, "control_readback_mismatch")]
    [DataRow(true, false, null, "control_read_failed")]
    [DataRow(false, false, null, "control_unsupported")]
    public async Task SingleWritePreservesActualAcceptanceAndReadback(bool accepted, bool confirmed, int? value, string? error)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var expected = new DisplayControlWriteResult(accepted, value, confirmed, error);
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, accepted, error,
            WriteAccepted: accepted, ControlWrite: expected));
        var count = fixture.Worker.Requests.Length;
        Assert.AreEqual(expected, await fixture.Controller.SetDisplayControlAsync(binding.TargetId, DisplayControl.Contrast, 70));
        var request = fixture.Worker.Requests.Skip(count).Single();
        Assert.AreEqual("set-control", request.Command);
        Assert.AreEqual(DisplayControl.Contrast, request.Control);
        Assert.AreEqual(70, request.Value);
        Assert.AreEqual(binding.TargetKey, request.TargetKey);
        Assert.AreEqual(binding.TopologyFingerprint, request.TopologyFingerprint);
        Assert.IsFalse((await fixture.Controller.GetSnapshotAsync()).Busy);
    }

    [TestMethod]
    public async Task QuantizedContinuousReadbackRemainsConfirmedWithActualPercentage()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, true,
            ControlWrite: new(true, 34, true)));
        var result = await fixture.Controller.SetDisplayControlAsync(binding.TargetId, DisplayControl.Contrast, 33);
        Assert.IsTrue(result.Confirmed);
        Assert.AreEqual(34, result.Value);
    }

    [TestMethod]
    public async Task LostWriteReplyIsUnknownAndNeverReplayed()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Handler = (_, _) => throw new ControlException("worker_timeout");
        var count = fixture.Worker.Requests.Length;
        var result = await fixture.Controller.SetDisplayControlAsync(binding.TargetId, DisplayControl.Contrast, 70);
        Assert.IsNull(result.WriteAccepted);
        Assert.IsFalse(result.Confirmed);
        Assert.AreEqual("worker_timeout", result.ErrorCode);
        Assert.AreEqual(1, fixture.Worker.Requests.Length - count);
    }

    [TestMethod]
    public async Task TopologyChangeDuringReadDiscardsStaleControls()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return new(request.RequestId, true, Controls: Controls());
        };
        var task = fixture.Controller.GetDisplayControlsAsync(binding.TargetId);
        await gate.Entered;
        fixture.Controller.NotifyTopologyChanged();
        gate.Release();
        var error = await Assert.ThrowsExceptionAsync<ControlException>(() => task);
        Assert.AreEqual("topology_changed", error.Code);
        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
    }

    [TestMethod]
    public async Task TopologyFailureAfterAcceptedRotationRetainsAcceptanceAndRequiresConfirmation()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, false, "topology_changed",
            WriteAccepted: true, ControlWrite: new(true, null, false, "topology_changed")));
        var result = await fixture.Controller.SetDisplayControlAsync(binding.TargetId, DisplayControl.Rotation, 1);
        Assert.AreEqual(true, result.WriteAccepted);
        Assert.IsFalse(result.Confirmed);
        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
        var count = fixture.Worker.Requests.Length;
        await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.SetDisplayControlAsync(binding.TargetId, DisplayControl.Contrast, 70));
        Assert.AreEqual(count, fixture.Worker.Requests.Length);
    }

    [TestMethod]
    public async Task ControlWriteAndPowerCommandUseSameSerialQueue()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            if (request.Command == "set-control")
            {
                await gate.WaitAsync(request, token);
                return new(request.RequestId, true, ControlWrite: new(true, 70, true));
            }
            return fixture.Worker.Reply(request);
        };
        var controlTask = fixture.Controller.SetDisplayControlAsync(binding.TargetId, DisplayControl.Contrast, 70);
        await gate.Entered;
        var powerTask = fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, Guid.NewGuid());
        Assert.IsFalse(powerTask.IsCompleted);
        gate.Release();
        Assert.IsTrue((await controlTask).Confirmed);
        Assert.AreEqual(OperationOutcome.Confirmed, (await powerTask).Outcome);
        Assert.AreEqual(1, fixture.Worker.MaximumConcurrentRequests);
    }

    [TestMethod]
    public async Task HiddenPreferencePersistsByMonitorAndDoesNotAffectPowerOrBinding()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var preferences = new MonitorPreferences(ShowContrast: false, ShowInputSource: true, Hidden: true);
        var count = fixture.Worker.Requests.Length;
        Assert.AreEqual(new MonitorPreferences(), await fixture.Controller.GetMonitorPreferencesAsync(binding.TargetKey));
        Assert.AreEqual(preferences, await fixture.Controller.SetMonitorPreferencesAsync(binding.TargetKey, preferences));
        Assert.AreEqual(count, fixture.Worker.Requests.Length);
        var power = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, Guid.NewGuid());
        Assert.AreEqual(OperationOutcome.Confirmed, power.Outcome);
        await fixture.RestartAsync();
        Assert.AreEqual(preferences, await fixture.Controller.GetMonitorPreferencesAsync(binding.TargetKey));
        Assert.AreEqual(binding.TargetId, (await fixture.Controller.GetSnapshotAsync()).Binding!.TargetId);
    }

    [TestMethod]
    public void PreferencesForDifferentMonitorsStayIndependentAndSurviveRestart()
    {
        using var directory = new TestDirectory();
        var first = new MonitorPreferences(Hidden: true);
        var second = new MonitorPreferences(ShowVolume: true, ShowBrightness: false);
        using (var store = new StateStore(directory.Path))
        {
            store.SaveMonitorPreferences("first", first);
            store.SaveMonitorPreferences("second", second);
        }
        using var reopened = new StateStore(directory.Path);
        Assert.AreEqual(first, reopened.LoadMonitorPreferences("first"));
        Assert.AreEqual(second, reopened.LoadMonitorPreferences("second"));
        Assert.AreEqual(new MonitorPreferences(), reopened.LoadMonitorPreferences("unseen"));
    }
}
