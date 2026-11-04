using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fy1Switch.WolGateway;

public sealed record RegistrationRequest(int Version, string RegistrationCode, string ClientName)
{
    public override string ToString() => "RegistrationRequest(redacted)";
}
public sealed record DeviceDescription(Guid DeviceId, string Name);
public sealed record RegistrationReply(int Version, Guid ClientId, string AccessToken, IReadOnlyList<DeviceDescription> Devices)
{
    public override string ToString() => "RegistrationReply(redacted)";
}
public sealed record DeviceList(int Version, Guid ClientId, IReadOnlyList<DeviceDescription> Devices);
public sealed record WakeRequest(int Version, Guid DeviceId, Guid RequestId, Guid OperationId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public sealed record CheckRequest(int Version, Guid DeviceId, Guid RequestId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public sealed record WakeOperation(int Version, Guid DeviceId, Guid OperationId, string Status, string? ErrorCode, DateTimeOffset UpdatedAt);
public sealed record GatewayError(int Version, string ErrorCode);
public sealed record RegisteredClient(Guid ClientId, string Name, DateTimeOffset CreatedAt, bool Revoked, IReadOnlyList<Guid> DeviceIds);
public sealed record RegistrationCode(string Code, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "RegistrationCode(redacted)";
}
public sealed class GatewayException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public static class WolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 12,
    };
}

public static class WolValidation
{
    public static string RandomSecret(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    public static bool ValidToken(string? token) => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
        Convert.ToBase64String(Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=")).TrimEnd('=').Replace('+', '-').Replace('/', '_') == token;
    public static bool ValidName(string? value) => value is { Length: > 0 and <= 80 } && value == value.Trim() && !value.Any(char.IsControl);
    public static bool ValidInterface(string? value) => value is { Length: > 0 and <= 15 } && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    public static bool ValidMac(string? value)
    {
        if (value is not { Length: 17 }) return false;
        var pieces = value.Split(':');
        if (pieces.Length != 6 || pieces.Any(x => x.Length != 2 || x.Any(c => !Uri.IsHexDigit(c)))) return false;
        var bytes = pieces.Select(x => Convert.ToByte(x, 16)).ToArray();
        return (bytes[0] & 1) == 0 && bytes.Any(x => x != 0);
    }
    public static void Envelope(int version, Guid routeId, Guid deviceId, Guid requestId, DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (version != 1 || routeId == Guid.Empty || deviceId != routeId || requestId == Guid.Empty || issuedAt == default ||
            expiresAt <= issuedAt || expiresAt - issuedAt > TimeSpan.FromSeconds(120) || issuedAt > now.AddSeconds(30))
            throw new GatewayException(400, "invalid_request");
        if (expiresAt <= now) throw new GatewayException(400, "request_expired");
    }
}
