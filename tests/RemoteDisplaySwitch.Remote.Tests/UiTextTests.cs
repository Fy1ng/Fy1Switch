using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Host.UI;

namespace RemoteDisplaySwitch.Remote.Tests;

[TestClass]
public sealed class UiTextTests
{
    [DataTestMethod]
    [DataRow(0, "刚刚")]
    [DataRow(59, "刚刚")]
    [DataRow(60, "1 分钟前")]
    [DataRow(180, "3 分钟前")]
    [DataRow(3600, "1 小时前")]
    [DataRow(86400, "1 天前")]
    public void TimestampsUseReadableRelativeLabels(int seconds, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(expected, UiText.Time(now.AddSeconds(-seconds), now));
    }

    [TestMethod]
    public void MissingTimestampDoesNotPretendToBeRecent() => Assert.AreEqual("尚无记录", UiText.Time(default));

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("read_failed")]
    [DataRow("topology_unavailable")]
    [DataRow("worker_timeout")]
    public void TransientStateFailuresCanBeAutomaticallyReadAgain(string? errorCode)
    {
        Assert.IsTrue(UiText.CanAutomaticallyRefresh(Snapshot(errorCode)));
    }

    [DataTestMethod]
    [DataRow("topology_changed")]
    [DataRow("target_mismatch")]
    [DataRow("target_ambiguous")]
    [DataRow("discovery_restart_required")]
    [DataRow("discovery_quarantined")]
    [DataRow("worker_protocol_error")]
    [DataRow("other_controller_running")]
    public void IdentityDiscoveryAndProtocolFailuresDoNotTriggerAutomaticProbing(string errorCode)
    {
        Assert.IsFalse(UiText.CanAutomaticallyRefresh(Snapshot(errorCode)));
    }

    [TestMethod]
    public void BusyMissingOrInvalidatedBindingsAreNeverAutomaticallyRefreshed()
    {
        var snapshot = Snapshot(null);
        Assert.IsFalse(UiText.CanAutomaticallyRefresh(null));
        Assert.IsFalse(UiText.CanAutomaticallyRefresh(snapshot with { Busy = true }));
        Assert.IsFalse(UiText.CanAutomaticallyRefresh(snapshot with { Binding = null }));
        Assert.IsFalse(UiText.CanAutomaticallyRefresh(snapshot with { Binding = snapshot.Binding! with { NeedsConfirmation = true } }));
    }

    private static HostSnapshot Snapshot(string? errorCode) => new(Guid.NewGuid(),
        new(Guid.NewGuid(), "target", "Monitor", "profile", "topology"),
        new(DisplayPower.Unknown, null, DateTimeOffset.UtcNow, "unavailable"), false, false, false, errorCode, false);
}
