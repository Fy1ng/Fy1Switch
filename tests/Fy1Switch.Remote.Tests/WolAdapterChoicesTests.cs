using System.Net.NetworkInformation;
using Fy1Switch.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class WolAdapterChoicesTests
{
    internal static WolAdapterChoice Adapter(string id = "wired-1", string name = "Ethernet", string mac = "02:11:22:33:44:55",
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet, OperationalStatus status = OperationalStatus.Up,
        string description = "Realtek PCIe 5GbE Family Controller") => new(id, name, description, mac, type, status);

    [DataTestMethod]
    [DataRow("02:11:22:33:44:55", "02:11:22:33:44:55")]
    [DataRow("aA-bb-cC-dd-eE-ff", "AA:BB:CC:DD:EE:FF")]
    public void ValidUnicastMacIsNormalized(string input, string expected)
    {
        Assert.IsTrue(WolAdapterChoices.TryNormalizeMac(input, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("00:00:00:00:00:00")]
    [DataRow("FF:FF:FF:FF:FF:FF")]
    [DataRow("01:11:22:33:44:55")]
    [DataRow("02:11:22:33:44:GG")]
    [DataRow("02:11-22:33:44:55")]
    [DataRow("021122334455")]
    [DataRow("02:11:22:33:44:55;id")]
    [DataRow("02:11:22:33:44: 5")]
    public void InvalidBroadcastOrMulticastMacIsRejected(string? input)
    {
        Assert.IsFalse(WolAdapterChoices.TryNormalizeMac(input, out _));
    }

    [TestMethod]
    public void DisconnectedWiredAdapterIsEligibleWithoutAnIpAddress()
    {
        var down = Adapter(status: OperationalStatus.Down);
        var choices = WolAdapterChoices.Eligible([down]);
        Assert.AreEqual(down, choices.Single());
    }

    [TestMethod]
    public void WirelessVirtualTunnelAndInvalidMacAreExcluded()
    {
        WolAdapterChoice[] candidates =
        [
            Adapter("wifi", type: NetworkInterfaceType.Wireless80211),
            Adapter("hyperv", name: "vEthernet (Default Switch)"),
            Adapter("virtual", description: "VMware Virtual Ethernet Adapter"),
            Adapter("tunnel", type: NetworkInterfaceType.Tunnel),
            Adapter("vpn", description: "Tailscale Tunnel"),
            Adapter("badmac", mac: "00:00:00:00:00:00"),
            Adapter("", name: "No ID"),
            Adapter("good", status: OperationalStatus.Down)
        ];
        Assert.AreEqual("good", WolAdapterChoices.Eligible(candidates).Single().AdapterId);
    }

    [TestMethod]
    public void ChoicesAreDeterministicAndDistinctByAdapterId()
    {
        var first = Adapter("id-a", "Ethernet A");
        var second = Adapter("id-b", "Ethernet B");
        CollectionAssert.AreEqual(new[] { first, second }, WolAdapterChoices.Eligible([second, first, first]).ToArray());
    }

    [TestMethod]
    public void GigabitAndFastEthernetAreEligible()
    {
        Assert.AreEqual(3, WolAdapterChoices.Eligible([
            Adapter("one", type: NetworkInterfaceType.GigabitEthernet),
            Adapter("two", type: NetworkInterfaceType.FastEthernetT),
            Adapter("three", type: NetworkInterfaceType.FastEthernetFx)]).Count);
    }
}
