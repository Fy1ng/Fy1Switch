using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Remote;
using RemoteDisplaySwitch.Runtime;

namespace RemoteDisplaySwitch.EndToEnd.Tests;

[TestClass]
public class RemoteRoundTripTests
{
    [TestMethod]
    public async Task HttpsToWssRoundTripPreservesOperationIdAndRevocation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemoteDisplaySwitch-E2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var worker = new SimulatedTransport();
            await using var controller = new DesktopController(worker, new FakeStartup(), directory, true);
            var discovery = await controller.DiscoverAsync();
            var bound = await controller.BindAsync(discovery.Candidates[0].TargetKey, discovery.TopologyFingerprint, "fixture");
            using var security = new DeviceSecurity(Path.Combine(directory, "identity"), new UserSecretProtector());
            var gateway = new RemoteGateway(controller, security, bound.DeviceId);
            var windowsToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var controllerToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var config = Path.Combine(directory, "relay.json");
            await File.WriteAllTextAsync(config, WireJson.Serialize(new { devices = new[] { new { deviceId = bound.DeviceId, windowsToken, controllerToken } } }));
            await using var relay = RelayApplication.Build(["--RDS_RELAY_CONFIG", config, "--urls", "http://127.0.0.1:0"], builder =>
            {
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(security.Certificate)));
            });
            await relay.StartAsync();
            var endpoint = relay.Urls.Single();
            var expectedPin = security.CertificatePin;
            bool Match(X509Certificate? certificate)
            {
                if (certificate is null) return false;
                using var cert = new X509Certificate2(certificate);
                using var key = cert.GetRSAPublicKey();
                return key is not null && DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
                    "sha256/" + Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) == expectedPin;
            }
            await using var bridge = new RemoteDisplaySwitch.Remote.RelayConnection(gateway, endpoint, windowsToken)
            {
                SocketFactory = () =>
                {
                    var socket = new ClientWebSocket();
                    socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => Match(cert);
                    return socket;
                }
            };
            bridge.Start();
            for (var attempt = 0; attempt < 100 && bridge.Status != "connected"; attempt++) await Task.Delay(50);
            Assert.AreEqual("connected", bridge.Status);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => Match(cert) }) { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpResponseMessage> Post(string route, object payload, string? token = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{bound.DeviceId}/{route}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", controllerToken);
                if (token is not null) request.Headers.Add("X-Device-Token", token);
                request.Content = new StringContent(WireJson.Serialize(payload), Encoding.UTF8, "application/json");
                return await client.SendAsync(request);
            }
            var bundle = security.BeginPairing(bound.DeviceId, endpoint);
            using var paired = await Post("pair", new PairRequest(bundle.Code, "fixture phone"));
            Assert.AreEqual(HttpStatusCode.OK, paired.StatusCode);
            var phone = WireJson.Parse<PairReply>(await paired.Content.ReadAsStringAsync());
            var operationId = Guid.NewGuid();
            RemoteCommand Command(string command, PowerIntent? intent = null, Guid? operation = null) => new(Guid.NewGuid(), bound.DeviceId, command, DateTimeOffset.UtcNow.AddSeconds(30), bound.Binding!.TargetId, intent, operation);
            var direct = await gateway.ControlAsync(phone.Token, Command("set", PowerIntent.Off, operationId));
            Assert.AreEqual(OperationOutcome.Confirmed, direct.Reply.Operation!.Outcome);
            using var repeated = await Post("control", Command("set", PowerIntent.Off, operationId), phone.Token);
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
            Assert.AreEqual(1, worker.Writes);
            var relayJson = await repeated.Content.ReadAsStringAsync();
            using var rawRelay = System.Text.Json.JsonDocument.Parse(relayJson);
            Assert.AreEqual("Confirmed", rawRelay.RootElement.GetProperty("operation").GetProperty("outcome").GetString());
            Assert.AreEqual("Off", rawRelay.RootElement.GetProperty("operation").GetProperty("observation").GetProperty("state").GetString());
            var result = WireJson.Parse<RemoteReply>(relayJson);
            Assert.AreEqual(operationId, result.Operation!.OperationId);
            Assert.AreEqual(direct.Reply.Operation.UpdatedAt, result.Operation.UpdatedAt);
            using var on = await Post("control", Command("set", PowerIntent.On, Guid.NewGuid()), phone.Token);
            Assert.AreEqual(DisplayPower.On, WireJson.Parse<RemoteReply>(await on.Content.ReadAsStringAsync()).Operation!.Observation!.State);
            security.Revoke(phone.ClientId);
            using var revoked = await Post("control", Command("status"), phone.Token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.AreEqual(2, worker.Writes);
            await relay.StopAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
    private sealed class FakeStartup : IStartupRegistration { public bool Enabled { get; private set; } public void SetEnabled(bool enabled) => Enabled = enabled; }
    private sealed class SimulatedTransport : IWorkerTransport
    {
        private static readonly string Key = new('a', 64);
        private static readonly string Fingerprint = new('b', 64);
        private DisplayPower _power = DisplayPower.On;
        public int Writes { get; private set; }
        public bool IsRunning => true;
        public Task<WorkerReply> SendAsync(WorkerRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Command == "discover") return Task.FromResult(new WorkerReply(request.RequestId, true, Discovery: new(Fingerprint, [new(Key, "fixture", "simulation")], DateTimeOffset.UtcNow)));
            if (request.Command == "set") { Writes++; _power = request.Intent == PowerIntent.On ? DisplayPower.On : DisplayPower.Off; }
            return Task.FromResult(new WorkerReply(request.RequestId, true, Observation: new(_power, _power == DisplayPower.On ? 1 : 2, DateTimeOffset.UtcNow, "simulation"), WriteAccepted: request.Command == "set" ? true : null));
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
