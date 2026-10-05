using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.PowerDisplay;
using RemoteDisplaySwitch.Worker;

namespace RemoteDisplaySwitch.Worker.Tests;

[TestClass]
public class ReadbackRecoveryTests
{
    [TestMethod]
    public async Task TransientReadFailureRetriesOnlyReadNotPowerWrite()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend { Reading = new(false, null, 50, DateTimeOffset.UtcNow) };
        backend.OnRead = () => { if (backend.ReadCalls == 2) backend.Reading = new(true, 2, null, DateTimeOffset.UtcNow); };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(CancellationToken.None);
        var result = await provider.SetAsync(FakeBackend.TargetKey, discovery.TopologyFingerprint, PowerIntent.Off, CancellationToken.None);
        Assert.IsTrue(result.WriteAccepted);
        Assert.AreEqual(DisplayPower.Off, result.Observation.State);
        Assert.AreEqual(2, backend.ReadCalls);
        CollectionAssert.AreEqual(new[] { 4 }, backend.Writes);
    }
    [TestMethod]
    public async Task ReadRetriesAreBoundedAndDoNotClaimConfirmation()
    {
        using var directory = new TestDirectory();
        var backend = new FakeBackend { Reading = new(false, null, 50, DateTimeOffset.UtcNow) };
        using var provider = new HardwareDisplayProvider(directory.Path, () => backend, () => null);
        var discovery = await provider.DiscoverAsync(CancellationToken.None);
        var result = await provider.SetAsync(FakeBackend.TargetKey, discovery.TopologyFingerprint, PowerIntent.On, CancellationToken.None);
        Assert.IsTrue(result.WriteAccepted);
        Assert.AreEqual(DisplayPower.Unknown, result.Observation.State);
        Assert.AreEqual(3, backend.ReadCalls);
        CollectionAssert.AreEqual(new[] { 1 }, backend.Writes);
    }
}
