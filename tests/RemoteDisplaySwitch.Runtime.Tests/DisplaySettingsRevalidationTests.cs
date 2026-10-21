using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Runtime;

namespace RemoteDisplaySwitch.Runtime.Tests;

[TestClass]
public sealed class DisplaySettingsRevalidationTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnchangedTopologyPreservesBindingAndOnlyResumeRestartsWorker(bool restartWorker)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var deviceId = (await fixture.Controller.GetSnapshotAsync()).DeviceId;
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        await fixture.Controller.RefreshAsync();
        var requests = fixture.Worker.Requests.Length;
        var stops = fixture.Worker.StopCount;

        await fixture.Controller.RevalidateTopologyAsync(restartWorker: restartWorker);

        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.AreEqual(binding, snapshot.Binding);
        Assert.AreEqual(deviceId, snapshot.DeviceId);
        Assert.IsFalse(snapshot.Busy);
        Assert.IsNull(snapshot.ErrorCode);
        Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        Assert.AreEqual(stops + (restartWorker ? 1 : 0), fixture.Worker.StopCount);
        CollectionAssert.AreEqual(new[] { "discover" }, fixture.Worker.Requests.Skip(requests).Select(request => request.Command).ToArray());
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
        await fixture.RestartAsync();
        Assert.AreEqual(binding, (await fixture.Controller.GetSnapshotAsync()).Binding);
    }

    [DataTestMethod]
    [DataRow("fingerprint")]
    [DataRow("target")]
    [DataRow("profile")]
    [DataRow("missing")]
    [DataRow("ambiguous")]
    public async Task ChangedTopologyStillRequiresConfirmationAndRejectsWrites(string change)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var discovery = fixture.Worker.Discovery;
        var candidate = discovery.Candidates.Single();
        fixture.Worker.Discovery = change switch
        {
            "fingerprint" => discovery with { TopologyFingerprint = "changed-topology" },
            "target" => discovery with { Candidates = [candidate with { TargetKey = "different-target" }] },
            "profile" => discovery with { Candidates = [candidate with { Profile = "different-profile" }] },
            "missing" => discovery with { Candidates = [] },
            _ => discovery with { Candidates = [candidate, candidate] },
        };

        await fixture.Controller.RevalidateTopologyAsync();

        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.IsTrue(snapshot.Binding!.NeedsConfirmation);
        Assert.AreEqual(binding.TargetId, snapshot.Binding.TargetId);
        Assert.AreEqual("topology_changed", snapshot.ErrorCode);
        var result = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.On, Guid.NewGuid());
        Assert.AreEqual(OperationOutcome.Failed, result.Outcome);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
        await fixture.RestartAsync();
        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
    }

    [TestMethod]
    public async Task ExistingInvalidationIsNeverAutomaticallyRebound()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Controller.NotifyTopologyChanged();
        var requests = fixture.Worker.Requests.Length;

        await fixture.Controller.RevalidateTopologyAsync();

        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.IsTrue(snapshot.Binding!.NeedsConfirmation);
        Assert.AreEqual(binding.TargetId, snapshot.Binding.TargetId);
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [TestMethod]
    public async Task NoBindingDoesNotStartDiscovery()
    {
        await using var fixture = new ControllerFixture();

        await fixture.Controller.RevalidateTopologyAsync();

        Assert.AreEqual(0, fixture.Worker.Requests.Length);
        Assert.AreEqual(0, fixture.Worker.StopCount);
        Assert.IsNull((await fixture.Controller.GetSnapshotAsync()).Binding);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedNotificationsCoalesceAndIgnoreAnOutdatedDiscovery(bool staleFailure)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var requests = fixture.Worker.Requests.Length;
        var stops = fixture.Worker.StopCount;
        var gate = new RequestGate();
        var discoveries = 0;
        fixture.Worker.Handler = async (request, token) =>
        {
            if (Interlocked.Increment(ref discoveries) == 1)
            {
                await gate.WaitAsync(request, token);
                return staleFailure
                    ? new(request.RequestId, false, "topology_changed")
                    : new(request.RequestId, true, Discovery: fixture.Worker.Discovery with { TopologyFingerprint = "transient-topology" });
            }
            return fixture.Worker.Reply(request);
        };
        var first = fixture.Controller.RevalidateTopologyAsync();
        var pending = new List<Task> { first };
        try
        {
            await gate.Entered;
            for (var notification = 0; notification < 6; notification++)
                pending.Add(fixture.Controller.RevalidateTopologyAsync());
        }
        finally { gate.Release(); }

        await Task.WhenAll(pending).WaitAsync(RequestGate.Deadline);

        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.AreEqual(binding, snapshot.Binding);
        Assert.IsNull(snapshot.ErrorCode);
        Assert.IsFalse(snapshot.Busy);
        Assert.AreEqual(requests + 2, fixture.Worker.Requests.Length);
        Assert.AreEqual(stops, fixture.Worker.StopCount);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
        Assert.AreEqual(1, fixture.Worker.MaximumConcurrentRequests);
    }

    [TestMethod]
    public async Task ResumeRestartIsRetainedWhenLaterDisplayNotificationsCoalesce()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var stops = fixture.Worker.StopCount;
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var refresh = fixture.Controller.RefreshAsync();
        var pending = new List<Task> { refresh };
        try
        {
            await gate.Entered;
            pending.Add(fixture.Controller.RevalidateTopologyAsync(restartWorker: true));
            for (var notification = 0; notification < 6; notification++)
                pending.Add(fixture.Controller.RevalidateTopologyAsync());
        }
        finally { gate.Release(); }

        await Task.WhenAll(pending).WaitAsync(RequestGate.Deadline);
        Assert.AreEqual(stops + 1, fixture.Worker.StopCount);
        Assert.AreEqual(binding, (await fixture.Controller.GetSnapshotAsync()).Binding);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [TestMethod]
    public async Task DefiniteTopologyChangeDuringValidationIsNeverCleared()
    {
        await using var fixture = new ControllerFixture();
        await fixture.BindAsync();
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var validation = fixture.Controller.RevalidateTopologyAsync();
        try
        {
            await gate.Entered;
            fixture.Controller.NotifyTopologyChanged();
        }
        finally { gate.Release(); }

        await validation.WaitAsync(RequestGate.Deadline);

        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
        await fixture.RestartAsync();
        Assert.IsTrue((await fixture.Controller.GetSnapshotAsync()).Binding!.NeedsConfirmation);
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
    }

    [DataTestMethod]
    [DataRow("topology_changed")]
    [DataRow("unsupported_topology")]
    [DataRow("target_not_unique_or_not_ddc")]
    public async Task WorkerIdentityFailureStillInvalidatesBinding(string errorCode)
    {
        await using var fixture = new ControllerFixture();
        await fixture.BindAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, false, errorCode));

        await fixture.Controller.RevalidateTopologyAsync();

        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.IsTrue(snapshot.Binding!.NeedsConfirmation);
        Assert.AreEqual("topology_changed", snapshot.ErrorCode);
        Assert.IsFalse(snapshot.Busy);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [DataTestMethod]
    [DataRow("worker_timeout")]
    [DataRow("io_exception")]
    [DataRow("topology_unavailable")]
    public async Task TransientWorkerFailurePreservesBindingAndDoesNotWriteOrRetry(string failure)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var requests = fixture.Worker.Requests.Length;
        fixture.Worker.Handler = (request, _) => failure == "topology_unavailable"
            ? Task.FromResult(new WorkerReply(request.RequestId, false, failure))
            : Task.FromException<WorkerReply>(failure == "io_exception" ? new IOException("Fake worker failure") : new ControlException(failure));

        await fixture.Controller.RevalidateTopologyAsync();

        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.AreEqual(binding, snapshot.Binding);
        Assert.AreEqual(failure == "io_exception" ? "worker_unavailable" : failure, snapshot.ErrorCode);
        Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        Assert.IsFalse(snapshot.Busy);
        Assert.AreEqual(requests + 1, fixture.Worker.Requests.Length);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
        fixture.Worker.Handler = null;
        await fixture.Controller.RevalidateTopologyAsync();
        Assert.AreEqual(binding, (await fixture.Controller.GetSnapshotAsync()).Binding);
        Assert.IsNull((await fixture.Controller.GetSnapshotAsync()).ErrorCode);
    }

    [TestMethod]
    public async Task ValidationNeverReplaysAnUncertainPowerOperation()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var operationId = Guid.NewGuid();
        fixture.Worker.Handler = (_, _) => Task.FromException<WorkerReply>(new ControlException("worker_timeout"));
        var original = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId);
        Assert.AreEqual(OperationOutcome.Unknown, original.Outcome);
        fixture.Worker.Handler = null;

        await fixture.Controller.RevalidateTopologyAsync();
        var requests = fixture.Worker.Requests.Length;
        var retry = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId);

        Assert.AreEqual(original, retry);
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(1, fixture.Worker.Writes.Length);
        Assert.AreEqual(binding, (await fixture.Controller.GetSnapshotAsync()).Binding);
    }

    [TestMethod]
    public async Task PowerCommandWaitsForValidationAndCannotUseChangedTopology()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var gate = new RequestGate();
        fixture.Worker.Handler = async (request, token) =>
        {
            await gate.WaitAsync(request, token);
            return fixture.Worker.Reply(request);
        };
        var validation = fixture.Controller.RevalidateTopologyAsync();
        Task<OperationRecord>? pending = null;
        try
        {
            Assert.AreEqual("discover", (await gate.Entered).Command);
            var snapshot = await fixture.Controller.GetSnapshotAsync();
            Assert.IsTrue(snapshot.Busy);
            Assert.IsFalse(snapshot.Binding!.NeedsConfirmation);
            pending = fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Toggle, Guid.NewGuid());
            Assert.IsFalse(pending.IsCompleted);
            Assert.AreEqual(0, fixture.Worker.Writes.Length);
            fixture.Worker.Discovery = fixture.Worker.Discovery with { TopologyFingerprint = "changed-topology" };
        }
        finally { gate.Release(); }

        await validation.WaitAsync(RequestGate.Deadline);
        var result = await pending!.WaitAsync(RequestGate.Deadline);
        Assert.AreEqual(OperationOutcome.Failed, result.Outcome);
        Assert.AreEqual("topology_changed", result.ErrorCode);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
        Assert.AreEqual(1, fixture.Worker.MaximumConcurrentRequests);
    }
}
