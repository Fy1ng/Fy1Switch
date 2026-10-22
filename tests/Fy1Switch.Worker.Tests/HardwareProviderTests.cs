using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Services;
using Fy1Switch.Contracts;
using Fy1Switch.Worker;

namespace Fy1Switch.Worker.Tests;

[TestClass]
public sealed class HardwareProviderTests
{
    private static WorkerRequest Request(string command, string fingerprint, PowerIntent? intent = null) =>
        new(Guid.NewGuid(), command, FakeBackend.TargetKey, fingerprint, intent);

    [TestMethod]
    public async Task DiscoveryRetainsBackendAndHandlesWithoutSilentRediscovery()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        var factories = 0;
        using (var provider = new HardwareDisplayProvider(directory.Path, () => { factories++; return backend; }, () => null))
        {
            var snapshot = await provider.DiscoverAsync(default);
            Assert.AreEqual(FakeBackend.TargetKey, snapshot.Candidates.Single().TargetKey);
            Assert.AreEqual(TopologyPolicy.Profile, snapshot.Candidates.Single().Profile);
            Assert.AreEqual(TopologyPolicy.Fingerprint(backend.Topology), snapshot.TopologyFingerprint);
            Assert.AreSame(snapshot, await provider.DiscoverAsync(default));
            Assert.AreEqual(DisplayPower.On, (await provider.ReadAsync(FakeBackend.TargetKey, snapshot.TopologyFingerprint, default)).State);
            Assert.AreEqual(1, factories);
            Assert.AreEqual(1, backend.DiscoveryCalls);
            Assert.IsFalse(backend.Disposed);
        }

