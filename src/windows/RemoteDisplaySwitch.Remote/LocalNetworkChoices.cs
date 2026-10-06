using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDisplaySwitch.Remote;

public sealed record LocalNetworkChoice(string Name, string Address, bool Tailscale, bool Recommended)
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
    public static IReadOnlyList<LocalNetworkChoice> Get()
    {
        var choices = new List<LocalNetworkChoice>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var tailscale = adapter.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase);
            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses.Any(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !address.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (!Eligible(unicast.Address, tailscale)) continue;
                choices.Add(new(adapter.Name, unicast.Address.ToString(), tailscale, !tailscale && gateway));
            }
        }
        return choices.DistinctBy(choice => choice.Address).OrderByDescending(choice => choice.Recommended).ThenByDescending(choice => choice.Tailscale).ThenBy(choice => choice.Name).ToArray();
    }
    public static bool Eligible(IPAddress address, bool tailscale)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (tailscale) return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }
}
