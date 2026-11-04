using System.Net.NetworkInformation;

namespace Fy1Switch.Remote;

/// <summary>A candidate adapter, not proof that firmware or Windows permits WOL.</summary>
public sealed record WolAdapterChoice(string AdapterId, string Name, string Description, string MacAddress,
    NetworkInterfaceType InterfaceType, OperationalStatus Status)
{
    public string Label => $"{Name} | {MacAddress} | {(Status == OperationalStatus.Up ? "已连接" : "未连接")}";
}

public static class WolAdapterChoices
{
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vpn", "hyper-v", "vethernet", "vmware", "vbox", "docker", "wsl",
        "wintun", "wireguard", "tailscale", "openvpn", "tap-windows", "tap adapter",
        "hamachi", "zerotier", "loopback", "npcap", "anyconnect", "globalprotect",
        "fortinet", "forticlient", "pulse secure"
    ];

    public static IReadOnlyList<WolAdapterChoice> Get()
    {
        var adapters = new List<WolAdapterChoice>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var mac = adapter.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length != 6) continue;
                adapters.Add(new(adapter.Id, adapter.Name, adapter.Description,
                    string.Join(":", mac.Select(value => value.ToString("X2"))),
                    adapter.NetworkInterfaceType, adapter.OperationalStatus));
            }
            catch (NetworkInformationException) { /* A disappearing adapter must not hide the other candidates. */ }
        }
        return Eligible(adapters);
    }

    public static IReadOnlyList<WolAdapterChoice> Eligible(IEnumerable<WolAdapterChoice> adapters) => adapters
        .Where(adapter => !string.IsNullOrWhiteSpace(adapter.AdapterId) && !string.IsNullOrWhiteSpace(adapter.Name)
            && adapter.InterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            && TryNormalizeMac(adapter.MacAddress, out _)
            && !VirtualMarkers.Any(marker => adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        .Select(adapter => adapter with { MacAddress = NormalizeMac(adapter.MacAddress) })
        .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(adapter => adapter.AdapterId, StringComparer.Ordinal)
        .DistinctBy(adapter => adapter.AdapterId, StringComparer.Ordinal)
        .ToArray();

    public static bool TryNormalizeMac(string? value, out string normalized)
    {
        normalized = "";
        if (value is not { Length: 17 } || value[2] is not (':' or '-')) return false;
        var separator = value[2];
        var bytes = new byte[6];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0 && value[index * 3 - 1] != separator) return false;
            if (!byte.TryParse(value.AsSpan(index * 3, 2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out bytes[index])) return false;
        }
        if (bytes.All(value => value == 0) || (bytes[0] & 1) != 0) return false;
        normalized = string.Join(":", bytes.Select(value => value.ToString("X2")));
        return true;
    }

    public static string NormalizeMac(string value) => TryNormalizeMac(value, out var normalized)
        ? normalized : throw new ArgumentException("invalid_wol_mac");
}
