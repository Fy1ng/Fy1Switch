using System.Net;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Discovery.Tests;

[TestClass]
public class MetadataTests
{
    internal const string Self = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    internal const string Peer = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    internal const string Name = "Phone._fy1switch-pair._tcp.local.";
    internal static ServiceMetadata Metadata(string name = Name, int port = 43210) => new(name, port, 7,
        new Dictionary<string, string> { ["v"] = "1", ["role"] = "android", ["sid"] = Peer },
        [IPAddress.Parse("fe80::1234"), IPAddress.Parse("192.0.2.2"), IPAddress.Parse("192.0.2.2")]);

    [TestMethod]
    public void PrefersIpv4DeduplicatesAndScopesLinkLocal()
    {
        var peers = DiscoveryMetadata.Parse(Metadata(), Self, 7);
        Assert.AreEqual(2, peers.Count);
        Assert.AreEqual("192.0.2.2", peers[0].Address.ToString());
        Assert.AreEqual("fe80::1234%7", peers[1].Address.ToString());
        Assert.AreEqual(43210, peers[0].Port);
        Assert.AreEqual("Phone", peers[0].Name);
        var scoped = DiscoveryMetadata.Parse(Metadata() with { Addresses = [IPAddress.Parse("fe80::1234%7")] }, Self, 7);
        Assert.AreEqual(7L, scoped[0].Address.ScopeId);
    }

    [TestMethod]
    [DataRow("v", "2")]
    [DataRow("v", "01")]
    [DataRow("role", "windows")]
    [DataRow("role", "Android")]
    [DataRow("role", "unknown")]
    [DataRow("sid", Self)]
    [DataRow("sid", "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB")]
    [DataRow("sid", "garbage")]
    public void RejectsInvalidProperties(string key, string value)
    {
        var properties = new Dictionary<string, string>(Metadata().Properties) { [key] = value };
        Assert.AreEqual(0, DiscoveryMetadata.Parse(Metadata() with { Properties = properties }, Self, 7).Count);
    }

    [TestMethod]
    public void RejectsMissingSidInvalidPortsAndWrongInterface()
    {
        var properties = new Dictionary<string, string>(Metadata().Properties);
        properties.Remove("sid");
        Assert.AreEqual(0, DiscoveryMetadata.Parse(Metadata() with { Properties = properties }, Self, 7).Count);
        foreach (var port in new[] { -1, 0, 65536 }) Assert.AreEqual(0, DiscoveryMetadata.Parse(Metadata(port: port), Self, 7).Count);
        Assert.AreEqual(0, DiscoveryMetadata.Parse(Metadata(), Self, 8).Count);
        Assert.AreEqual(0, DiscoveryMetadata.Parse(Metadata() with { InterfaceIndex = 0 }, Self, 7).Count);
        Assert.AreEqual(0, DiscoveryMetadata.Parse(Metadata("Phone.example.com."), Self, 7).Count);
    }

    [TestMethod]
    public void SdkLayoutsMatchPointerAlignment()
    {
        Assert.AreEqual(nint.Size == 8 ? 32 : 20, Marshal.SizeOf<DnsNative.QueryRequest>());
        Assert.AreEqual(nint.Size == 8 ? 48 : 28, Marshal.SizeOf<DnsNative.RegisterRequest>());
        Assert.AreEqual(nint.Size == 8 ? 72 : 40, Marshal.SizeOf<DnsNative.Instance>());
        Assert.AreEqual(nint.Size == 8 ? 32 : 24, Marshal.SizeOf<DnsNative.RecordHeader>());
        Assert.AreEqual(nint.Size == 8 ? 32 : 16, Marshal.OffsetOf<DnsNative.Instance>("Port").ToInt32());
        Assert.AreEqual(nint.Size == 8 ? 64 : 36, Marshal.OffsetOf<DnsNative.Instance>("InterfaceIndex").ToInt32());
    }

