using Fy1Switch.Contracts;
using Fy1Switch.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Runtime.Tests;

[TestClass]
public sealed class DiscoveryRecoveryTests
{
    [DataTestMethod]
    [DataRow("display_process_conflict")]
    [DataRow("worker_already_running")]
    [DataRow("discovery_restart_required")]
    public async Task SuccessfulExplicitScanClearsOldFailureAndKeepsMatchingBinding(string code)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, false, code));
        await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.RefreshAsync());
        Assert.AreEqual(code, (await fixture.Controller.GetSnapshotAsync()).ErrorCode);
        fixture.Worker.Handler = null;
        var count = fixture.Worker.Requests.Length;
        await fixture.Controller.DiscoverAsync();
        var recovered = await fixture.Controller.GetSnapshotAsync();
        Assert.IsNull(recovered.ErrorCode);
        Assert.AreEqual(binding, recovered.Binding);
        Assert.AreEqual(DisplayPower.Unknown, recovered.Observation.State);
        Assert.AreEqual("discover", fixture.Worker.Requests.Skip(count).Single().Command);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
        fixture.Worker.Reading = FakeWorkerTransport.Observe(DisplayPower.On);
        Assert.AreEqual(DisplayPower.On, (await fixture.Controller.RefreshAsync()).Observation.State);
    }

    [TestMethod]
    public async Task SuccessfulScanWithoutBindingClearsFailureWithoutAutoBinding()
    {
        await using var fixture = new ControllerFixture();
        fixture.Worker.Handler = (request, _) => Task.FromResult(new WorkerReply(request.RequestId, false, "display_process_conflict"));
        await Assert.ThrowsExceptionAsync<ControlException>(() => fixture.Controller.DiscoverAsync());
        fixture.Worker.Handler = null;
        await fixture.Controller.DiscoverAsync();
        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.IsNull(snapshot.ErrorCode);
        Assert.IsNull(snapshot.Binding);
        Assert.AreEqual(DisplayPower.Unknown, snapshot.Observation.State);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [DataTestMethod]
    [DataRow("fingerprint")]
    [DataRow("target")]
    [DataRow("profile")]
    [DataRow("missing")]
    [DataRow("ambiguous")]
    public async Task ScanOfChangedTargetRequiresConfirmationInsteadOfClearingError(string change)
    {
        await using var fixture = new ControllerFixture();
        var binding = await fixture.BindAsync();
        var discovery = fixture.Worker.Discovery;
        var candidate = discovery.Candidates.Single();
        fixture.Worker.Discovery = change switch
        {
            "fingerprint" => discovery with { TopologyFingerprint = "changed" },
            "target" => discovery with { Candidates = [candidate with { TargetKey = "other" }] },
            "profile" => discovery with { Candidates = [candidate with { Profile = "other" }] },
            "missing" => discovery with { Candidates = [] },
            _ => discovery with { Candidates = [candidate, candidate] },
        };
        await fixture.Controller.DiscoverAsync();
        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.AreEqual("topology_changed", snapshot.ErrorCode);
        Assert.IsTrue(snapshot.Binding!.NeedsConfirmation);
        Assert.AreEqual(binding.TargetId, snapshot.Binding.TargetId);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }

    [TestMethod]
    public async Task ScanNeverRestoresAlreadyInvalidatedBinding()
    {
        await using var fixture = new ControllerFixture();
        await fixture.BindAsync();
        fixture.Controller.NotifyTopologyChanged();
        await fixture.Controller.DiscoverAsync();
        var snapshot = await fixture.Controller.GetSnapshotAsync();
        Assert.IsTrue(snapshot.Binding!.NeedsConfirmation);
        Assert.AreEqual("topology_changed", snapshot.ErrorCode);
        Assert.AreEqual(0, fixture.Worker.Writes.Length);
    }
}
