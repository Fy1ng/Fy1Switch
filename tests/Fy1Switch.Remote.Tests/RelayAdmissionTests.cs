using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fy1Switch.Contracts;
using Fy1Switch.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class RelayAdmissionTests
{
    [TestMethod]
    public void RelayCodeIsIndependentFromOriginalBootstrapAndIsSingleUse()
    {
        using var fixture = new RemoteFixture();
        var direct = fixture.Security.BeginPairing(fixture.DeviceId, "https://127.0.0.1:48152");
        var relay = fixture.Security.BeginRelayPairing();
        Assert.IsTrue(DeviceSecurity.IsSixDigitCode(relay.Code));
        Assert.AreEqual(fixture.Clock.Now.AddMinutes(5), relay.ExpiresAt);
        Assert.IsNull(fixture.Gateway.Pair(new(relay.Code, "wrong channel")));
        Assert.IsNull(fixture.Security.CompleteRelayPairing(fixture.DeviceId, new(direct.Code, "wrong channel")));
        var reply = fixture.Security.CompleteRelayPairing(fixture.DeviceId, new(relay.Code, "Relay phone"));
        Assert.IsNotNull(reply);
        Assert.IsTrue(fixture.Security.Authenticate(reply.Token));
        Assert.IsFalse(fixture.Security.RelayPairingActive);
        Assert.IsNull(fixture.Security.CompleteRelayPairing(fixture.DeviceId, new(relay.Code, "Replay")));
        Assert.IsNotNull(fixture.Gateway.Pair(new(direct.Code, "Direct phone")));
        Assert.AreEqual(2, fixture.Security.Clients.Count);
    }

    [DataTestMethod]
    [DataRow(299999, true)]
    [DataRow(300000, false)]
    [DataRow(300001, false)]
    public void RelayCodeExpiresAtFiveMinutes(int milliseconds, bool accepted)
    {
        using var fixture = new RemoteFixture();
        var code = fixture.Security.BeginRelayPairing();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Assert.AreEqual(accepted, fixture.Security.CompleteRelayPairing(fixture.DeviceId, new(code.Code, "phone")) is not null);
    }

    [TestMethod]
    public void RelayCodeFiveWrongAttemptsCancelAndRenewalDoesNotResetAdmissionBudget()
    {
        using var fixture = new RemoteFixture();
        var code = fixture.Security.BeginRelayPairing();
        var wrong = (code.Code[0] == '0' ? "1" : "0") + code.Code[1..];
        for (var attempt = 0; attempt < 5; attempt++) Assert.IsNull(fixture.Security.CompleteRelayPairing(fixture.DeviceId, new(wrong, "phone")));
        Assert.IsNull(fixture.Security.CompleteRelayPairing(fixture.DeviceId, new(code.Code, "phone")));
        for (var index = 1; index < 10; index++) { fixture.Security.BeginRelayPairing(); fixture.Security.CancelRelayPairing(); }
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Security.BeginRelayPairing());
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsTrue(DeviceSecurity.IsSixDigitCode(fixture.Security.BeginRelayPairing().Code));
    }

    [DataTestMethod]
    [DataRow("000001", true)]
    [DataRow("000000", true)]
    [DataRow("12345", false)]
    [DataRow("1234567", false)]
    [DataRow("12 456", false)]
    [DataRow("１２３４５６", false)]
    public void CodesRequireExactlySixAsciiDigits(string code, bool valid) => Assert.AreEqual(valid, DeviceSecurity.IsSixDigitCode(code));

    [TestMethod]
    public async Task ConcurrentRelayPairingIssuesOnePhoneAuthorization()
    {
        using var fixture = new RemoteFixture();
        var code = fixture.Security.BeginRelayPairing();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await start.Task;
            return fixture.Security.CompleteRelayPairing(fixture.DeviceId, new(code.Code, "phone"));
        }).ToArray();
        start.SetResult();
        Assert.AreEqual(1, (await Task.WhenAll(tasks)).Count(reply => reply is not null));
        Assert.AreEqual(1, fixture.Security.Clients.Count);
    }

    [TestMethod]
    public void AuthorizeRelayRequiresTokenOwnedByRequestedClientAndHonorsRevocation()
    {
        using var fixture = new RemoteFixture();
        var first = fixture.Pair(); var second = fixture.Pair();
        Assert.IsNull(fixture.Gateway.AuthorizeRelay(null, new(first.ClientId)));
        Assert.IsNull(fixture.Gateway.AuthorizeRelay(first.Token, new(second.ClientId)));
        var reply = fixture.Gateway.AuthorizeRelay(first.Token, new(first.ClientId));
        Assert.IsNotNull(reply);
        Assert.AreEqual(first.ClientId, reply.ClientId);
        Assert.IsNull(reply.DeviceToken); Assert.IsNull(reply.Endpoint); Assert.IsNull(reply.CertificatePin);
        fixture.Security.Revoke(first.ClientId);
        Assert.IsNull(fixture.Gateway.AuthorizeRelay(first.Token, new(first.ClientId)));
        Assert.IsNotNull(fixture.Gateway.AuthorizeRelay(second.Token, new(second.ClientId)));
    }

    [TestMethod]
    public void RelayFirstPairReturnsWindowsIdentityAndDoesNotPerformDisplayControl()
    {
        using var fixture = new RemoteFixture();
        fixture.Gateway.PublicEndpoint = "https://192.168.1.10:48152";
        fixture.Gateway.RelayEndpoint = "https://relay.test";
        var code = fixture.Security.BeginRelayPairing();
        var reply = fixture.Gateway.PairRelay(new(code.Code, "Remote phone"));
        Assert.IsNotNull(reply);
        Assert.AreEqual(fixture.DeviceId, reply.DeviceId);
        Assert.AreEqual(fixture.Security.CertificatePin, reply.CertificatePin);
        Assert.AreEqual(fixture.Gateway.PublicEndpoint, reply.Endpoint);
        Assert.IsTrue(fixture.Security.Authenticate(reply.DeviceToken));
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
        Assert.IsNull(fixture.Gateway.PairRelay(new(code.Code, "Replay")));
    }

    [TestMethod]
    public async Task LanConfigurationRechecksAuthorizationAfterAwaitingRelay()
    {
        using var fixture = new RemoteFixture();
        var phone = fixture.Pair();
        fixture.Gateway.RelayEndpoint = "https://relay.test";
        var issued = new TaskCompletionSource<RelayControllerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Gateway.IssueRelayController = (_, _) => issued.Task;
        var pending = fixture.Gateway.RelayConfigurationAsync(phone.Token, new(phone.ClientId));
        fixture.Security.Revoke(phone.ClientId);
        issued.SetResult(new(fixture.DeviceId, phone.ClientId, NewToken()));
        var result = await pending;
        Assert.AreEqual(401, result.StatusCode); Assert.IsNull(result.Reply);
    }

    [TestMethod]
    public async Task RealPinnedHttpsLanConfigurationNeverIssuesCredentialsToUnauthorizedPhone()
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var phone = await fixture.PairAsync();
        var controllerToken = NewToken(); var calls = 0;
        fixture.Remote.Gateway.RelayEndpoint = "https://relay.test";
        fixture.Remote.Gateway.IssueRelayController = (id, _) => { calls++; return Task.FromResult(new RelayControllerReply(fixture.Remote.DeviceId, id, controllerToken)); };
        using (var unauthenticated = await fixture.SendAsync("/v1/relay", new RelayAuthorizeRequest(phone.ClientId))) Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using (var wrongClient = await fixture.SendAsync("/v1/relay", new RelayAuthorizeRequest(Guid.NewGuid()), phone.Token)) Assert.AreEqual(HttpStatusCode.Unauthorized, wrongClient.StatusCode);
        Assert.AreEqual(0, calls);
        using (var valid = await fixture.SendAsync("/v1/relay", new RelayAuthorizeRequest(phone.ClientId), phone.Token))
        {
            Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
            Assert.IsTrue(valid.Headers.CacheControl!.NoStore);
            var reply = WireJson.Parse<RelayConfigurationReply>(await valid.Content.ReadAsStringAsync());
            Assert.AreEqual(fixture.Remote.DeviceId, reply.DeviceId); Assert.AreEqual(phone.ClientId, reply.ClientId);
            Assert.AreEqual("https://relay.test", reply.Endpoint); Assert.AreEqual(controllerToken, reply.ControllerToken);
        }
        fixture.Remote.Security.Revoke(phone.ClientId);
        using (var revoked = await fixture.SendAsync("/v1/relay", new RelayAuthorizeRequest(phone.ClientId), phone.Token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task RegistrationStoresProtectedTokenAndPreservesIdentityAcrossRestart()
    {
        using var fixture = new RemoteFixture();
        var token = NewToken();
        using var http = new HttpClient(new FakeHandler(async request =>
        {
            Assert.AreEqual("https://relay.test/v1/relay/register", request.RequestUri!.AbsoluteUri);
            Assert.IsNull(request.Headers.Authorization);
            var body = WireJson.Parse<RelayRegistrationRequest>(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("000123", body.Code); Assert.AreEqual(fixture.DeviceId, body.DeviceId);
            return Json(new RelayRegistrationReply(body.DeviceId, token));
        }));
        using var api = new RelayApiClient(http);
        await using (var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector, api))
        {
            await manager.RegisterRelayAsync("https://relay.test/", "000123");
            Assert.IsFalse(manager.Settings.Enabled);
            Assert.IsTrue(manager.Settings.UseRelay);
            Assert.AreEqual("https://relay.test", manager.Settings.RelayUrl);
            Assert.IsFalse(File.ReadAllText(Path.Combine(fixture.Directory.Path, "remote.json")).Contains(token, StringComparison.Ordinal));
            Assert.AreEqual(token, new RemoteSettingsStore(fixture.Directory.Path, fixture.Protector).RelayToken(manager.Settings));
        }
        await using var restarted = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector, api);
        Assert.AreEqual("https://relay.test", restarted.Settings.RelayUrl);
        Assert.AreEqual(fixture.Security.CertificatePin, restarted.Security.CertificatePin);
    }

    [TestMethod]
    public async Task RegistrationRejectsWrongDeviceResponseWithoutPersistingConfiguration()
    {
        using var fixture = new RemoteFixture();
        using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Json(new RelayRegistrationReply(Guid.NewGuid(), NewToken())))));
        using var api = new RelayApiClient(http);
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector, api);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => manager.RegisterRelayAsync("https://relay.test", "000123"));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory.Path, "remote.json")));
        Assert.IsNull(manager.Settings.ProtectedRelayToken);
    }

    [TestMethod]
    public async Task HidingWhileCodeRegistrationIsPendingInvalidatesCodeBeforeHttpCompletion()
    {
        using var fixture = new RemoteFixture();
        var submitted = new TaskCompletionSource<RelayCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = 0; var token = NewToken();
        using var http = new HttpClient(new FakeHandler(async request =>
        {
            Assert.AreEqual(token, request.Headers.Authorization!.Parameter);
            Assert.IsTrue(request.RequestUri!.IsLoopback);
            if (request.RequestUri.AbsolutePath.EndsWith("/controllers/synchronize", StringComparison.Ordinal)) return new(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Delete) { deleted++; return new(HttpStatusCode.NoContent); }
            submitted.SetResult(WireJson.Parse<RelayCode>(await request.Content!.ReadAsStringAsync()));
            await complete.Task;
            return new(HttpStatusCode.NoContent);
        }));
        using var api = new RelayApiClient(http);
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector, api);
        var port = HttpsFixture.GetLoopbackPort();
        await manager.ApplyAsync(new(true, "127.0.0.1", port, $"https://127.0.0.1:{port}", "https://127.0.0.1:1", manager.ProtectedRelayToken(token)));
        var pending = manager.BeginRelayPairingAsync();
        var code = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelling = manager.CancelRelayPairingAsync();
        Assert.IsFalse(manager.Security.RelayPairingActive);
        Assert.IsNull(manager.Security.CompleteRelayPairing(fixture.DeviceId, new(code.Code, "racing phone")));
        complete.SetResult();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pending);
        await cancelling;
        Assert.AreEqual(1, deleted);
    }

    [TestMethod]
    public async Task RelayCodeCollisionRetriesAndConfigurationChangeCancelsAcceptedCode()
    {
        using var fixture = new RemoteFixture();
        var attempts = 0; var deletes = 0; var token = NewToken();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            Assert.IsTrue(request.RequestUri!.IsLoopback);
            Assert.AreEqual(token, request.Headers.Authorization!.Parameter);
            if (request.RequestUri.AbsolutePath.EndsWith("/controllers/synchronize", StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (request.Method == HttpMethod.Delete) { deletes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            attempts++;
            return Task.FromResult(new HttpResponseMessage(attempts == 1 ? HttpStatusCode.Conflict : HttpStatusCode.NoContent));
        }));
        using var api = new RelayApiClient(http);
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector, api);
        var port = HttpsFixture.GetLoopbackPort();
        var settings = new RemoteSettings(true, "127.0.0.1", port, $"https://127.0.0.1:{port}", "https://127.0.0.1:1", manager.ProtectedRelayToken(token));
        await manager.ApplyAsync(settings);
        var code = await manager.BeginRelayPairingAsync();
        Assert.AreEqual(2, attempts); Assert.IsTrue(manager.Security.RelayPairingActive);
        await manager.ApplyAsync(settings with { RelayEnabled = false });
        Assert.IsTrue(manager.Running); Assert.AreEqual("disabled", manager.RelayStatus);
        Assert.IsFalse(manager.Security.RelayPairingActive); Assert.AreEqual(1, deletes);
        Assert.IsNull(manager.Security.CompleteRelayPairing(fixture.DeviceId, new(code.Code, "stale phone")));
    }

    [TestMethod]
    public async Task CurrentWindowsClientsAreSynchronizedBeforeCodePublishingAndLanGrantIssuance()
    {
        using var fixture = new RemoteFixture();
        var order = new List<string>(); var synchronized = new List<Guid[]>();
        var token = NewToken(); var controllerToken = NewToken();
        using var http = new HttpClient(new FakeHandler(async request =>
        {
            Assert.IsTrue(request.RequestUri!.IsLoopback); Assert.AreEqual(token, request.Headers.Authorization!.Parameter);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (request.RequestUri.AbsolutePath.EndsWith("/controllers/synchronize", StringComparison.Ordinal))
            {
                order.Add("synchronize");
                synchronized.Add(WireJson.Parse<RelayClientsRequest>(await request.Content!.ReadAsStringAsync()).ClientIds.ToArray());
                return new(HttpStatusCode.NoContent);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/pairing-code", StringComparison.Ordinal)) { order.Add("pairing-code"); return new(HttpStatusCode.NoContent); }
            order.Add("controller");
            var body = WireJson.Parse<RelayAuthorizeRequest>(await request.Content!.ReadAsStringAsync());
            return Json(new RelayControllerReply(fixture.DeviceId, body.ClientId, controllerToken));
        }));
        using var api = new RelayApiClient(http);
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector, api);
        var port = HttpsFixture.GetLoopbackPort();
        var endpoint = $"https://127.0.0.1:{port}";
        await manager.ApplyAsync(new(true, "127.0.0.1", port, endpoint, "https://127.0.0.1:1", manager.ProtectedRelayToken(token)));
        PairReply Pair()
        {
            var bundle = manager.BeginPairing();
            return manager.Security.CompletePairing(fixture.DeviceId, new(bundle.Code, "phone"))!;
        }
        var revoked = Pair(); var current = Pair(); manager.Security.Revoke(revoked.ClientId);
        await manager.BeginRelayPairingAsync();
        using var pinned = new PinnedHttpClient(new Uri(endpoint), manager.Security.CertificatePin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/relay")
        { Content = new StringContent(WireJson.Serialize(new RelayAuthorizeRequest(current.ClientId)), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", current.Token);
        using var response = await pinned.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { "synchronize", "pairing-code", "synchronize", "controller" }, order);
        Assert.AreEqual(2, synchronized.Count);
        foreach (var clients in synchronized) CollectionAssert.AreEqual(new[] { current.ClientId }, clients);
    }

    [TestMethod]
    public async Task FailedControllerSynchronizationCannotPublishCodeOrReturnLanGrant()
    {
        using var fixture = new RemoteFixture();
        var requests = new List<string>(); var token = NewToken();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            Assert.IsTrue(request.RequestUri!.IsLoopback); requests.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        using var api = new RelayApiClient(http);
        await using var manager = new RemoteManager(fixture.Controller, fixture.DeviceId, fixture.Directory.Path, fixture.Protector, api);
        var port = HttpsFixture.GetLoopbackPort(); var endpoint = $"https://127.0.0.1:{port}";
        await manager.ApplyAsync(new(true, "127.0.0.1", port, endpoint, "https://127.0.0.1:1", manager.ProtectedRelayToken(token)));
        await Assert.ThrowsExceptionAsync<RelayApiException>(() => manager.BeginRelayPairingAsync());
        Assert.IsFalse(manager.Security.RelayPairingActive);
        var bundle = manager.BeginPairing(); var phone = manager.Security.CompletePairing(fixture.DeviceId, new(bundle.Code, "phone"))!;
        using var pinned = new PinnedHttpClient(new Uri(endpoint), manager.Security.CertificatePin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/relay")
        { Content = new StringContent(WireJson.Serialize(new RelayAuthorizeRequest(phone.ClientId)), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", phone.Token);
        using var response = await pinned.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(2, requests.Count);
        Assert.IsTrue(requests.All(path => path.EndsWith("/controllers/synchronize", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NewRelayPairCannotOvertakeAnInflightLanMembershipSynchronization()
    {
        using var fixture = new RemoteFixture();
        var existing = fixture.Pair();
        fixture.Gateway.PublicEndpoint = "https://127.0.0.1:48152";
        fixture.Gateway.RelayEndpoint = "https://relay.test";
        var code = fixture.Security.BeginRelayPairing();
        var synchronizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Gateway.IssueRelayController = async (clientId, _) =>
        {
            synchronizing.SetResult(); await complete.Task;
            return new(fixture.DeviceId, clientId, NewToken());
        };
        var configuration = fixture.Gateway.RelayConfigurationAsync(existing.Token, new(existing.ClientId));
        await synchronizing.Task;
        var paired = fixture.Gateway.PairRelayAsync(new(code.Code, "new remote phone"));
        Assert.IsFalse(paired.IsCompleted);
        Assert.AreEqual(1, fixture.Security.Clients.Count);
        complete.SetResult();
        Assert.AreEqual(200, (await configuration).StatusCode);
        Assert.IsNotNull(await paired);
        Assert.AreEqual(2, fixture.Security.Clients.Count);
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(WireJson.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
