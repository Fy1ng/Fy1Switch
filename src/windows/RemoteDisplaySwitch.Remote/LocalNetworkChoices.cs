using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDisplaySwitch.Remote;

public sealed record LocalNetworkChoice(
    string Name,
    string Address,
    bool Tailscale,
    bool Recommended,
    NetworkInterfaceType InterfaceType = NetworkInterfaceType.Unknown,
    bool Virtual = false)
{
    public string Label => $"{(Tailscale ? "Tailscale" : "局域网")} · {Name} · {Address}";
    public RemoteSettings Apply(RemoteSettings current) => Tailscale ? current with
    {
        TailscaleListenAddress = Address,
        TailscaleEndpoint = new UriBuilder("https", Address, current.Port).Uri.GetLeftPart(UriPartial.Authority)
    } : current with
    {
        ListenAddress = Address,
        PublicEndpoint = new UriBuilder("https", Address, current.Port).Uri.GetLeftPart(UriPartial.Authority)
    };
}

public static class LocalNetworkChoices
{
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "vpn", "hyper-v", "vethernet", "vmware", "vbox", "docker", "wsl",
        "wintun", "wireguard", "openvpn", "tap-windows", "tap adapter", "hamachi", "zerotier",
        "loopback", "npcap", "anyconnect", "globalprotect", "fortinet", "forticlient", "pulse secure"
    ];

    public static IReadOnlyList<LocalNetworkChoice> Get()
    {
        var choices = new List<LocalNetworkChoice>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var tailscale = adapter.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase);
            var virtualAdapter = IsVirtualAdapter(adapter.NetworkInterfaceType, adapter.Name, adapter.Description);
            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses.Any(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !address.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (!Eligible(unicast.Address, tailscale)) continue;
                var recommended = !tailscale && !virtualAdapter && IsPhysical(adapter.NetworkInterfaceType) && gateway;
                choices.Add(new(adapter.Name, unicast.Address.ToString(), tailscale, recommended, adapter.NetworkInterfaceType, virtualAdapter));
            }
        }
        return Ordered(choices).DistinctBy(choice => choice.Address).ToArray();
    }

    public static LocalNetworkChoice? SelectLan(IReadOnlyList<LocalNetworkChoice> choices, string? preferredAddress = null)
    {
        var candidates = choices.Where(choice => !choice.Tailscale && IsEligible(choice));
        var preferred = FindPreferred(candidates, preferredAddress);
        return preferred ?? Ordered(candidates.Where(choice => !IsVirtual(choice))).FirstOrDefault();
    }

    public static LocalNetworkChoice? SelectTailscale(IReadOnlyList<LocalNetworkChoice> choices, string? preferredAddress = null)
    {
        var candidates = choices.Where(choice => choice.Tailscale && IsEligible(choice));
        return FindPreferred(candidates, preferredAddress) ?? Ordered(candidates).FirstOrDefault();
    }

    private static LocalNetworkChoice? FindPreferred(IEnumerable<LocalNetworkChoice> choices, string? preferredAddress) =>
        IPAddress.TryParse(preferredAddress, out var preferred)
            ? choices.FirstOrDefault(choice => IPAddress.TryParse(choice.Address, out var address) && address.Equals(preferred))
            : null;

    private static bool IsEligible(LocalNetworkChoice choice) =>
        IPAddress.TryParse(choice.Address, out var address) && Eligible(address, choice.Tailscale);

    private static bool IsPhysical(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;

    private static bool IsVirtual(LocalNetworkChoice choice) => choice.Virtual || IsVirtualAdapter(choice.InterfaceType, choice.Name, "");

    private static bool IsVirtualAdapter(NetworkInterfaceType type, string name, string description) =>
        type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or NetworkInterfaceType.Loopback ||
        VirtualAdapterMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase) || description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static IOrderedEnumerable<LocalNetworkChoice> Ordered(IEnumerable<LocalNetworkChoice> choices) => choices
        .OrderBy(choice => choice.Tailscale)
        .ThenBy(IsVirtual)
        .ThenByDescending(choice => IsPhysical(choice.InterfaceType))
        .ThenByDescending(choice => choice.Recommended)
        .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(choice => choice.Address, StringComparer.Ordinal);

    public static bool Eligible(IPAddress address, bool tailscale)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (tailscale) return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }
}
