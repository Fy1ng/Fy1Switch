using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Remote;

namespace RemoteDisplaySwitch.Remote.Tests;

[TestClass]
public class LocalNetworkChoicesTests
{
    [TestMethod]
    [DataRow("127.0.0.1", false, false)]
    [DataRow("0.0.0.0", false, false)]
    [DataRow("169.254.1.2", false, false)]
    [DataRow("198.18.0.1", false, false)]
    [DataRow("192.168.1.10", false, true)]
    [DataRow("172.16.1.2", false, true)]
    [DataRow("10.0.0.2", false, true)]
    [DataRow("100.70.1.2", false, false)]
    [DataRow("100.70.1.2", true, true)]
    [DataRow("::1", false, false)]
    public void OnlyAppropriateAddressesAreRecommended(string value, bool tailscale, bool expected) =>
        Assert.AreEqual(expected, LocalNetworkChoices.Eligible(IPAddress.Parse(value), tailscale));

    [TestMethod]
    public void ExplicitChoiceChangesOnlyAddressFields()
    {
        var current = new RemoteSettings(true, "127.0.0.1", 49152, "https://127.0.0.1:49152", "https://relay.example", "protected-token");
        var updated = new LocalNetworkChoice("fixture", "192.168.1.20", false, true).Apply(current);
        Assert.AreEqual("192.168.1.20", updated.ListenAddress);
        Assert.AreEqual("https://192.168.1.20:49152", updated.PublicEndpoint);
        Assert.AreEqual(current.Port, updated.Port);
        Assert.AreEqual(current.ProtectedRelayToken, updated.ProtectedRelayToken);
        Assert.AreEqual(current.RelayUrl, updated.RelayUrl);
        Assert.AreEqual(current.Enabled, updated.Enabled);
        Assert.AreEqual("127.0.0.1", current.ListenAddress);
    }
}
