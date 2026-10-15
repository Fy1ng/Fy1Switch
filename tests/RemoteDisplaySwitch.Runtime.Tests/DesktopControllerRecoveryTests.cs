using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Runtime;

namespace RemoteDisplaySwitch.Runtime.Tests;

[TestClass]
public sealed class DesktopControllerRecoveryTests
{
    [TestMethod]
    public async Task RestartLoadsBindingAndDeduplicationButNeverRestoresAnOnObservation()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var deviceId = (await fixture.Controller.GetSnapshotAsync()).DeviceId;
        var operationId = Guid.NewGuid();
        var original = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.On, operationId);
        Assert.AreEqual(DisplayPower.On, (await fixture.Controller.GetSnapshotAsync()).Observation.State);

        await fixture.RestartAsync();
        var snapshot = await fixture.Controller.GetSnapshotAsync();
        var retry = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.On, operationId);

        Assert.AreEqual(deviceId, snapshot.DeviceId);
        Assert.AreEqual(binding, snapshot.Binding);
        Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        Assert.IsNull(snapshot.Observation.RawValue);
        Assert.AreEqual(original, retry);
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
    }

    [DataTestMethod]
    [DataRow("worker_timeout")]
    [DataRow("worker_disconnected")]
    [DataRow("io_exception")]
    public async Task UncertainWriteBecomesUnknownAndSameIdIsNotResentEvenAfterRestart(string failure)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        await fixture.Controller.RefreshAsync();
        fixture.Worker.Handler = (_, _) => Task.FromException<WorkerReply>(failure == "io_exception" ? new IOException("Fake transport disconnected") : new ControlException(failure));
        var operationId = Guid.NewGuid();

        var original = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId);
        var requests = fixture.Worker.Requests.Length;
        var retry = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId);

        Assert.AreEqual(OperationOutcome.Unknown, original.Outcome);
        Assert.AreEqual(failure == "io_exception" ? "worker_unavailable" : failure, original.ErrorCode);
        Assert.AreEqual(PowerIntent.Off, original.Resolved);
        Assert.AreEqual(DisplayPower.Unknown, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
        Assert.AreEqual(original, retry);
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(1, fixture.Worker.Writes.Length);

        await fixture.RestartAsync();
        Assert.AreEqual(original, await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId));
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
        Assert.AreEqual(DisplayPower.Unknown, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
    }

    [TestMethod]
    public async Task TopologyInvalidationPersistsWithoutAnotherCommandAndRejectsWritesAfterRestart()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        await fixture.Controller.RefreshAsync();
        fixture.Controller.NotifyTopologyChanged();

        var invalidated = await fixture.Controller.GetSnapshotAsync();
        Assert.IsTrue(invalidated.Binding!.NeedsConfirmation);
        Assert.AreEqual(DisplayPower.Unknown, invalidated.Observation.State);
        Assert.AreEqual("topology_changed", invalidated.ErrorCode);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);

        await fixture.RestartAsync();
        var restored = await fixture.Controller.GetSnapshotAsync();
        Assert.AreEqual(binding.TargetId, restored.Binding!.TargetId);
        Assert.IsTrue(restored.Binding.NeedsConfirmation);
        Assert.AreEqual("topology_changed", restored.ErrorCode);
        var afterRestart = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, Guid.NewGuid());
        Assert.AreEqual(OperationOutcome.Failed, afterRestart.Outcome);
        Assert.AreEqual("topology_changed", afterRestart.ErrorCode);
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
    }

    [DataTestMethod]
    [DataRow(PowerIntent.On)]
    [DataRow(PowerIntent.Off)]
    [DataRow(PowerIntent.Toggle)]
    public async Task InvalidatedBindingRejectsEveryPowerIntentWithoutWorkerRequest(PowerIntent intent)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Controller.NotifyTopologyChanged();
        var requests = fixture.Worker.Requests.Length;

        var record = await fixture.Controller.ExecuteAsync(binding.TargetId, intent, Guid.NewGuid());

        Assert.AreEqual(OperationOutcome.Failed, record.Outcome);
        Assert.AreEqual("topology_changed", record.ErrorCode);
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [TestMethod]
    public async Task RebindingCreatesNewTargetIdAndRejectsOldTargetWithoutWriting()
    {
        await using var fixture = new ControllerFixture();
        var oldBinding = await fixture.BindAsync();
        fixture.Controller.NotifyTopologyChanged();
        fixture.Worker.Discovery = fixture.Worker.Discovery with { TopologyFingerprint = "fake-topology-v2" };
        var newBinding = await fixture.BindAsync();

        Assert.AreNotEqual(oldBinding.TargetId, newBinding.TargetId);
        Assert.IsFalse(newBinding.NeedsConfirmation);
        Assert.AreEqual("fake-topology-v2", newBinding.TopologyFingerprint);
        var requests = fixture.Worker.Requests.Length;
        var rejected = await fixture.Controller.ExecuteAsync(oldBinding.TargetId, PowerIntent.On, Guid.NewGuid());
        Assert.AreEqual(OperationOutcome.Failed, rejected.Outcome);
        Assert.AreEqual("target_changed", rejected.ErrorCode);
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);

        var accepted = await fixture.Controller.ExecuteAsync(newBinding.TargetId, PowerIntent.On, Guid.NewGuid());
        Assert.AreEqual(OperationOutcome.Confirmed, accepted.Outcome);
        Assert.AreEqual(newBinding.TopologyFingerprint, fixture.Worker.Writes.Single().TopologyFingerprint);
        await fixture.RestartAsync();
        Assert.AreEqual(newBinding, (await fixture.Controller.GetSnapshotAsync()).Binding);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransientTopologyFailurePreservesBindingAndDeduplicationAfterRestart(bool writeAccepted)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var operationId = Guid.NewGuid();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, false, "topology_unavailable", WriteAccepted: writeAccepted));

        var original = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId);
        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.AreEqual(writeAccepted ? OperationOutcome.Unknown : OperationOutcome.Failed, original.Outcome);
        Assert.AreEqual("topology_unavailable", original.ErrorCode);
        Assert.AreEqual(binding, snapshot.Binding);
        Assert.IsFalse(snapshot.Binding!.NeedsConfirmation);
        Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        var requests = fixture.Worker.Requests.Length;
        Assert.AreEqual(original, await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId));
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);

        await fixture.RestartAsync();
        Assert.AreEqual(binding, (await fixture.Controller.GetSnapshotAsync()).Binding);
        Assert.AreEqual(original, await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId));
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        await fixture.Controller.RefreshAsync();
        Assert.AreEqual(DisplayPower.On, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
        var recovered = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.On, Guid.NewGuid());
        Assert.AreEqual(OperationOutcome.Confirmed, recovered.Outcome);
        Assert.AreEqual(1, fixture.Worker.Writes.Length);
    }

    [DataTestMethod]
    [DataRow("topology_changed")]
    [DataRow("target_not_found")]
    [DataRow("target_ambiguous")]
    [DataRow("profile_unsupported")]
    public async Task WorkerTopologyErrorInvalidatesAndPersistsBinding(string errorCode)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, false, errorCode, WriteAccepted: false));
        var record = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.On, Guid.NewGuid());

        Assert.AreEqual(OperationOutcome.Failed, record.Outcome);
        Assert.AreEqual(errorCode, record.ErrorCode);
        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
        await fixture.RestartAsync();
        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
        var rejected = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, Guid.NewGuid());
        Assert.AreEqual("topology_changed", rejected.ErrorCode);
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
    }

    [TestMethod]
    public async Task PendingJournalBecomesUnknownOnStartupAndIsNeverReplayed()
    {
        using var directory = new TestDirectory();
        var binding = new MonitorBinding(Guid.NewGuid(), FakeWorkerTransport.TargetKey, "Test", "fake-profile", FakeWorkerTransport.Fingerprint);
        var pending = new OperationRecord(Guid.NewGuid(), binding.TargetId, PowerIntent.Toggle, PowerIntent.Off, OperationOutcome.Pending, null, null, DateTimeOffset.UtcNow.AddHours(-1));
        using (var store = new StateStore(directory.Path))
        {
            store.SaveBinding(binding);
            store.SaveOperation(pending);
        }
        var worker = new FakeWorkerTransport();
        await using (var controller = new DesktopController(worker, new FakeStartup(), directory.Path, simulation: false))
        {
            var recovered = await controller.GetOperationAsync(pending.OperationId);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(OperationOutcome.Unknown, recovered.Outcome);
            Assert.AreEqual("host_restarted", recovered.ErrorCode);
            Assert.AreEqual(pending.Resolved, recovered.Resolved);
            Assert.AreEqual(recovered, await controller.ExecuteAsync(binding.TargetId, pending.Requested, pending.OperationId));
            Assert.AreEqual(DisplayPower.Unknown, (await controller.GetSnapshotAsync()).Observation.State);
            Assert.AreEqual(0, worker.Requests.Length);
        }
        using var reopened = new StateStore(directory.Path);
        Assert.AreEqual(OperationOutcome.Unknown, reopened.FindOperation(pending.OperationId)!.Outcome);
    }
}
