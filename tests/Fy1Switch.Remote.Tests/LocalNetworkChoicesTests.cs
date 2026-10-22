using System.Net;
using System.Net.NetworkInformation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Remote;

namespace Fy1Switch.Remote.Tests;

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

    [TestMethod]
    public void LanRecommendationDoesNotDependOnAdapterEnumerationOrder()
    {
        var wifi = new LocalNetworkChoice("Wi-Fi", "192.168.1.20", false, true, NetworkInterfaceType.Wireless80211);
        var ethernet = new LocalNetworkChoice("Ethernet", "10.0.0.20", false, false, NetworkInterfaceType.Ethernet);
        var tailscale = new LocalNetworkChoice("Tailscale", "100.70.1.2", true, false);
        LocalNetworkChoice[] choices = [ethernet, tailscale, wifi];

        Assert.AreEqual(wifi, LocalNetworkChoices.SelectLan(choices));
        Assert.AreEqual(wifi, LocalNetworkChoices.SelectLan(choices.Reverse().ToArray()));
    }

    [TestMethod]
    public void ValidPreferredLanAddressIsPreserved()
    {
        var wifi = new LocalNetworkChoice("Wi-Fi", "192.168.1.20", false, true, NetworkInterfaceType.Wireless80211);
        var ethernet = new LocalNetworkChoice("Ethernet", "10.0.0.20", false, false, NetworkInterfaceType.Ethernet);
        LocalNetworkChoice[] choices = [wifi, ethernet];

        Assert.AreEqual(ethernet, LocalNetworkChoices.SelectLan(choices, ethernet.Address));
        Assert.AreEqual(wifi, LocalNetworkChoices.SelectLan(choices, "192.168.1.99"));
    }

    [TestMethod]
    public void TailscaleCannotReplaceLanRecommendation()
    {
        var wifi = new LocalNetworkChoice("Wi-Fi", "192.168.1.20", false, false);
        var tailscale = new LocalNetworkChoice("Tailscale", "100.70.1.2", true, true);
        LocalNetworkChoice[] choices = [tailscale, wifi];

        Assert.AreEqual(wifi, LocalNetworkChoices.SelectLan(choices, tailscale.Address));
        Assert.AreEqual(tailscale, LocalNetworkChoices.SelectTailscale(choices, wifi.Address));
    }

    [TestMethod]
    public void MissingOrInvalidLanCandidatesProduceNoAutomaticSelection()
    {
        LocalNetworkChoice[] choices =
        [
            new("Tailscale", "100.70.1.2", true, false),
            new("Loopback", "127.0.0.1", false, true),
            new("Unassigned", "169.254.1.2", false, true),
            new("Public", "203.0.113.1", false, true),
            new("Invalid", "not-an-address", false, true)
        ];

        Assert.IsNull(LocalNetworkChoices.SelectLan(choices, "127.0.0.1"));
        Assert.IsNull(LocalNetworkChoices.SelectLan([]));
        Assert.IsNull(LocalNetworkChoices.SelectTailscale([]));
    }

    [TestMethod]
    public void PhysicalInterfacesTakePriorityOverVirtualAndVpnInterfaces()
    {
        var wifi = new LocalNetworkChoice("Wi-Fi", "192.168.1.20", false, false, NetworkInterfaceType.Wireless80211);
        LocalNetworkChoice[] choices =
        [
            new("vEthernet (Default Switch)", "172.20.0.1", false, true, NetworkInterfaceType.Ethernet),
            new("Corporate VPN", "10.0.0.2", false, true, NetworkInterfaceType.Ethernet),
            new("Custom interface", "10.1.0.2", false, true, NetworkInterfaceType.Tunnel),
            new("Custom bridge", "10.2.0.2", false, true, NetworkInterfaceType.Ethernet, true),
            wifi
        ];

        Assert.AreEqual(wifi, LocalNetworkChoices.SelectLan(choices));
        Assert.IsNull(LocalNetworkChoices.SelectLan(choices[..^1]));
        Assert.AreEqual(choices[0], LocalNetworkChoices.SelectLan(choices, choices[0].Address));
    }

    [TestMethod]
    public void TailscaleSelectionIsDeterministicAndPreservesPreferredAddress()
    {
        var first = new LocalNetworkChoice("Tailscale", "100.70.1.2", true, false);
        var second = new LocalNetworkChoice("Tailscale", "100.70.1.3", true, false);
        LocalNetworkChoice[] choices = [second, first];

        Assert.AreEqual(first, LocalNetworkChoices.SelectTailscale(choices));
        Assert.AreEqual(first, LocalNetworkChoices.SelectTailscale(choices.Reverse().ToArray()));
        Assert.AreEqual(second, LocalNetworkChoices.SelectTailscale(choices, second.Address));
        Assert.IsNull(LocalNetworkChoices.SelectTailscale([new("Tailscale", "192.168.1.20", true, true)]));
    }
}
