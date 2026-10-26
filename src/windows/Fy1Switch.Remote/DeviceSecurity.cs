using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fy1Switch.Contracts;

namespace Fy1Switch.Remote;

public sealed record PairedClient(Guid ClientId, string Name, string TokenHash, DateTimeOffset PairedAt);
public sealed class DeviceSecurity : IDisposable
{
    private readonly object _gate = new();
    private readonly string _clientsPath;
    private readonly List<PairedClient> _clients;
    private byte[]? _pairHash;
    private DateTimeOffset _pairExpiry;
    private byte[]? _relayPairHash;
    private DateTimeOffset _relayPairExpiry;
    private int _relayPairAttempts;
    private readonly Queue<DateTimeOffset> _relayPairAdmissions = new();
    private readonly Func<DateTimeOffset> _clock;
    public X509Certificate2 Certificate { get; }
    public string CertificatePin { get; }
    public DeviceSecurity(string directory, ISecretProtector protector, Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(directory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _clientsPath = Path.Combine(directory, "clients.json");
        _clients = File.Exists(_clientsPath) ? WireJson.Parse<List<PairedClient>>(File.ReadAllText(_clientsPath)) : [];
        var certificatePath = Path.Combine(directory, "identity.pfx.protected");
        if (File.Exists(certificatePath)) Certificate = X509CertificateLoader.LoadPkcs12(protector.Unprotect(File.ReadAllBytes(certificatePath)), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        else
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Fy1Switch", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(System.Net.IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(_clock().AddMinutes(-5), _clock().AddYears(3));
            var payload = generated.Export(X509ContentType.Pkcs12);
            AtomicFile.Write(certificatePath, protector.Protect(payload));
            Certificate = X509CertificateLoader.LoadPkcs12(payload, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            CryptographicOperations.ZeroMemory(payload);
        }
        using var publicKey = Certificate.GetRSAPublicKey() ?? throw new CryptographicException("unsupported_certificate");
        CertificatePin = "sha256/" + Convert.ToBase64String(SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo()));
    }
    public PairBundle BeginPairing(Guid deviceId, string endpoint)
    {
        RemoteSettings.RequireUrl(endpoint, "https");
        lock (_gate)
        {
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _pairHash = Hash(code); _pairExpiry = _clock().AddMinutes(5);
            return new(1, deviceId, endpoint.TrimEnd('/'), CertificatePin, code, _pairExpiry);
        }
    }
    public PairReply? CompletePairing(Guid deviceId, PairRequest request)
    {
        if (request.Code is null || request.Code.Length > 128 || string.IsNullOrWhiteSpace(request.ClientName) || request.ClientName.Length > 80) return null;
        lock (_gate)
        {
            if (_pairHash is null || _clock() >= _pairExpiry || !CryptographicOperations.FixedTimeEquals(_pairHash, Hash(request.Code)) || _clients.Count >= 32) return null;
            var reply = AddClient(deviceId, request.ClientName);
            _pairHash = null;
            return reply;
        }
    }
    public RelayCode BeginRelayPairing()
    {
        lock (_gate)
        {
            var now = _clock();
            while (_relayPairAdmissions.TryPeek(out var admitted) && admitted <= now.AddMinutes(-10)) _relayPairAdmissions.Dequeue();
            if (_relayPairAdmissions.Count >= 10) throw new InvalidOperationException("relay_pairing_limited");
            _relayPairAdmissions.Enqueue(now);
            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            _relayPairHash = Hash(code); _relayPairExpiry = now.AddMinutes(5); _relayPairAttempts = 0;
            return new(code, _relayPairExpiry);
        }
    }
    public PairReply? CompleteRelayPairing(Guid deviceId, PairRequest request)
    {
        lock (_gate)
        {
            if (_relayPairHash is null || _clock() >= _relayPairExpiry || _clients.Count >= 32) return null;
            if (++_relayPairAttempts > 5) { _relayPairHash = null; return null; }
            if (!IsSixDigitCode(request.Code) || string.IsNullOrWhiteSpace(request.ClientName) || request.ClientName.Length > 80
                || !CryptographicOperations.FixedTimeEquals(_relayPairHash, Hash(request.Code)))
            {
                if (_relayPairAttempts >= 5) _relayPairHash = null;
                return null;
            }
            var reply = AddClient(deviceId, request.ClientName);
            _relayPairHash = null;
            return reply;
        }
    }
    public bool RelayPairingActive { get { lock (_gate) return _relayPairHash is not null && _clock() < _relayPairExpiry; } }
    public void CancelRelayPairing() { lock (_gate) _relayPairHash = null; }
    public static bool IsSixDigitCode(string? code) => code is { Length: 6 } && code.All(character => character is >= '0' and <= '9');
    private PairReply AddClient(Guid deviceId, string name)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var client = new PairedClient(Guid.NewGuid(), name.Trim(), Convert.ToHexString(Hash(token)), _clock());
        var updated = _clients.Append(client).ToList();
        AtomicFile.Write(_clientsPath, Encoding.UTF8.GetBytes(WireJson.Serialize(updated)));
        _clients.Add(client);
        return new(deviceId, client.ClientId, token, Environment.MachineName);
    }
    public bool Authenticate(string? token) => AuthenticateClient(token) is not null;
    public PairedClient? AuthenticateClient(string? token)
    {
        if (token is null || token.Length != 64) return null;
        var hash = Hash(token);
        lock (_gate) return _clients.FirstOrDefault(client => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(client.TokenHash), hash));
    }
    public IReadOnlyList<PairedClient> Clients { get { lock (_gate) return _clients.ToArray(); } }
    public void Revoke(Guid clientId)
    {
        lock (_gate)
        {
            var updated = _clients.Where(client => client.ClientId != clientId).ToList();
            AtomicFile.Write(_clientsPath, Encoding.UTF8.GetBytes(WireJson.Serialize(updated)));
            _clients.Clear(); _clients.AddRange(updated);
        }
    }
    public void CancelPairing() { lock (_gate) _pairHash = null; }
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    public void Dispose() => Certificate.Dispose();
}
