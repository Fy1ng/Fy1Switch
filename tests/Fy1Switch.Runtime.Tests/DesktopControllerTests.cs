using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Runtime.Tests;

[TestClass]
public sealed class DesktopControllerTests
{
    [TestMethod]
    public async Task FreshSnapshotIsUnknownAndDoesNotContactWorkerOrStartup()
    {
        await using var fixture = new ControllerFixture();
        var snapshot = await fixture.Controller.GetSnapshotAsync();

        Assert.AreNotEqual(Guid.Empty, snapshot.DeviceId);
        Assert.IsNull(snapshot.Binding);
        Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        Assert.IsNull(snapshot.Observation.RawValue);
        Assert.IsFalse(snapshot.Busy);
        Assert.IsFalse(snapshot.WorkerRunning);
        Assert.IsFalse(snapshot.Simulation);
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
        Assert.AreEqual(0, fixture.Startup.Changes.Count);
    }

    [TestMethod]
    public async Task DiscoveryAndBindingUseTheSameFingerprintAndDoNotInferPower()
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var snapshot = await fixture.Controller.GetSnapshotAsync();

        Assert.AreEqual(FakeWorkerTransport.TargetKey, binding.TargetKey);
        Assert.AreEqual(FakeWorkerTransport.Fingerprint, binding.TopologyFingerprint);
        Assert.AreEqual("fake-profile", binding.Profile);
        Assert.AreNotEqual(Guid.Empty, binding.TargetId);
        Assert.IsFalse(binding.NeedsConfirmation);
        Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        CollectionAssert.AreEqual(new[] { "discover", "discover" }, fixture.Worker.Requests.Select(request => request.Command).ToArray());
    }

    [TestMethod]
    public async Task BindingRejectsFingerprintChangedSinceDiscovery()
    {
        await using var fixture = new ControllerFixture();
        var discovery = await fixture.Controller.DiscoverAsync();
        fixture.Worker.Discovery = discovery with { TopologyFingerprint = "fake-topology-v2" };

        var exception = await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.BindAsync(FakeWorkerTransport.TargetKey, discovery.TopologyFingerprint, "Test"));

        Assert.AreEqual("topology_changed", exception.Code);
        Assert.IsNull((await fixture.Controller.GetSnapshotAsync()).Binding);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [TestMethod]
    public async Task BindingRejectsTargetNotInDiscovery()
    {
        await using var fixture = new ControllerFixture();
        var exception = await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.BindAsync("not-discovered", FakeWorkerTransport.Fingerprint, "Test"));

        Assert.AreEqual("target_not_found", exception.Code);
        Assert.IsNull((await fixture.Controller.GetSnapshotAsync()).Binding);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [DataTestMethod]
    [DataRow(PowerIntent.On, DisplayPower.On)]
    [DataRow(PowerIntent.Off, DisplayPower.Off)]
    public async Task ExplicitPowerWritesExactlyTheRequestedIntent(PowerIntent intent, DisplayPower expected)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var before = fixture.Worker.Requests.Length;
        var operationId = Guid.NewGuid();

        var record = await fixture.Controller.ExecuteAsync(binding.TargetId, intent, operationId);

        Assert.AreEqual(operationId, record.OperationId);
        Assert.AreEqual(binding.TargetId, record.TargetId);
        Assert.AreEqual(intent, record.Requested);
        Assert.AreEqual(intent, record.Resolved);
        Assert.AreEqual(OperationOutcome.Confirmed, record.Outcome);
        Assert.AreEqual(expected, record.Observation?.State);
        var request = fixture.Worker.Requests.Skip(before).Single();
        Assert.AreEqual("set", request.Command);
        Assert.AreEqual(intent, request.Intent);
        Assert.AreEqual(binding.TargetKey, request.TargetKey);
        Assert.AreEqual(binding.TopologyFingerprint, request.TopologyFingerprint);
        Assert.AreEqual(expected, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
        Assert.AreEqual(record, await fixture.Controller.GetOperationAsync(operationId));
    }

    [DataTestMethod]
    [DataRow(DisplayPower.On, DisplayPower.Off, PowerIntent.On)]
    [DataRow(DisplayPower.Off, DisplayPower.On, PowerIntent.Off)]
    public async Task ToggleReadsAuthoritativeStateRatherThanCachedSnapshot(DisplayPower cached, DisplayPower authoritative, PowerIntent expected)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(cached);
        Assert.AreEqual(cached, (await fixture.Controller.RefreshAsync()).Observation.State);
        fixture.Worker.Reading = FakeWorkerTransport.Observe(authoritative);
        var before = fixture.Worker.Requests.Length;

        var record = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Toggle, Guid.NewGuid());

        var requests = fixture.Worker.Requests.Skip(before).ToArray();
        CollectionAssert.AreEqual(new[] { "read", "set" }, requests.Select(request => request.Command).ToArray());
        Assert.IsTrue(requests.All(request => request.TargetKey == binding.TargetKey && request.TopologyFingerprint == binding.TopologyFingerprint));
        Assert.IsNull(requests[0].Intent);
        Assert.AreEqual(expected, requests[1].Intent);
        Assert.AreEqual(expected, record.Resolved);
        Assert.AreEqual(PowerIntent.Toggle, record.Requested);
        Assert.AreEqual(OperationOutcome.Confirmed, record.Outcome);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ToggleRejectsUnknownOrMissingObservationEvenWithCachedOn(bool missing)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        await fixture.Controller.RefreshAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, true, Observation: missing ? null : FakeWorkerTransport.Observe(DisplayPower.Unknown)));
        var before = fixture.Worker.Requests.Length;

        var record = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Toggle, Guid.NewGuid());

        Assert.AreEqual(OperationOutcome.Failed, record.Outcome);
        Assert.AreEqual("state_unknown", record.ErrorCode);
        Assert.IsNull(record.Resolved);
        Assert.AreEqual("read", fixture.Worker.Requests.Skip(before).Single().Command);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
        Assert.AreEqual(DisplayPower.Unknown, (await fixture.Controller.GetSnapshotAsync()).Observation.State);
    }

    [DataTestMethod]
    [DataRow(PowerIntent.On)]
    [DataRow(PowerIntent.Off)]
    [DataRow(PowerIntent.Toggle)]
    public async Task SameOperationIdAndRequestReturnsOriginalRecordWithoutWorkerRequest(PowerIntent intent)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        var operationId = Guid.NewGuid();
        var original = await fixture.Controller.ExecuteAsync(binding.TargetId, intent, operationId);
        var requests = fixture.Worker.Requests.Length;

        var retry = await fixture.Controller.ExecuteAsync(binding.TargetId, intent, operationId);

        Assert.AreEqual(OperationOutcome.Confirmed, original.Outcome);
        Assert.AreEqual(original, retry);
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(1, fixture.Worker.Writes.Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReusedOperationIdWithDifferentCommandOrTargetDoesNotOverwriteRecord(bool differentTarget)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var operationId = Guid.NewGuid();
        var original = await fixture.Controller.ExecuteAsync(binding.TargetId, PowerIntent.Off, operationId);
        var requests = fixture.Worker.Requests.Length;

        var exception = await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.ExecuteAsync(differentTarget ? Guid.NewGuid() : binding.TargetId, differentTarget ? PowerIntent.Off : PowerIntent.On, operationId));

        Assert.AreEqual("operation_id_conflict", exception.Code);
        Assert.AreEqual(original, await fixture.Controller.GetOperationAsync(operationId));
        Assert.AreEqual(requests, fixture.Worker.Requests.Length);
        Assert.AreEqual(1, fixture.Worker.Writes.Length);
    }

    [TestMethod]
    public async Task StartupUsesFakeRegistrationOnly()
    {
        await using var fixture = new ControllerFixture();
        Assert.IsTrue((await fixture.Controller.SetStartupAsync(true)).StartupEnabled);
        Assert.IsTrue(fixture.Startup.Enabled);
        Assert.IsFalse((await fixture.Controller.SetStartupAsync(false)).StartupEnabled);
        CollectionAssert.AreEqual(new[] { true, false }, fixture.Startup.Changes);
        Assert.AreEqual(0, fixture.Worker.Requests.Length);
    }
}
