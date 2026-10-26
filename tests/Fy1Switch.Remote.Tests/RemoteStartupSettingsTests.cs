using System.Net.NetworkInformation;
using Fy1Switch.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class RemoteStartupSettingsTests
{
    private static readonly LocalNetworkChoice Wifi = new("Wi-Fi", "192.168.1.20", false, true, NetworkInterfaceType.Wireless80211);
    private static readonly LocalNetworkChoice Ethernet = new("Ethernet", "10.0.0.20", false, false, NetworkInterfaceType.Ethernet);

    [TestMethod]
    public void NewSettingsEnableControlAndSelectPhysicalPrivateNetwork()
    {
        var saved = new RemoteSettings();
        LocalNetworkChoice[] networks =
        [
            new("Tailscale", "100.70.1.2", true, true),
            new("vEthernet", "172.20.0.1", false, true, NetworkInterfaceType.Ethernet),
            new("Public", "203.0.113.1", false, true, NetworkInterfaceType.Ethernet),
            Ethernet,
            Wifi
        ];

        var prepared = RemoteStartupSettings.Prepare(saved, networks);

        Assert.IsTrue(prepared.Enabled);
        Assert.AreEqual(true, prepared.AutomaticNetwork);
        Assert.AreEqual(Wifi.Address, prepared.ListenAddress);
        Assert.AreEqual("https://192.168.1.20:48152", prepared.PublicEndpoint);
        prepared.Validate();
        Assert.IsFalse(saved.Enabled);
        Assert.AreEqual("127.0.0.1", saved.ListenAddress);
    }

    [TestMethod]
    public void AutomaticSettingsKeepTheEligiblePreferredAddressAndConfiguredPort()
    {
        var saved = new RemoteSettings(false, Ethernet.Address, 49200, "https://old.example:49200", AutomaticNetwork: true);

        var prepared = RemoteStartupSettings.Prepare(saved, [Wifi, Ethernet]);

        Assert.AreEqual(saved with { Enabled = true, PublicEndpoint = "https://10.0.0.20:49200" }, prepared);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(null)]
    public void CustomLanAndPublicEndpointRemainUnchanged(bool? automatic)
    {
        var saved = new RemoteSettings(false, "192.168.10.45", 49443, "https://desktop.example:4443", AutomaticNetwork: automatic);

        var prepared = RemoteStartupSettings.Prepare(saved, [Wifi, Ethernet]);

        Assert.AreEqual(saved with { Enabled = true }, prepared);
    }

    [TestMethod]
    public void ExplicitManualLoopbackIsNotReinterpretedAsAutomatic()
    {
        var saved = new RemoteSettings(AutomaticNetwork: false);

        Assert.AreEqual(saved with { Enabled = true }, RemoteStartupSettings.Prepare(saved, [Wifi]));
    }

    [TestMethod]
    public void LegacyLoopbackWithCustomPublicEndpointRemainsManual()
    {
        var saved = new RemoteSettings(PublicEndpoint: "https://desktop.example:48152");

        Assert.AreEqual(saved with { Enabled = true }, RemoteStartupSettings.Prepare(saved, [Wifi]));
    }

    [TestMethod]
    public void NoEligibleLanStartsOnLoopbackAndKeepsAutomaticDetectionEnabled()
    {
        var saved = new RemoteSettings(false, "192.168.99.20", 49200, "https://192.168.99.20:49200", AutomaticNetwork: true);
        LocalNetworkChoice[] networks =
        [
            new("Tailscale", "100.70.1.2", true, true),
            new("Virtual bridge", "172.20.0.1", false, true, NetworkInterfaceType.Ethernet, true),
            new("Loopback", "127.0.0.1", false, true),
            new("Unassigned", "169.254.1.2", false, true),
            new("Invalid", "not-an-address", false, true)
        ];

        var prepared = RemoteStartupSettings.Prepare(saved, networks);

        Assert.AreEqual(saved with { Enabled = true, ListenAddress = "127.0.0.1", PublicEndpoint = "https://127.0.0.1:49200" }, prepared);
        prepared.Validate();
        var reconnected = RemoteStartupSettings.Prepare(prepared, [Wifi]);
        Assert.AreEqual(Wifi.Address, reconnected.ListenAddress);
        Assert.IsTrue(reconnected.Enabled);
    }

    [TestMethod]
    public void EmptyNetworkListStillEnablesNewSettingsOnLoopback()
    {
        var prepared = RemoteStartupSettings.Prepare(new(), []);

        Assert.AreEqual(new RemoteSettings(Enabled: true, AutomaticNetwork: true), prepared);
        prepared.Validate();
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(null, null)]
    public void OptionalPathsCredentialsAndFlagsArePreserved(bool? tailscaleEnabled, bool? relayEnabled)
    {
        var saved = new RemoteSettings(false, "127.0.0.1", 49200, "https://127.0.0.1:49200",
            "https://relay.example", "fixture-protected-token", "https://100.70.1.2:49200", "100.70.1.2",
            AutomaticNetwork: true, TailscaleEnabled: tailscaleEnabled, RelayEnabled: relayEnabled);

        var prepared = RemoteStartupSettings.Prepare(saved, [Wifi]);

        Assert.AreEqual(saved with { Enabled = true, ListenAddress = Wifi.Address, PublicEndpoint = "https://192.168.1.20:49200" }, prepared);
        prepared.Validate();
    }
}