    [TestMethod]
    public void ReadsNativeInstanceWithoutNetworkAndRejectsDuplicateTxt()
    {
        using var memory = new NativeMemory();
        var ipv4 = memory.Allocate(4);
        Marshal.Copy(new byte[] { 192, 0, 2, 10 }, 0, ipv4, 4);
        var data = new DnsNative.Instance
        {
            Name = memory.String(Name), IPv4 = ipv4, Port = 43210, InterfaceIndex = 7,
            PropertyCount = 3, Keys = memory.Strings("v", "role", "sid"), Values = memory.Strings("1", "android", Peer)
        };
        var instance = memory.Structure(data);
        var metadata = WindowsDnsSdAdapter.ReadInstance(instance, 7)!;
        Assert.AreEqual(43210, metadata.Port);
        Assert.AreEqual("192.0.2.10", metadata.Addresses[0].ToString());
        Assert.IsNull(WindowsDnsSdAdapter.ReadInstance(instance, 8));
        data.Keys = memory.Strings("v", "V", "sid");
        Marshal.StructureToPtr(data, instance, false);
        Assert.IsNull(WindowsDnsSdAdapter.ReadInstance(instance, 7));
    }

    [TestMethod]
    public void PtrRecordIncludesGoodbyeTtlWithoutNetwork()
    {
        using var memory = new NativeMemory();
        var record = memory.Allocate(Marshal.SizeOf<DnsNative.RecordHeader>() + nint.Size);
        Marshal.StructureToPtr(new DnsNative.RecordHeader
        {
            Name = memory.String(DiscoveryMetadata.ServiceType), Type = 12, DataLength = (ushort)nint.Size, Ttl = 0
        }, record, false);
        Marshal.WriteIntPtr(record, Marshal.SizeOf<DnsNative.RecordHeader>(), memory.String(Name));
        var records = NativeBrowse.ReadRecords(record);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(new ServiceAnnouncement(Name, 0), records[0]);
    }

    [TestMethod]
    public void WindowsExportsArePresentWithoutInvokingDiscovery()
    {
        var library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "dnsapi.dll"));
        try
        {
            foreach (var export in new[] { "DnsServiceBrowse", "DnsServiceBrowseCancel", "DnsServiceResolve", "DnsServiceResolveCancel", "DnsServiceRegister", "DnsServiceDeRegister", "DnsServiceRegisterCancel", "DnsServiceConstructInstance", "DnsServiceFreeInstance", "DnsRecordListFree" })
                Assert.IsTrue(NativeLibrary.TryGetExport(library, export, out _), export);
        }
        finally { NativeLibrary.Free(library); }
    }

    [TestMethod]
    public void PtrHonorsUtf8DeleteFlagAndUnusedDataLength()
    {
        using var memory = new NativeMemory();
        nint Utf8(string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
            var result = memory.Allocate(bytes.Length);
            Marshal.Copy(bytes, 0, result, bytes.Length);
            return result;
        }
        var record = memory.Allocate(Marshal.SizeOf<DnsNative.RecordHeader>() + nint.Size);
        Marshal.StructureToPtr(new DnsNative.RecordHeader
        {
            Name = Utf8(DiscoveryMetadata.ServiceType), Type = 12, DataLength = 0, Ttl = 120, Flags = (2 << 3) | 4
        }, record, false);
        Marshal.WriteIntPtr(record, Marshal.SizeOf<DnsNative.RecordHeader>(), Utf8("手机._fy1switch-pair._tcp.local."));
        Assert.AreEqual(new ServiceAnnouncement("手机._fy1switch-pair._tcp.local.", 0), NativeBrowse.ReadRecords(record).Single());
    }

    [TestMethod]
    public void RejectsNonUnicastAddresses()
    {
        var data = Metadata() with { Addresses = [IPAddress.Any, IPAddress.IPv6Any, IPAddress.Broadcast, IPAddress.Parse("224.0.0.251"), IPAddress.Parse("ff02::fb")] };
        Assert.AreEqual(0, DiscoveryMetadata.Parse(data, Self, 7).Count);
    }
}
