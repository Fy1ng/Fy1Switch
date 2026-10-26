using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fy1Switch.Contracts;
using Fy1Switch.Relay;
using Fy1Switch.Remote;
using Fy1Switch.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.EndToEnd.Tests;

[TestClass]
public sealed class ManagedRelayRoundTripTests
{
    [TestMethod]
    public async Task ManagedRegistrationFirstPairAutomaticAuthorizationAndRevocationUseRealHttpsAndWss()
    {
        using var directory = new OwnedDirectory();
        var worker = new SimulatedTransport();
        await using var controller = new DesktopController(worker, new FakeStartup(), Path.Combine(directory.Path, "desktop"), true);
        var discovery = await controller.DiscoverAsync();
        var bound = await controller.BindAsync(discovery.Candidates[0].TargetKey, discovery.TopologyFingerprint, "managed fixture");
        using var relayIdentity = new DeviceSecurity(Path.Combine(directory.Path, "relay-tls"), new UserSecretProtector());
        var passwordPath = Path.Combine(directory.Path, "bootstrap.txt");
        await File.WriteAllTextAsync(passwordPath, "Fixture-Only-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "!a7");
        var dataPath = Path.Combine(directory.Path, "relay-data");
        var configPath = Path.Combine(directory.Path, "relay.json");
        await File.WriteAllTextAsync(configPath, WireJson.Serialize(new
        {
            management = new { publicUrl = "https://relay.example.test", dataDirectory = dataPath, adminPasswordFile = passwordPath },
            requestTimeoutSeconds = 5, requestsPerMinute = 120
        }));
        await using var relay = RelayApplication.Build(["--FY1SWITCH_RELAY_CONFIG", configPath, "--urls", "http://127.0.0.1:0", "--environment", "Production"], builder =>
        {
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(relayIdentity.Certificate)));
        });
        await relay.StartAsync();
        var relayOrigin = new Uri(relay.Urls.Single());
        Assert.IsTrue(relayOrigin.IsLoopback); Assert.AreEqual("https", relayOrigin.Scheme);
        Assert.IsTrue(File.Exists(Path.Combine(dataPath, "identity.db")));
        Assert.IsTrue(File.Exists(Path.Combine(dataPath, "registry.db")));
        using var relayHttp = Client(relayOrigin, relayIdentity.CertificatePin);
        using var api = new RelayApiClient(relayHttp);
        var windowsDirectory = Path.Combine(directory.Path, "windows");
        await using var manager = new RemoteManager(controller, bound.DeviceId, windowsDirectory, new UserSecretProtector(), api)
        {
            RelayClientFactory = (gateway, endpoint, token) => new RelayConnection(gateway, endpoint, token)
            {
                SocketFactory = () =>
                {
                    var socket = new ClientWebSocket();
                    socket.Options.Proxy = null;
                    socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) => Matches(certificate, errors, relayIdentity.CertificatePin);
                    return socket;
                }
            }
        };
        var registry = relay.Services.GetRequiredService<RelayRegistry>();
        var registration = registry.GenerateRegistrationCode();
        var port = LoopbackPort();
        var windowsOrigin = new Uri($"https://127.0.0.1:{port}");
        await manager.RegisterRelayAsync(new RemoteSettings(true, "127.0.0.1", port, windowsOrigin.GetLeftPart(UriPartial.Authority), relayOrigin.GetLeftPart(UriPartial.Authority), RelayEnabled: true), registration.Code);
        Assert.IsNotNull(manager.Settings.ProtectedRelayToken);
        var saved = new RemoteSettingsStore(windowsDirectory, new UserSecretProtector());
        var windowsToken = saved.RelayToken(saved.Load());
        Assert.IsNotNull(windowsToken);
        Assert.IsTrue(registry.Authenticate(bound.DeviceId, windowsToken, true));
        Assert.IsFalse(File.ReadAllText(Path.Combine(windowsDirectory, "remote.json")).Contains(windowsToken, StringComparison.Ordinal));
        for (var attempt = 0; attempt < 100 && manager.RelayStatus != "connected"; attempt++) await Task.Delay(50);
        Assert.AreEqual("connected", manager.RelayStatus);

        var code = await manager.BeginRelayPairingAsync();
        using var firstPair = await Post(relayHttp, "/v1/relay/pair", new RelayJoinRequest(code.Code, "first remote phone"));
        Assert.AreEqual(HttpStatusCode.OK, firstPair.StatusCode);
        var phone = WireJson.Parse<RelayPairingReply>(await firstPair.Content.ReadAsStringAsync());
        Assert.AreEqual(bound.DeviceId, phone.DeviceId);
        Assert.AreEqual(Environment.MachineName, phone.DeviceName);
        Assert.AreNotEqual(Guid.Empty, phone.ClientId);
        Assert.AreEqual(manager.Security.CertificatePin, phone.CertificatePin);
        Assert.AreEqual(windowsOrigin.GetLeftPart(UriPartial.Authority), phone.Endpoint);
        Assert.IsNotNull(phone.DeviceToken); Assert.IsTrue(manager.Security.Authenticate(phone.DeviceToken));
        Assert.AreEqual(1, manager.Security.Clients.Count);
        Assert.IsFalse(manager.Security.RelayPairingActive);
        using var replay = await Post(relayHttp, "/v1/relay/pair", new RelayJoinRequest(code.Code, "replayed phone"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);

        var operationId = Guid.NewGuid();
        RemoteCommand Command(string command, PowerIntent? intent = null, Guid? operation = null) => new(Guid.NewGuid(), bound.DeviceId, command,
            DateTimeOffset.UtcNow.AddSeconds(30), bound.Binding!.TargetId, intent, operation);
        using var controlled = await Post(relayHttp, $"/v1/devices/{bound.DeviceId}/control", Command("set", PowerIntent.Off, operationId), phone.ControllerToken, phone.DeviceToken);
        Assert.AreEqual(HttpStatusCode.OK, controlled.StatusCode);
        var result = WireJson.Parse<RemoteReply>(await controlled.Content.ReadAsStringAsync());
        Assert.AreEqual(OperationOutcome.Confirmed, result.Operation!.Outcome);
        Assert.AreEqual(operationId, result.Operation.OperationId);
        Assert.AreEqual(DisplayPower.Off, result.Operation.Observation!.State);
        Assert.AreEqual(1, worker.Writes);

        using var authorized = await Post(relayHttp, $"/v1/devices/{bound.DeviceId}/authorize", new RelayAuthorizeRequest(phone.ClientId), deviceToken: phone.DeviceToken);
        Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode);
        var same = WireJson.Parse<RelayPairingReply>(await authorized.Content.ReadAsStringAsync());
        Assert.AreEqual(phone.ClientId, same.ClientId); Assert.AreEqual(phone.ControllerToken, same.ControllerToken);
        Assert.IsNull(same.DeviceToken); Assert.IsNull(same.CertificatePin);
        Assert.AreEqual(1, manager.Security.Clients.Count);
        using var windowsHttp = Client(windowsOrigin, manager.Security.CertificatePin);
        using var lan = await Post(windowsHttp, "/v1/relay", new RelayAuthorizeRequest(phone.ClientId), bearer: phone.DeviceToken);
        Assert.AreEqual(HttpStatusCode.OK, lan.StatusCode);
        var automatic = WireJson.Parse<RelayConfigurationReply>(await lan.Content.ReadAsStringAsync());
        Assert.AreEqual(phone.ClientId, automatic.ClientId); Assert.AreEqual(phone.ControllerToken, automatic.ControllerToken);
        Assert.AreEqual(relayOrigin.GetLeftPart(UriPartial.Authority), automatic.Endpoint);

        manager.Security.Revoke(phone.ClientId);
        using var revokedAuthorize = await Post(relayHttp, $"/v1/devices/{bound.DeviceId}/authorize", new RelayAuthorizeRequest(phone.ClientId), deviceToken: phone.DeviceToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, revokedAuthorize.StatusCode);
        using var revokedControl = await Post(relayHttp, $"/v1/devices/{bound.DeviceId}/control", Command("status"), phone.ControllerToken, phone.DeviceToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, revokedControl.StatusCode);
        Assert.AreEqual(1, worker.Writes);

        // Fill the Relay's stale grant slots. Windows remains the authority for current phone membership.
        for (var index = 1; index < 32; index++) Assert.IsNotNull(registry.ControllerFor(bound.DeviceId, Guid.NewGuid()));
        Assert.IsNull(registry.ControllerFor(bound.DeviceId, Guid.NewGuid()));
        var replacementCode = await manager.BeginRelayPairingAsync();
        Assert.IsFalse(registry.Authenticate(bound.DeviceId, phone.ControllerToken, false));
        using var replacement = await Post(relayHttp, "/v1/relay/pair", new RelayJoinRequest(replacementCode.Code, "replacement phone"));
        Assert.AreEqual(HttpStatusCode.OK, replacement.StatusCode);
        var newPhone = WireJson.Parse<RelayPairingReply>(await replacement.Content.ReadAsStringAsync());
        Assert.AreNotEqual(phone.ClientId, newPhone.ClientId);
        Assert.AreEqual(1, manager.Security.Clients.Count);
        Assert.IsTrue(registry.Authenticate(bound.DeviceId, newPhone.ControllerToken, false));
        await manager.CancelRelayPairingAsync();
    }

    private static HttpClient Client(Uri origin, string pin)
    {
        Assert.IsTrue(origin.IsLoopback); Assert.AreEqual("https", origin.Scheme);
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            request.RequestUri is { IsLoopback: true, Scheme: "https" } uri && uri.Authority == origin.Authority && Matches(certificate, errors, pin);
        return new(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(15) };
    }
    private static bool Matches(X509Certificate? certificate, SslPolicyErrors errors, string pin)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        using var cert = new X509Certificate2(certificate);
        if (DateTime.UtcNow < cert.NotBefore.ToUniversalTime() || DateTime.UtcNow > cert.NotAfter.ToUniversalTime()) return false;
        using var key = cert.GetRSAPublicKey();
        return key is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(key.ExportSubjectPublicKeyInfo()), Convert.FromBase64String(pin[7..]));
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object payload, string? bearer = null, string? deviceToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(WireJson.Serialize(payload), Encoding.UTF8, "application/json") };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (deviceToken is not null) request.Headers.Add("X-Device-Token", deviceToken);
        return await client.SendAsync(request);
    }
    private static int LoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private sealed class FakeStartup : IStartupRegistration { public bool Enabled { get; private set; } public void SetEnabled(bool enabled) => Enabled = enabled; }
    private sealed class SimulatedTransport : IWorkerTransport
    {
        private static readonly string Key = new('c', 64), Fingerprint = new('d', 64);
        private DisplayPower power = DisplayPower.On;
        public int Writes { get; private set; }
        public bool IsRunning => true;
        public Task<WorkerReply> SendAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Command == "discover") return Task.FromResult(new WorkerReply(request.RequestId, true, Discovery: new(Fingerprint, [new(Key, "managed fixture", "simulation")], DateTimeOffset.UtcNow)));
            if (request.Command == "set") { Writes++; power = request.Intent == PowerIntent.On ? DisplayPower.On : DisplayPower.Off; }
            return Task.FromResult(new WorkerReply(request.RequestId, true, Observation: new(power, power == DisplayPower.On ? 1 : 2, DateTimeOffset.UtcNow, "simulation"), WriteAccepted: request.Command == "set" ? true : null));
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OwnedDirectory : IDisposable
    {
        private readonly string name = "Fy1Switch-Managed-E2E-" + Guid.NewGuid().ToString("N");
        public string Path { get; }
        public OwnedDirectory() { Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name)); Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name));
            if (Path != expected || (File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories).Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Refusing deletion outside the owned fixture directory.");
            Directory.Delete(Path, recursive: true);
        }
    }
}
