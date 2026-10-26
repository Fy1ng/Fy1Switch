using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fy1Switch.Relay;

public sealed class RelaySettings
{
    private readonly Dictionary<Guid, DeviceCredentials> devices;
    private readonly byte[] missingHash = SHA256.HashData("unconfigured-device"u8);

    private RelaySettings(Dictionary<Guid, DeviceCredentials> devices, double timeoutSeconds, int requestsPerMinute, RelayManagementOptions? management)
    {
        this.devices = devices;
        RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        RequestsPerMinute = requestsPerMinute;
        Management = management;
    }

    public IEnumerable<Guid> DeviceIds => devices.Keys;
    public int DeviceCount => devices.Count;
    public TimeSpan RequestTimeout { get; }
    public int RequestsPerMinute { get; }
    public RelayManagementOptions? Management { get; }
    internal IEnumerable<(Guid DeviceId, byte[] WindowsHash, byte[] ControllerHash)> LegacyDevices =>
        devices.Select(entry => (entry.Key, entry.Value.WindowsHash, entry.Value.ControllerHash));

    public static RelaySettings Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("FY1SWITCH_RELAY_CONFIG must be an absolute file path.");
        Configuration config;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException();
            config = JsonSerializer.Deserialize<Configuration>(stream, RelayJson.Options) ?? throw new JsonException();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("Cannot load relay configuration; check file access and JSON schema.");
        }
        if ((config.Devices is not { Count: > 0 } && config.Management is null) || !double.IsFinite(config.RequestTimeoutSeconds) ||
            config.RequestTimeoutSeconds < 0.05 || config.RequestTimeoutSeconds > 100 ||
            config.RequestsPerMinute is < 1 or > 120)
            throw new InvalidOperationException("Invalid relay devices or limits.");
        var devices = new Dictionary<Guid, DeviceCredentials>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in config.Devices ?? [])
        {
            if (entry is null || entry.DeviceId == Guid.Empty || devices.ContainsKey(entry.DeviceId))
                throw new InvalidOperationException("Invalid or duplicate configured device.");
            var windowsHash = HashConfiguredToken(entry.WindowsToken);
            var controllerHash = HashConfiguredToken(entry.ControllerToken);
            if (!seenHashes.Add(Convert.ToHexString(windowsHash)) || !seenHashes.Add(Convert.ToHexString(controllerHash)))
                throw new InvalidOperationException("Relay credentials must be unique across devices and roles.");
            devices.Add(entry.DeviceId, new DeviceCredentials(windowsHash, controllerHash));
        }
        config.Management?.Validate();
        return new RelaySettings(devices, config.RequestTimeoutSeconds, config.RequestsPerMinute, config.Management);
    }

    public bool Authenticate(Guid deviceId, string token, bool windows)
    {
        var known = devices.TryGetValue(deviceId, out var device);
        var expected = known ? (windows ? device!.WindowsHash : device!.ControllerHash) : missingHash;
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(supplied, expected) & known;
    }

    private static byte[] HashConfiguredToken(string? token)
    {
        if (token is null || token.Length > 172)
            throw new InvalidOperationException("Relay tokens must encode 32-128 random bytes as canonical Base64.");
        byte[] decoded;
        try { decoded = Convert.FromBase64String(token); }
        catch (FormatException) { throw new InvalidOperationException("Relay tokens must be canonical Base64."); }
        try
        {
            if (decoded.Length is < 32 or > 128 || Convert.ToBase64String(decoded) != token)
                throw new InvalidOperationException("Relay tokens must encode 32-128 random bytes as canonical Base64.");
            return SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    private sealed record DeviceCredentials(byte[] WindowsHash, byte[] ControllerHash);
    private sealed record DeviceConfiguration(Guid DeviceId, string WindowsToken, string ControllerToken);
    private sealed record Configuration(List<DeviceConfiguration?>? Devices = null, double RequestTimeoutSeconds = 30, int RequestsPerMinute = 60, RelayManagementOptions? Management = null);
}

public sealed record RelayManagementOptions(string PublicUrl, string DataDirectory, string? AdminPasswordFile = null)
{
    public void Validate()
    {
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            PublicUrl != uri.GetLeftPart(UriPartial.Authority) || uri.HostNameType != UriHostNameType.Dns ||
            !Path.IsPathFullyQualified(DataDirectory) ||
            (AdminPasswordFile is not null && !Path.IsPathFullyQualified(AdminPasswordFile)))
            throw new InvalidOperationException("Management requires a canonical HTTPS origin with a DNS host and absolute private data/password paths.");
    }
}

public static class RelayJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };
}
