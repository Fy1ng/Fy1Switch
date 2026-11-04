using System.Net;
using System.Text.Json;

namespace Fy1Switch.WolGateway;

public sealed record WolDevice(Guid DeviceId, string Name, string MacAddress, string Interface);
public sealed record SshSettings(string Host, string User, string IdentityFile, string KnownHostsFile, string BindAddress,
    string WireGuardInterface, int Port = 22, int ConnectTimeoutSeconds = 5, int ExecutionTimeoutSeconds = 15, string Executable = "/usr/bin/ssh");
public sealed record GatewaySettings(string PublicUrl, string DataDirectory, SshSettings Ssh, IReadOnlyList<WolDevice> Devices,
    int DeviceCooldownSeconds = 30, int RequestsPerMinute = 120, int ClientRequestsPerMinute = 60)
{
    public static GatewaySettings Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("FY1SWITCH_WOL_CONFIG must be an absolute file path.");
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException();
            var settings = JsonSerializer.Deserialize<GatewaySettings>(stream, WolJson.Options) ?? throw new JsonException();
            settings.Validate();
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("Cannot load WOL configuration; check access and schema. No configuration values are logged.");
        }
    }

    public void Validate()
    {
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || PublicUrl != uri.GetLeftPart(UriPartial.Authority) ||
            !AbsolutePath(DataDirectory) || Devices is not { Count: > 0 and <= 64 } || Ssh is null ||
            DeviceCooldownSeconds is < 1 or > 3600 || RequestsPerMinute is < 10 or > 600 || ClientRequestsPerMinute is < 5 or > 120)
            throw new InvalidOperationException("Invalid WOL origin, data path, devices, or limits.");
        if (Devices.Any(x => x is null || x.DeviceId == Guid.Empty || !WolValidation.ValidName(x.Name) || !WolValidation.ValidMac(x.MacAddress) || !WolValidation.ValidInterface(x.Interface)) ||
            Devices.Select(x => x.DeviceId).Distinct().Count() != Devices.Count ||
            Devices.Select(x => (x.Interface, x.MacAddress.ToUpperInvariant())).Distinct().Count() != Devices.Count)
            throw new InvalidOperationException("WOL devices require unique UUIDs and interface/MAC pairs, valid unicast MACs, and names.");
        if (!PrivateAddress(Ssh.Host) || !PrivateAddress(Ssh.BindAddress) || Ssh.Host == Ssh.BindAddress ||
            IPAddress.Parse(Ssh.Host).AddressFamily != IPAddress.Parse(Ssh.BindAddress).AddressFamily ||
            !WolValidation.ValidInterface(Ssh.WireGuardInterface) || Ssh.User is not { Length: > 0 and <= 32 } ||
            !char.IsAsciiLetter(Ssh.User[0]) || Ssh.User.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-') ||
            !AbsolutePath(Ssh.IdentityFile) || !AbsolutePath(Ssh.KnownHostsFile) || !AbsolutePath(Ssh.Executable) ||
            Ssh.KnownHostsFile.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\0') ||
            Ssh.Port is < 1 or > 65535 || Ssh.ConnectTimeoutSeconds is < 1 or > 15 || Ssh.ExecutionTimeoutSeconds is < 2 or > 45 ||
            Ssh.ExecutionTimeoutSeconds <= Ssh.ConnectTimeoutSeconds)
            throw new InvalidOperationException("Invalid SSH paths, WireGuard private addresses, account, or timeout limits.");
    }

    private static bool AbsolutePath(string? value) => !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value);

    private static bool PrivateAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.ToString() != value) return false;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }
}