        Assert.IsTrue(backend.Disposed);
    }

    [TestMethod]
    public async Task ReadAndWriteBeforeDiscoveryNeverCreateHardwareBackend()
    {
        using var directory = new TestDirectory();
        using var provider = new HardwareDisplayProvider(directory.Path, () => throw new AssertFailedException("Native factory called"), () => null);
        var protocol = new WorkerProtocol(provider);
        var fingerprint = new string('c', 64);
        Assert.AreEqual("discovery_required", (await protocol.HandleAsync(Request("read", fingerprint))).ErrorCode);
        var write = await protocol.HandleAsync(Request("set", fingerprint, PowerIntent.Off));
        Assert.AreEqual("discovery_required", write.ErrorCode);
        Assert.AreEqual(false, write.WriteAccepted);
    }

    [TestMethod]
    public async Task StaleRequestAndInexactTargetNeverWriteButDoNotRemap()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        var protocol = new WorkerProtocol(provider);
        var stale = await protocol.HandleAsync(Request("set", new string('c', 64), PowerIntent.Off));
        Assert.AreEqual("topology_changed", stale.ErrorCode);
        Assert.AreEqual(false, stale.WriteAccepted);
        var wrong = await protocol.HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off) with { TargetKey = FakeBackend.TargetKey.ToUpperInvariant() });
        Assert.AreEqual("target_mismatch", wrong.ErrorCode);
        Assert.AreEqual(false, wrong.WriteAccepted);
        Assert.AreEqual(0, backend.Writes.Count);
        Assert.AreEqual(DisplayPower.On, (await provider.ReadAsync(FakeBackend.TargetKey, snapshot.TopologyFingerprint, default)).State);
        Assert.AreEqual(1, backend.DiscoveryCalls);
    }

    [TestMethod]
    public async Task ChangedTopologyRejectsWritesAndLatchesUntilWorkerRestart()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        backend.ChangeTopology();
        var protocol = new WorkerProtocol(provider);
        var reply = await protocol.HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual("topology_changed", reply.ErrorCode);
        Assert.AreEqual(false, reply.WriteAccepted);
        Assert.IsNull(reply.Observation);
        backend.Topology = FakeBackend.ValidTopology();
        Assert.AreEqual("topology_changed", (await protocol.HandleAsync(Request("discover", snapshot.TopologyFingerprint))).ErrorCode);
        Assert.AreEqual("topology_changed", (await protocol.HandleAsync(Request("read", snapshot.TopologyFingerprint))).ErrorCode);
        Assert.AreEqual(0, backend.Writes.Count);
        Assert.AreEqual(1, backend.DiscoveryCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TopologyChangeAfterWritePreservesActualAcceptanceAndNeverConfirms(bool accepted)
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend { WriteAccepted = accepted };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        backend.OnWrite = backend.ChangeTopology;
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual("topology_changed", reply.ErrorCode);
        Assert.AreEqual(accepted, reply.WriteAccepted);
        Assert.IsFalse(reply.Success);
        Assert.IsNull(reply.Observation);
        Assert.AreEqual(0, backend.ReadCalls);
        CollectionAssert.AreEqual(new[] { 4 }, backend.Writes);
    }

    [TestMethod]
    public async Task TopologyChangeDuringReadbackDoesNotReturnAnObservation()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        backend.OnRead = backend.ChangeTopology;
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.On));
        Assert.AreEqual("topology_changed", reply.ErrorCode);
        Assert.AreEqual(true, reply.WriteAccepted);
        Assert.IsNull(reply.Observation);
    }

    [TestMethod]
    public async Task TopologyCaptureFailureBeforeWriteFailsClosedButAllowsRecovery()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        backend.FailCapture = true;
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual("topology_unavailable", reply.ErrorCode);
        Assert.AreEqual(false, reply.WriteAccepted);
        Assert.AreEqual(0, backend.Writes.Count);
        backend.FailCapture = false;
        Assert.AreSame(snapshot, await provider.DiscoverAsync(default));
        Assert.AreEqual(DisplayPower.On, (await provider.ReadAsync(FakeBackend.TargetKey, snapshot.TopologyFingerprint, default)).State);
        Assert.IsTrue((await new WorkerProtocol(provider).HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off))).Success);
        CollectionAssert.AreEqual(new[] { 4 }, backend.Writes);
        Assert.AreEqual(1, backend.DiscoveryCalls);
    }

    [TestMethod]
    public async Task TopologyCaptureFailureAfterReadDiscardsObservationAndAllowsRecovery()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        backend.OnRead = () => backend.FailCapture = true;
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("read", snapshot.TopologyFingerprint));
        Assert.AreEqual("topology_unavailable", reply.ErrorCode);
        Assert.IsNull(reply.Observation);
        Assert.AreEqual(0, backend.Writes.Count);
        backend.OnRead = null;
        backend.FailCapture = false;
        Assert.AreEqual(DisplayPower.On, (await provider.ReadAsync(FakeBackend.TargetKey, snapshot.TopologyFingerprint, default)).State);
        Assert.AreEqual(1, backend.DiscoveryCalls);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task TopologyCaptureFailureAfterWritePreservesAcceptanceWithoutReplaying(bool accepted, bool duringReadback)
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend { WriteAccepted = accepted };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        if (duringReadback) backend.OnRead = () => backend.FailCapture = true;
        else backend.OnWrite = () => backend.FailCapture = true;
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual("topology_unavailable", reply.ErrorCode);
        Assert.AreEqual(accepted, reply.WriteAccepted);
        Assert.IsNull(reply.Observation);
        CollectionAssert.AreEqual(new[] { 4 }, backend.Writes);
        backend.OnRead = null;
        backend.OnWrite = null;
        backend.FailCapture = false;
        Assert.AreEqual(DisplayPower.On, (await provider.ReadAsync(FakeBackend.TargetKey, snapshot.TopologyFingerprint, default)).State);
        CollectionAssert.AreEqual(new[] { 4 }, backend.Writes);
        backend.ChangeTopology();
        Assert.AreEqual("topology_changed", (await new WorkerProtocol(provider).HandleAsync(Request("read", snapshot.TopologyFingerprint))).ErrorCode);
        backend.Topology = FakeBackend.ValidTopology();
        Assert.AreEqual("topology_changed", (await new WorkerProtocol(provider).HandleAsync(Request("read", snapshot.TopologyFingerprint))).ErrorCode);
    }

    [TestMethod]
    public async Task DiscoveryTopologyRaceRequiresRestartAndExposesNoCandidate()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        backend.OnDiscovery = backend.ChangeTopology;
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var protocol = new WorkerProtocol(provider);
        var reply = await protocol.HandleAsync(ProtocolFixture.Request("discover"));
        Assert.AreEqual("topology_changed", reply.ErrorCode);
        Assert.IsNull(reply.Discovery);
        Assert.AreEqual("topology_changed", (await protocol.HandleAsync(ProtocolFixture.Request("discover"))).ErrorCode);
        Assert.AreEqual(1, backend.DiscoveryCalls);
    }

    [TestMethod]
    public async Task OnlyExplicit01And04AreWrittenAndReadbackIsIndependent()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend { Reading = new(true, 1, null, DateTimeOffset.UtcNow) };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        var protocol = new WorkerProtocol(provider);
        var off = await protocol.HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual(true, off.WriteAccepted);
        Assert.AreEqual(DisplayPower.On, off.Observation!.State);
        Assert.AreEqual("hardware_read", off.Observation.Source);
        await protocol.HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.On));
        var toggle = await protocol.HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Toggle));
        Assert.AreEqual("invalid_intent", toggle.ErrorCode);
        CollectionAssert.AreEqual(new[] { 4, 1 }, backend.Writes);
        Assert.AreEqual(2, backend.ReadCalls);
    }

    [TestMethod]
    public async Task FailedWriteStillReadsHardwareAndDoesNotClaimAcceptance()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend { WriteAccepted = false, Reading = new(true, 4, null, DateTimeOffset.UtcNow) };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.IsFalse(reply.Success);
        Assert.AreEqual(false, reply.WriteAccepted);
        Assert.AreEqual("power_write_failed", reply.ErrorCode);
        Assert.AreEqual(DisplayPower.Off, reply.Observation!.State);
        Assert.AreEqual(1, backend.ReadCalls);
    }

    [TestMethod]
    public async Task FailedReadbackPreservesAcceptedWriteButReturnsUnknown()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        backend.OnRead = () => throw new IOException(FakeBackend.PhysicalId);
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        var protocol = new WorkerProtocol(provider);
        var reply = await protocol.HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual(true, reply.WriteAccepted);
        Assert.AreEqual("power_read_failed", reply.ErrorCode);
        Assert.AreEqual(DisplayPower.Unknown, reply.Observation!.State);
        Assert.IsNull(reply.Observation.RawValue);
        Assert.AreEqual("hardware_read", reply.Observation.Source);
        var read = await protocol.HandleAsync(Request("read", snapshot.TopologyFingerprint));
        Assert.IsFalse(read.Success);
        Assert.AreEqual(DisplayPower.Unknown, read.Observation!.State);
    }

    [TestMethod]
    public async Task WriteExceptionHasUnknownAcceptanceRatherThanFalse()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend();
        backend.OnWrite = () => throw new IOException(FakeBackend.PhysicalId);
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var snapshot = await provider.DiscoverAsync(default);
        var reply = await new WorkerProtocol(provider).HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual("power_io_failed", reply.ErrorCode);
        Assert.IsNull(reply.WriteAccepted);
        Assert.IsNull(reply.Observation);
    }

    [TestMethod]
    public async Task InterruptedDiscoveryIsArchivedWithoutBlockingDiscoveryOrPowerControl()
    {
        using var directory = new TestDirectory();
        var lockPath = Path.Combine(directory.Path, "discovery.lock");
        await File.WriteAllTextAsync(lockPath, "prior interrupted discovery");
        var previousArchive = $"{lockPath}.{Guid.NewGuid():N}.previous";
        await File.WriteAllTextAsync(previousArchive, "earlier interrupted discovery");
        var backend = new FakeBackend();
        backend.OnDiscovery = () =>
        {
            using var scope = CrashDetectionScope.Begin(lockPath);
            Assert.IsTrue(File.Exists(lockPath));
        };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var protocol = new WorkerProtocol(provider);
        var reply = await protocol.HandleAsync(ProtocolFixture.Request("discover"));
        Assert.IsTrue(reply.Success, reply.ErrorCode);
        Assert.IsFalse(File.Exists(lockPath));
        var archives = Directory.GetFiles(directory.Path, "discovery.lock.*.previous");
        Assert.AreEqual(2, archives.Length);
        Assert.AreEqual("earlier interrupted discovery", await File.ReadAllTextAsync(previousArchive));
        Assert.AreEqual("prior interrupted discovery", await File.ReadAllTextAsync(archives.Single(path => path != previousArchive)));
        var fingerprint = reply.Discovery!.TopologyFingerprint;
        Assert.IsTrue((await protocol.HandleAsync(Request("read", fingerprint))).Success);
        Assert.IsTrue((await protocol.HandleAsync(Request("set", fingerprint, PowerIntent.On))).Success);
        Assert.IsTrue((await protocol.HandleAsync(ProtocolFixture.Request("discover"))).Success);
        Assert.AreEqual(1, backend.DiscoveryCalls);
    }

    [TestMethod]
    public async Task UnmovableDiscoveryLockIsPreservedWithoutBlockingDiscoveryOrPowerControl()
    {
        using var directory = new TestDirectory();
        var lockPath = Path.Combine(directory.Path, "discovery.lock");
        await File.WriteAllTextAsync(lockPath, "prior interrupted discovery");
        using var locked = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var backend = new FakeBackend();
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var protocol = new WorkerProtocol(provider);
        var discovered = await protocol.HandleAsync(ProtocolFixture.Request("discover"));
        Assert.IsTrue(discovered.Success, discovered.ErrorCode);
        Assert.AreEqual("prior interrupted discovery", await File.ReadAllTextAsync(lockPath));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "discovery.lock.*.previous").Length);
        var fingerprint = discovered.Discovery!.TopologyFingerprint;
        Assert.IsTrue((await protocol.HandleAsync(Request("read", fingerprint))).Success);
        Assert.IsTrue((await protocol.HandleAsync(Request("set", fingerprint, PowerIntent.On))).Success);
        Assert.AreEqual(1, backend.DiscoveryCalls);
        CollectionAssert.AreEqual(new[] { 1 }, backend.Writes);
    }

    [TestMethod]
    public async Task InterruptedDiscoveryRequiresNewWorkerButDoesNotQuarantineIt()
    {
        using var directory = new TestDirectory();
        var lockPath = Path.Combine(directory.Path, "discovery.lock");
        var backend = new FakeBackend();
        backend.OnDiscovery = () =>
        {
            File.WriteAllText(lockPath, "interrupted discovery");
            throw new IOException("simulated discovery failure");
        };
        using (var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null))
        {
            var protocol = new WorkerProtocol(provider);
            Assert.AreEqual("provider_failed", (await protocol.HandleAsync(ProtocolFixture.Request("discover"))).ErrorCode);
            Assert.AreEqual("discovery_restart_required", (await protocol.HandleAsync(ProtocolFixture.Request("discover"))).ErrorCode);
            Assert.AreEqual(1, backend.DiscoveryCalls);
            Assert.AreEqual("interrupted discovery", await File.ReadAllTextAsync(lockPath));
            Assert.AreEqual(0, Directory.GetFiles(directory.Path, "discovery.lock.*.previous").Length);
        }

        using var restarted = new HardwareDisplayProvider(directory.Path, () => new FakeBackend(), () => null);
        Assert.IsTrue((await new WorkerProtocol(restarted).HandleAsync(ProtocolFixture.Request("discover"))).Success);
        Assert.IsFalse(File.Exists(lockPath));
        var archive = Directory.GetFiles(directory.Path, "discovery.lock.*.previous").Single();
        Assert.AreEqual("interrupted discovery", await File.ReadAllTextAsync(archive));
    }

    [TestMethod]
    public async Task ConflictingProcessPreventsDiscoveryAndSubsequentWrites()
    {
        using var directory = new TestDirectory();
        var lockPath = Path.Combine(directory.Path, "discovery.lock");
        await File.WriteAllTextAsync(lockPath, "discovery in progress");
        var backend = new FakeBackend();
        string? conflict = "display_process_conflict";
        var factories = 0;
        using var provider = new HardwareDisplayProvider(directory.Path, () => { factories++; return backend; }, () => conflict);
        var protocol = new WorkerProtocol(provider);
        Assert.AreEqual(conflict, (await protocol.HandleAsync(ProtocolFixture.Request("discover"))).ErrorCode);
        Assert.AreEqual(0, factories);
        Assert.AreEqual("discovery in progress", await File.ReadAllTextAsync(lockPath));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "discovery.lock.*.previous").Length);
        conflict = null;
        var snapshot = await provider.DiscoverAsync(default);
        conflict = "display_process_conflict";
        var reply = await protocol.HandleAsync(Request("set", snapshot.TopologyFingerprint, PowerIntent.Off));
        Assert.AreEqual(conflict, reply.ErrorCode);
        Assert.AreEqual(false, reply.WriteAccepted);
        Assert.AreEqual(0, backend.Writes.Count);
    }

    [TestMethod]
    public async Task ConflictDetectedAfterLeaseDoesNotArchiveDiscoveryLock()
    {
        using var directory = new TestDirectory();
        var lockPath = Path.Combine(directory.Path, "discovery.lock");
        await File.WriteAllTextAsync(lockPath, "discovery in progress");
        var checks = 0;
        using var provider = new HardwareDisplayProvider(directory.Path,
            () => throw new AssertFailedException("Native factory called"),
            () => ++checks == 1 ? null : "display_process_conflict");
        var reply = await new WorkerProtocol(provider).HandleAsync(ProtocolFixture.Request("discover"));
        Assert.AreEqual("display_process_conflict", reply.ErrorCode);
        Assert.AreEqual(2, checks);
        Assert.AreEqual("discovery in progress", await File.ReadAllTextAsync(lockPath));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "discovery.lock.*.previous").Length);
    }

    [TestMethod]
    public async Task SeparateProvidersCannotDiscoverInSameDataDirectory()
    {
        using var directory = new TestDirectory();
        using var first = new HardwareDisplayProvider(directory.Path, () => new FakeBackend(), () => null);
        await first.DiscoverAsync(default);
        var lockPath = Path.Combine(directory.Path, "discovery.lock");
        await File.WriteAllTextAsync(lockPath, "discovery in progress");
        using var second = new HardwareDisplayProvider(directory.Path, () => throw new AssertFailedException("Second factory called"), () => null);
        Assert.AreEqual("worker_already_running", (await new WorkerProtocol(second).HandleAsync(ProtocolFixture.Request("discover"))).ErrorCode);
        Assert.AreEqual("discovery in progress", await File.ReadAllTextAsync(lockPath));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "discovery.lock.*.previous").Length);
    }

    [TestMethod]
    public async Task AmbiguousDiscoveryDoesNotRetryNativeDiscoveryInSameWorker()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend { Monitors = [FakeBackend.ValidMonitor(), FakeBackend.ValidMonitor()] };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var protocol = new WorkerProtocol(provider);
        Assert.AreEqual("target_not_unique_or_not_ddc", (await protocol.HandleAsync(ProtocolFixture.Request("discover"))).ErrorCode);
        Assert.AreEqual("discovery_restart_required", (await protocol.HandleAsync(ProtocolFixture.Request("discover"))).ErrorCode);
        Assert.AreEqual(1, backend.DiscoveryCalls);
    }
}
