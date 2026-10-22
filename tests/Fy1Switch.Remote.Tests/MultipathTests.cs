using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Remote;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class MultipathTests
{
    private static RemoteSettings Settings(int port, string secondary = "127.0.0.2") =>
        new(true, "127.0.0.1", port, $"https://127.0.0.1:{port}",
            TailscaleEndpoint: $"https://{secondary}:{port}", TailscaleListenAddress: secondary);

    private static HttpClient Client(string endpoint, DeviceSecurity security)
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
            request.RequestUri?.GetLeftPart(UriPartial.Authority) == endpoint && certificate is not null
            && CryptographicOperations.FixedTimeEquals(certificate.RawData, security.Certificate.RawData);
        return new(handler) { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(10) };
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(HttpClient client, string path, T body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(WireJson.Serialize(body), Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [TestMethod]
    public async Task BothListenersShareIdentityAuthorizationAndUpdatedStatus()
    {
        using var fixture = new RemoteFixture();
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector);
        var settings = Settings(HttpsFixture.GetLoopbackPort());
        await manager.ApplyAsync(settings);
        Assert.IsTrue(manager.Running);
        Assert.AreEqual("listening", manager.TailscaleStatus);
        var bundle = manager.BeginPairing();
        Assert.AreEqual(settings.PublicEndpoint, bundle.Endpoint);
        Assert.AreEqual(settings.TailscaleEndpoint, bundle.TailscaleEndpoint);
        Assert.AreEqual(manager.Security.CertificatePin, bundle.CertificatePin);
        using var lan = Client(bundle.Endpoint, manager.Security);
        using var tailscale = Client(bundle.TailscaleEndpoint!, manager.Security);
        using var paired = await SendAsync(lan, "/v1/pair", new PairRequest(bundle.Code, "multipath fixture"));
        Assert.AreEqual(HttpStatusCode.OK, paired.StatusCode);
        var credentials = WireJson.Parse<PairReply>(await paired.Content.ReadAsStringAsync());
        var command = new RemoteCommand(Guid.NewGuid(), fixture.DeviceId, "status", DateTimeOffset.UtcNow.AddSeconds(30));
        foreach (var client in new[] { lan, tailscale })
        {
            using var unauthorized = await SendAsync(client, "/v1/control", command);
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var response = await SendAsync(client, "/v1/control", command, credentials.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var reply = WireJson.Parse<RemoteReply>(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(fixture.DeviceId, reply.Status!.DeviceId);
            Assert.AreEqual(settings.TailscaleEndpoint, reply.Status.TailscaleEndpoint);
        }
        var moved = Settings(settings.Port, "127.0.0.3");
        await manager.ApplyAsync(moved);
        using var movedClient = Client(moved.TailscaleEndpoint!, manager.Security);
        foreach (var client in new[] { lan, movedClient })
        {
            using var response = await SendAsync(client, "/v1/control", command, credentials.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(moved.TailscaleEndpoint, WireJson.Parse<RemoteReply>(await response.Content.ReadAsStringAsync()).Status!.TailscaleEndpoint);
        }
        manager.Security.Revoke(credentials.ClientId);
        foreach (var client in new[] { lan, movedClient })
        {
            using var response = await SendAsync(client, "/v1/control", command, credentials.Token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var replacement = manager.BeginPairing();
        using var repaired = await SendAsync(movedClient, "/v1/pair", new PairRequest(replacement.Code, "replacement fixture"));
        Assert.AreEqual(HttpStatusCode.OK, repaired.StatusCode);
        credentials = WireJson.Parse<PairReply>(await repaired.Content.ReadAsStringAsync());
        await manager.ApplyAsync(settings with { TailscaleEndpoint = null, TailscaleListenAddress = null });
        Assert.AreEqual("disabled", manager.TailscaleStatus);
        Assert.AreEqual(bundle.CertificatePin, manager.Security.CertificatePin);
        Assert.IsNull(manager.BeginPairing().TailscaleEndpoint);
        using var updated = await SendAsync(lan, "/v1/control", command, credentials.Token);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        Assert.IsNull(WireJson.Parse<RemoteReply>(await updated.Content.ReadAsStringAsync()).Status!.TailscaleEndpoint);
        using var released = new TcpListener(IPAddress.Parse("127.0.0.3"), settings.Port);
        released.Start();
    }

    [TestMethod]
    public async Task DisabledOptionalPathsKeepConfigurationWithoutOpeningListenersOrConnectingRelay()
    {
        using var fixture = new RemoteFixture();
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector);
        var settings = Settings(HttpsFixture.GetLoopbackPort()) with
        {
            TailscaleEnabled = false, RelayEnabled = false, RelayUrl = "https://relay.invalid"
        };
        await manager.ApplyAsync(settings);
        Assert.IsTrue(manager.Running);
        Assert.AreEqual("disabled", manager.TailscaleStatus);
        Assert.AreEqual("disabled", manager.RelayStatus);
        Assert.IsNull(manager.BeginPairing().TailscaleEndpoint);
        using var released = new TcpListener(IPAddress.Parse("127.0.0.2"), settings.Port);
        released.Start();
        Assert.AreEqual(settings, new RemoteSettingsStore(fixture.Directory.Path, fixture.Protector).Load());
    }

    [TestMethod]
    public async Task DuplicateAddressUsesOneListenerAndDisableReleasesIt()
    {
        using var fixture = new RemoteFixture();
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector);
        var settings = Settings(HttpsFixture.GetLoopbackPort(), "127.0.0.1");
        await manager.ApplyAsync(settings, persist: false);
        Assert.AreEqual("listening", manager.TailscaleStatus);
        Assert.AreEqual(settings.PublicEndpoint, manager.BeginPairing().TailscaleEndpoint);
        await manager.ApplyAsync(settings with { Enabled = false }, persist: false);
        Assert.IsFalse(manager.Running);
        Assert.AreEqual("disabled", manager.TailscaleStatus);
        using var released = new TcpListener(IPAddress.Loopback, settings.Port);
        released.Start();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrOccupiedSecondaryDoesNotDisableLan(bool occupied)
    {
        using var fixture = new RemoteFixture();
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector);
        var settings = Settings(HttpsFixture.GetLoopbackPort(), occupied ? "127.0.0.2" : "192.0.2.254");
        using var blocker = new TcpListener(IPAddress.Parse("127.0.0.2"), settings.Port);
        if (occupied) blocker.Start();
        await manager.ApplyAsync(settings);
        Assert.IsTrue(manager.Running);
        Assert.AreEqual("unavailable", manager.TailscaleStatus);
        Assert.IsNull(manager.LastError);
        Assert.AreEqual(settings, new RemoteSettingsStore(fixture.Directory.Path, fixture.Protector).Load());
        var bundle = manager.BeginPairing();
        Assert.AreEqual(settings.PublicEndpoint, bundle.Endpoint);
        Assert.IsNull(bundle.TailscaleEndpoint);
        using var lan = Client(bundle.Endpoint, manager.Security);
        using var paired = await SendAsync(lan, "/v1/pair", new PairRequest(bundle.Code, "degraded fixture"));
        Assert.AreEqual(HttpStatusCode.OK, paired.StatusCode);
        var credentials = WireJson.Parse<PairReply>(await paired.Content.ReadAsStringAsync());
        var command = new RemoteCommand(Guid.NewGuid(), fixture.DeviceId, "status", DateTimeOffset.UtcNow.AddSeconds(30));
        using var response = await SendAsync(lan, "/v1/control", command, credentials.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNull(WireJson.Parse<RemoteReply>(await response.Content.ReadAsStringAsync()).Status!.TailscaleEndpoint);
        if (occupied)
        {
            blocker.Stop();
            await manager.ApplyAsync(settings);
            Assert.AreEqual("listening", manager.TailscaleStatus);
            Assert.AreEqual(bundle.CertificatePin, manager.BeginPairing().CertificatePin);
        }
    }

    [TestMethod]
    public void OldJsonRemainsReadableAndNewDtoFieldUsesCamelCase()
    {
        var legacy = WireJson.Parse<RemoteSettings>("""{"enabled":true,"listenAddress":"192.168.1.2","port":48152,"publicEndpoint":"https://192.168.1.2:48152"}""");
        legacy.Validate();
        Assert.IsNull(legacy.TailscaleEndpoint);
        Assert.IsNull(legacy.TailscaleListenAddress);
        Assert.AreEqual("192.168.1.2", legacy.ListenAddress);
        var bundle = WireJson.Parse<PairBundle>("""{"version":1,"deviceId":"00000000-0000-0000-0000-000000000001","endpoint":"https://192.168.1.2:48152","certificatePin":"test-pin","code":"123456","expiresAt":"2026-10-07T00:00:00Z"}""");
        Assert.IsNull(bundle.TailscaleEndpoint);
        var status = WireJson.Parse<RemoteStatus>("""{"deviceId":"00000000-0000-0000-0000-000000000001","available":false,"busy":false}""");
        Assert.IsNull(status.TailscaleEndpoint);
        const string endpoint = "https://100.70.1.2:48152";
        foreach (var json in new[] { WireJson.Serialize(bundle with { TailscaleEndpoint = endpoint }), WireJson.Serialize(status with { TailscaleEndpoint = endpoint }) })
        {
            using var document = JsonDocument.Parse(json);
            Assert.AreEqual(endpoint, document.RootElement.GetProperty("tailscaleEndpoint").GetString());
            Assert.IsFalse(document.RootElement.TryGetProperty("TailscaleEndpoint", out _));
        }
    }

    [TestMethod]
    [DataRow(null, "127.0.0.2")]
    [DataRow("https://127.0.0.2:48152", null)]
    [DataRow("http://127.0.0.2:48152", "127.0.0.2")]
    [DataRow("https://127.0.0.2:48153", "127.0.0.2")]
    [DataRow("https://127.0.0.3:48152", "127.0.0.2")]
    [DataRow("https://0.0.0.0:48152", "0.0.0.0")]
    [DataRow("https://127.0.0.2:48152/path", "127.0.0.2")]
    public void SecondaryMustBeAnExplicitMatchingHttpsListener(string? endpoint, string? address) =>
        Assert.ThrowsException<ArgumentException>(() => new RemoteSettings(TailscaleEndpoint: endpoint, TailscaleListenAddress: address).Validate());

    [TestMethod]
    public void ChoosingTailscaleDoesNotReplaceLanOrRelay()
    {
        var current = new RemoteSettings(true, "192.168.1.2", 48152, "https://192.168.1.2:48152", "https://relay.example", "fixture");
        var updated = new LocalNetworkChoice("fixture", "100.70.1.2", true, false).Apply(current);
        Assert.AreEqual(current, updated with { TailscaleEndpoint = null, TailscaleListenAddress = null });
        Assert.AreEqual("https://100.70.1.2:48152", updated.TailscaleEndpoint);
        updated.Validate();
        var changedLan = new LocalNetworkChoice("fixture", "192.168.1.3", false, true).Apply(updated);
        Assert.AreEqual(updated.TailscaleEndpoint, changedLan.TailscaleEndpoint);
        Assert.AreEqual(updated.TailscaleListenAddress, changedLan.TailscaleListenAddress);
    }
}
