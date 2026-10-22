using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Common.Services;
using PowerDisplay.Models;

namespace PowerDisplay.UnitTests;

[TestClass]
public sealed class MonitorBlacklistResourceTests
{
    [TestMethod]
    public void EmbeddedResource_ContainsPinnedUpstreamBlacklist()
    {
        using var stream = typeof(BuiltInMonitorBlacklist).Assembly.GetManifestResourceStream(
            "PowerDisplay.Models.BuiltInMonitorBlacklist.json");

        Assert.IsNotNull(stream, "The embedded resource must not silently fall back to an empty blacklist.");
        using var document = JsonDocument.Parse(stream);
        Assert.AreEqual(1, document.RootElement.GetProperty("version").GetInt32());
        CollectionAssert.AreEquivalent(
            new[] { "LTM2C02", "GSM7714" },
            BuiltInMonitorBlacklist.Entries.Select(entry => entry.EdidId).ToArray());
    }

    [DataTestMethod]
    [DataRow(@"\\?\DISPLAY#LTM2C02#5&fixture&0&UID111")]
    [DataRow(@"\\?\display#ltm2c02#5&fixture&0&uid222")]
    [DataRow(@"\\?\DISPLAY#LTM2C02#5&fixture&0&UID333#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    [DataRow(@"\\?\DISPLAY#GSM7714#5&fixture&0&UID111")]
    [DataRow(@"\\?\display#gsm7714#5&fixture&0&uid222")]
    [DataRow(@"\\?\DISPLAY#GSM7714#5&fixture&0&UID333#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    public void IsBlocked_KnownModelAcrossCasingAndPorts_ReturnsTrue(string monitorId)
    {
        var service = new MonitorBlacklistService();

        Assert.IsTrue(service.IsBlocked(monitorId));
    }
}
