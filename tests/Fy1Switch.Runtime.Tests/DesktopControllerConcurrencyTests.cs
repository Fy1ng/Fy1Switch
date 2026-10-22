using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Runtime.Tests;

[TestClass]
public sealed class DesktopControllerConcurrencyTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentCommandsAreSerializedAndDuplicateIdWritesOnlyOnce(bool duplicate)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var operationId = Guid.NewGuid();
        var first = fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.On, operationId);
        Task<OperationRecord> second;
        try
        {
            Assert.AreEqual("set", (await gate.Entered).Command);
            second = fixture.Controller.ExecuteAsync(binding.TargetId, duplicate ? PowerIntent.On : PowerIntent.Off, duplicate ? operationId : Guid.NewGuid());
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual(1, fixture.Worker.Writes.Length);
        }
        finally { gate.Release(); }

        var records = await Task.WhenAll(first, second).WaitAsync(RequestGate.Deadline);
        Assert.IsTrue(records.All(record => record.Outcome == OperationOutcome.Confirmed));
        Assert.AreEqual(1, fixture.Worker.MaximumConcurrentRequests);
        CollectionAssert.AreEqual(duplicate ? new[] { PowerIntent.On } : new[] { PowerIntent.On, PowerIntent.Off }, fixture.Worker.Writes.Select(request => request.Intent!.Value).ToArray());
        if (duplicate) Assert.AreEqual(records[0], records[1]);
        Assert.IsFalse((await fixture.Controller.GetSnapshotAsync()).Busy);
    }

    [TestMethod]
    public async Task ConcurrentTogglesKeepEachReadWritePairTogether()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        var before = fixture.Worker.Requests.Length;
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var first = fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Toggle, Guid.NewGuid());
        Task<OperationRecord> second;
        try
        {
            Assert.AreEqual("read", (await gate.Entered).Command);
            second = fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Toggle, Guid.NewGuid());
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual(before + 1, fixture.Worker.Requests.Length);
        }
        finally { gate.Release(); }

        var records = await Task.WhenAll(first, second).WaitAsync(RequestGate.Deadline);
        Assert.IsTrue(records.All(record => record.Outcome == OperationOutcome.Confirmed));
        Assert.AreEqual(PowerIntent.Off, records[0].Resolved);
        Assert.AreEqual(PowerIntent.On, records[1].Resolved);
        CollectionAssert.AreEqual(new[] { "read", "set", "read", "set" }, fixture.Worker.Requests.Skip(before).Select(request => request.Command).ToArray());
        Assert.AreEqual(1, fixture.Worker.MaximumConcurrentRequests);
    }

    [DataTestMethod]
    [DataRow("discover")]
    [DataRow("bind")]
    [DataRow("refresh")]
    [DataRow("set")]
    [DataRow("toggle")]
    public async Task SnapshotReturnsBusyWhileWorkerRequestIsStillBlocked(string action)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        Task pending = action switch
        {
            "discover" => fixture.Controller.DiscoverAsync(),
            "bind" => fixture.Controller.BindAsync(binding.TargetKey, binding.TopologyFingerprint, "Rebind"),
            "refresh" => fixture.Controller.RefreshAsync(),
            "set" => fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, Guid.NewGuid()),
            "toggle" => fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Toggle, Guid.NewGuid()),
            _ => throw new AssertFailedException("Unsupported test action")
        };
        try
        {
            await gate.Entered;
            var snapshot = await Task.Run(() => fixture.Controller.GetSnapshotAsync()).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsTrue(snapshot.Busy);
            Assert.IsTrue(snapshot.WorkerRunning);
            Assert.IsFalse(pending.IsCompleted, "Snapshot must return before the worker request is released.");
            Assert.AreEqual(binding.TargetId, snapshot.Binding!.TargetId);
        }
        finally { gate.Release(); }

        await pending.WaitAsync(RequestGate.Deadline);
        Assert.IsFalse((await fixture.Controller.GetSnapshotAsync()).Busy);
    }

    [TestMethod]
    public async Task CancellationAfterWriteWasIssuedIsUnknownAndSameIdDoesNotResend()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        await fixture.Controller.RefreshAsync();
        using var cancellation = new CancellationTokenSource();
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var operationId = Guid.NewGuid();
        var pending = fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId, cancellation.Token);
        OperationRecord record;
        try
        {
            Assert.AreEqual("set", (await gate.Entered).Command);
            cancellation.Cancel();
            record = await pending.WaitAsync(RequestGate.Deadline);
        }
        finally { gate.Release(); }

        Assert.AreEqual(OperationOutcome.Unknown, record.Outcome);
        Assert.AreEqual("request_cancelled", record.ErrorCode);
        Assert.AreEqual(DisplayPower.Unknown, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
        var requests = fixture.Worker.Requests.Length;
        Assert.AreEqual(record, await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId));
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(1, fixture.Worker.Writes.Length);
    }

    [TestMethod]
    public async Task TopologyChangeDuringBindingRejectsDiscoveryEvenWhenFingerprintMatches()
    {
        await using var fixture = new ControllerFixture();
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var pending = fixture.Controller.BindAsync(FakeWorkerTransport.TargetKey, FakeWorkerTransport.Fingerprint, "Test");
        try
        {
            await gate.Entered;
            fixture.Controller.NotifyTopologyChanged();
        }
        finally { gate.Release(); }

        var exception = await Assert.ThrowsExceptionAsync<ControlException>(() => pending.WaitAsync(RequestGate.Deadline));
        Assert.AreEqual("topology_changed", exception.Code);
        Assert.IsNull((await fixture.Controller.GetSnapshotAsync()).Binding);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [DataTestMethod]
    [DataRow(PowerIntent.Toggle, "read", OperationOutcome.Failed, 0)]
    [DataRow(PowerIntent.On, "set", OperationOutcome.Unknown, 1)]
    public async Task TopologyChangeDuringRequestRejectsStaleSuccessAndPersistsInvalidation(PowerIntent intent, string command, OperationOutcome outcome, int writes)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var operationId = Guid.NewGuid();
        var pending = fixture.Controller.ExecuteAsync(binding.TargetId, intent, operationId);
        try
        {
            Assert.AreEqual(command, (await gate.Entered).Command);
            fixture.Controller.NotifyTopologyChanged();
            var snapshot = await fixture.Controller.GetSnapshotAsync();
            Assert.IsTrue(snapshot.Busy);
            Assert.IsTrue(snapshot.Binding!.NeedsConfirmation);
            Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        }
        finally { gate.Release(); }

        var record = await pending.WaitAsync(RequestGate.Deadline);
        Assert.AreEqual(outcome, record.Outcome);
        Assert.AreEqual("topology_changed", record.ErrorCode);
        Assert.AreEqual(writes, fixture.Worker.Writes.Length);
        Assert.AreEqual(DisplayPower.Unknown, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
        var requests = fixture.Worker.Requests.Length;
        Assert.AreEqual(record, await fixture.Controller.ExecuteAsync(binding.TargetId, intent, operationId));
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        await fixture.RestartAsync();
        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
    }
}
