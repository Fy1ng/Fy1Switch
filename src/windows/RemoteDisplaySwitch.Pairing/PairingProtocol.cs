using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Crypto.Agreement.Srp;

namespace RemoteDisplaySwitch.Pairing;

public sealed class PairingProtocolException : Exception
{
    public PairingProtocolException() : base("Pairing protocol rejected.") { }
}

public sealed record PairingFrame
{
    public required string Type { get; init; }
    public int Version { get; init; } = 1;
    public string? ClientSession { get; init; }
    public string? ServerSession { get; init; }
    public string? ClientRole { get; init; }
    public string? ServerRole { get; init; }
    public string? A { get; init; }
    public string? B { get; init; }
    public string? Salt { get; init; }
    public string? M1 { get; init; }
    public string? M2 { get; init; }
    public string? Purpose { get; init; }
    public string? Nonce { get; init; }
    public string? Ciphertext { get; init; }
    public string? Tag { get; init; }
}

public static class PairingProtocol
{
    public const int MaxFrameBytes = 16 * 1024;
    public const string WindowsBundle = "windows-bundle";
    public const string AndroidResult = "android-result";
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    static PairingProtocol() => Json.MakeReadOnly(populateMissingResolver: true);

    public static string NewSessionId() => Guid.NewGuid().ToString("D");
    public static string NewCode() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    public static void ValidateCode(string code)
    {
        if (code is null || code.Length != 6 || code.Any(character => character is < '0' or > '9')) throw new PairingProtocolException();
    }

    public static void ValidateSession(string? session)
    {
        if (!Guid.TryParseExact(session, "D", out var parsed) || parsed.ToString("D") != session) throw new PairingProtocolException();
    }

    public static string OppositeRole(string role) => role switch
    {
        "windows" => "android",
        "android" => "windows",
        _ => throw new PairingProtocolException()
    };

    public static byte[] Decode(string? encoded, int? length = null)
    {
        if (encoded is null || encoded.Length > MaxFrameBytes) throw new PairingProtocolException();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new PairingProtocolException(); }
        if ((length.HasValue && bytes.Length != length) || Convert.ToBase64String(bytes) != encoded) throw new PairingProtocolException();
        return bytes;
    }

    public static byte[] Padded(BigInteger number, int length)
    {
        var bytes = number.ToByteArrayUnsigned();
        if (number.SignValue < 0 || bytes.Length > length) throw new PairingProtocolException();
        var result = new byte[length];
        bytes.CopyTo(result, length - bytes.Length);
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }

    internal static string Encode(BigInteger number, int length) => Convert.ToBase64String(Padded(number, length));
    internal static BigInteger PublicValue(string? value)
    {
        var number = new BigInteger(1, Decode(value, 384));
        if (number.SignValue == 0 || number.CompareTo(Srp6StandardGroups.rfc5054_3072.N) >= 0) throw new PairingProtocolException();
        return number;
    }

    internal static void Evidence(BigInteger expected, string? supplied)
    {
        var expectedBytes = Padded(expected, 32);
        var received = Decode(supplied, 32);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, received)) throw new PairingProtocolException();
        }
        finally { CryptographicOperations.ZeroMemory(expectedBytes); CryptographicOperations.ZeroMemory(received); }
    }

    public static byte[] Serialize(PairingFrame frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        ValidateDocument(bytes);
        return bytes;
    }

    public static PairingFrame Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ValidateDocument(bytes);
            return JsonSerializer.Deserialize<PairingFrame>(bytes, Json) ?? throw new PairingProtocolException();
        }
        catch (JsonException) { throw new PairingProtocolException(); }
    }

    private static void ValidateDocument(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length + 1 > MaxFrameBytes) throw new PairingProtocolException();
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new PairingProtocolException();
        var fields = type.GetString() switch
        {
            "hello" => "type version clientSession serverSession clientRole a",
            "challenge" => "type version clientSession serverSession serverRole salt b",
            "proof" => "type version clientSession serverSession m1",
            "verified" => "type version clientSession serverSession m2",
            "encrypted" => "type version purpose nonce ciphertext tag",
            _ => throw new PairingProtocolException()
        };
        var remaining = fields.Split(' ').ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!remaining.Remove(property.Name)) throw new PairingProtocolException();
            if (property.Name == "version")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var version) || version != 1) throw new PairingProtocolException();
            }
            else if (property.Value.ValueKind != JsonValueKind.String) throw new PairingProtocolException();
        }
        if (remaining.Count != 0) throw new PairingProtocolException();
    }

    internal static void Check(PairingFrame frame, string type, string clientSession, string serverSession)
    {
        Serialize(frame);
        if (frame.Type != type || frame.Version != 1 || frame.ClientSession != clientSession || frame.ServerSession != serverSession) throw new PairingProtocolException();
    }
}

public sealed record PairingTranscript(string ClientSession, string ServerSession, string ClientRole,
    string ServerRole, string Salt, string A, string B, string M1, string M2)
{
    public byte[] ToUtf8()
    {
        PairingProtocol.ValidateSession(ClientSession);
        PairingProtocol.ValidateSession(ServerSession);
        if (PairingProtocol.OppositeRole(ClientRole) != ServerRole) throw new PairingProtocolException();
        PairingProtocol.Decode(Salt, 32);
        PairingProtocol.PublicValue(A);
        PairingProtocol.PublicValue(B);
        PairingProtocol.Decode(M1, 32);
        PairingProtocol.Decode(M2, 32);
        return Encoding.UTF8.GetBytes(string.Join('\n', "RDS-Pair-v1", ClientSession, ServerSession, ClientRole, ServerRole, Salt, A, B, M1, M2));
    }

    public byte[] Hash() => SHA256.HashData(ToUtf8());
}
