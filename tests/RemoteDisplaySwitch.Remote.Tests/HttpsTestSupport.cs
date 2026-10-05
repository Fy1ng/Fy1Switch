using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Remote;

namespace RemoteDisplaySwitch.Remote.Tests;

internal sealed class PinnedHttpClient : IDisposable
{
    private int validationCalls;
    private int acceptedCertificates;
    public int ValidationCalls => Volatile.Read(ref validationCalls);
    public int AcceptedCertificates => Volatile.Read(ref acceptedCertificates);
    public HttpClient Client { get; }

    public PinnedHttpClient(Uri endpoint, string pin)
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
        {
            Interlocked.Increment(ref validationCalls);
            if (certificate is null || request.RequestUri is not { Scheme: "https", IsLoopback: true } uri
                || uri.Authority != endpoint.Authority
                || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None
                || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime()
                || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime()) return false;
            using var publicKey = certificate.GetRSAPublicKey();
            if (publicKey is null || !pin.StartsWith("sha256/", StringComparison.Ordinal)) return false;
            var actual = SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo());
            var matches = CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(pin[7..]));
            if (matches) Interlocked.Increment(ref acceptedCertificates);
            return matches;
        };
        Client = new(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(15) };
    }

    public void Dispose() => Client.Dispose();
}

internal sealed class HttpsFixture : IAsyncDisposable
{
    public RemoteFixture Remote { get; } = new();
    public HttpsControlServer Server { get; }
    public RemoteSettings Settings { get; }
    public PinnedHttpClient Pinned { get; }
    public Uri Endpoint => Pinned.Client.BaseAddress!;

    private HttpsFixture()
    {
        var port = GetLoopbackPort();
        Settings = new(true, "127.0.0.1", port, $"https://127.0.0.1:{port}");
        Server = new(Remote.Gateway, Remote.Security);
        Pinned = new(new Uri(Settings.PublicEndpoint), Remote.Security.CertificatePin);
    }

    public static int GetLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task<HttpsFixture> StartAsync()
    {
        var fixture = new HttpsFixture();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await fixture.Server.StartAsync(fixture.Settings, timeout.Token);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public async Task<PairReply> PairAsync()
    {
        var bundle = Remote.Security.BeginPairing(Remote.DeviceId, Settings.PublicEndpoint);
        using var response = await SendAsync("/v1/pair", new PairRequest(bundle.Code, "HTTPS test client"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var reply = WireJson.Parse<PairReply>(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(Remote.DeviceId, reply.DeviceId);
        Assert.IsTrue(Remote.Security.Authenticate(reply.Token));
        return reply;
    }

    public Task<HttpResponseMessage> SendAsync<T>(string path, T body, string? token = null) => SendRawAsync(path, WireJson.Serialize(body), token);

    public async Task<HttpResponseMessage> SendRawAsync(string path, string body, string? token = null, bool chunked = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return await Pinned.Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Pinned.Dispose();
        try { await Server.DisposeAsync(); }
        finally { Remote.Dispose(); }
    }
}
