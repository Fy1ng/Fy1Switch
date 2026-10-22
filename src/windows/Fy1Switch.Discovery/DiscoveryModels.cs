using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fy1Switch.Discovery;

public sealed record DiscoveredPeer(string SessionId, string Name, string Role, IPAddress Address, int Port, uint InterfaceIndex);
public sealed record DiscoveryDiagnostic(string Operation, uint? NativeStatus, string Message);
public sealed record ServiceAnnouncement(string InstanceName, uint Ttl);
public sealed record ServiceMetadata(string InstanceName, int Port, uint InterfaceIndex,
    IReadOnlyDictionary<string, string> Properties, IReadOnlyList<IPAddress> Addresses);
public sealed record LocalAdvertisement(string SessionId, string DisplayName, IPAddress Address, int Port, uint InterfaceIndex);

public interface IDnsSdAdapter
{
    Task<IAsyncDisposable> RegisterAsync(LocalAdvertisement advertisement, CancellationToken cancellationToken);
    IAsyncDisposable Browse(uint interfaceIndex, Action<ServiceAnnouncement> onAnnouncement, Action<DiscoveryDiagnostic> onError);
    Task<ServiceMetadata?> ResolveAsync(string instanceName, uint interfaceIndex, CancellationToken cancellationToken);
}

public sealed class DnsSdException(string operation, uint status)
    : Exception($"{operation} failed: {status} ({new System.ComponentModel.Win32Exception(unchecked((int)status)).Message})")
{
    public string Operation { get; } = operation;
    public uint NativeStatus { get; } = status;
}

internal static class DiscoveryMetadata
{
    internal const string ServiceType = "_fy1switch-pair._tcp.local.";
    internal static bool ValidSession(string value) => Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;

    internal static IReadOnlyList<DiscoveredPeer> Parse(ServiceMetadata metadata, string self, uint selectedInterface)
    {
        if (selectedInterface == 0 || metadata.InterfaceIndex != selectedInterface || metadata.Port is < 1 or > 65535 ||
            !metadata.InstanceName.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase) ||
            !metadata.Properties.TryGetValue("v", out var version) || version != "1" ||
            !metadata.Properties.TryGetValue("role", out var role) || role != "android" ||
            !metadata.Properties.TryGetValue("sid", out var sid) || !ValidSession(sid) || sid == self)
            return [];
        var name = metadata.InstanceName[..^(ServiceType.Length + 1)];
        return metadata.Addresses.Where(UsableAddress).Select(address =>
        {
            var scoped = address.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPAddress(address.GetAddressBytes(), address.ScopeId == 0 && address.IsIPv6LinkLocal ? selectedInterface : address.ScopeId)
                : new IPAddress(address.GetAddressBytes());
            return new DiscoveredPeer(sid, name, role, scoped, metadata.Port, selectedInterface);
        }).Distinct().OrderBy(peer => peer.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
    }

    internal static bool UsableAddress(IPAddress address) =>
        address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.Broadcast) &&
        !address.IsIPv6Multicast && !(address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239);

    internal static void ValidateInterface(IPAddress address, uint index)
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            var component = address.AddressFamily == AddressFamily.InterNetwork ? NetworkInterfaceComponent.IPv4 : NetworkInterfaceComponent.IPv6;
            if (!network.Supports(component)) continue;
            var properties = network.GetIPProperties();
            var actual = address.AddressFamily == AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
            if (actual == index && properties.UnicastAddresses.Any(item => item.Address.Equals(address))) return;
        }
        throw new ArgumentException("The local address does not belong to the selected interface.", nameof(address));
    }
}
