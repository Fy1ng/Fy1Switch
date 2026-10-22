using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Host.UI;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class PairingRenewalTests
{
    [DataTestMethod]
    [DataRow("authenticating")]
    [DataRow("sending-bundle")]
    [DataRow("awaiting-android")]
    [DataRow("connecting")]
    [DataRow("rejected")]
    [DataRow("exhausted")]
    [DataRow("failed")]
    [DataRow("completed")]
    public void ExpiredQrNeverInterruptsHandshakeOrRetriesFailure(string status)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.IsFalse(PairingRenewalPolicy.ShouldRenew(status, now.AddSeconds(-1), now));
    }

    [DataTestMethod]
    [DataRow("listening")]
    [DataRow("stopped")]
    [DataRow("discovery_unavailable")]
    public void VisibleQrRenewsOnlyAtNaturalExpiry(string status)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.IsFalse(PairingRenewalPolicy.ShouldRenew(status, null, now));
        Assert.IsFalse(PairingRenewalPolicy.ShouldRenew(status, now.AddSeconds(1), now));
        Assert.IsTrue(PairingRenewalPolicy.ShouldRenew(status, now, now));
    }

    [TestMethod]
    public void ShortCodeExpiryCanRenewWhenQrExpiresLater()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(PairingRenewalPolicy.ShouldRenew("expired", now.AddSeconds(1), now));
    }
}
