using Microsoft.VisualStudio.TestTools.UnitTesting;
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
}
