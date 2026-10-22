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
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var client = new PairedClient(Guid.NewGuid(), request.ClientName.Trim(), Convert.ToHexString(Hash(token)), _clock());
            var updated = _clients.Append(client).ToList();
            AtomicFile.Write(_clientsPath, Encoding.UTF8.GetBytes(WireJson.Serialize(updated)));
            _clients.Add(client); _pairHash = null;
            return new(deviceId, client.ClientId, token);
        }
    }
    public bool Authenticate(string? token)
    {
        if (token is null || token.Length != 64) return false;
        var hash = Hash(token);
        lock (_gate) return _clients.Any(client => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(client.TokenHash), hash));
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
