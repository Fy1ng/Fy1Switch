using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Remote.Tests;

[TestClass]
public sealed class HttpsControlServerTests
{
    [TestMethod]
    public async Task PinnedHttpsPairsControlsAndRejectsRevokedToken()
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var client = await fixture.PairAsync();
        Assert.IsTrue(fixture.Endpoint.IsLoopback);
        Assert.IsTrue(fixture.Pinned.ValidationCalls > 0);
        Assert.AreEqual(fixture.Pinned.ValidationCalls, fixture.Pinned.AcceptedCertificates);

        foreach (var command in new[] { "status", "refresh", "set" })
        {
            var request = fixture.Remote.Command(command);
            using var response = await fixture.SendAsync("/v1/control", request, client.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            var reply = WireJson.Parse<RemoteReply>(text);
            Assert.AreEqual(1, reply.Version);
            Assert.IsTrue(reply.Success);
            Assert.AreEqual(request.RequestId, reply.RequestId);
            Assert.IsFalse(text.Contains(FakeDesktopController.PrivateFingerprint, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(FakeDesktopController.PrivateTargetKey, StringComparison.Ordinal));
            if (command == "set")
            {
                Assert.IsNotNull(reply.Operation);
                Assert.AreEqual(request.OperationId, reply.Operation.OperationId);
                Assert.AreEqual(request.TargetId, reply.Operation.TargetId);
                Assert.AreEqual(PowerIntent.Off, reply.Operation.Requested);
                Assert.AreEqual(OperationOutcome.Confirmed, reply.Operation.Outcome);
            }
            else
            {
                Assert.IsNotNull(reply.Status);
                Assert.AreEqual(fixture.Remote.DeviceId, reply.Status.DeviceId);
                Assert.AreEqual(fixture.Remote.Controller.Snapshot.Observation, reply.Status.Observation);
                Assert.IsTrue(reply.Status.Available);
            }
        }
        CollectionAssert.AreEqual(new[] { "status", "refresh", "set" }, fixture.Remote.Controller.Calls.Select(call => call.Method).ToArray());
        fixture.Remote.Security.Revoke(client.ClientId);
        using var revoked = await fixture.SendAsync("/v1/control", fixture.Remote.Command("set"), client.Token);
        Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.AreEqual(3, fixture.Remote.Controller.Calls.Length);
    }

    [TestMethod]
    public async Task WrongTlsPinFailsCertificateAuthenticationBeforePairing()
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var wrongPin = "sha256/" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("intentionally-wrong-test-pin")));
        Assert.AreNotEqual(fixture.Remote.Security.CertificatePin, wrongPin);
        using var rejected = new PinnedHttpClient(fixture.Endpoint, wrongPin);
        var bundle = fixture.Remote.Security.BeginPairing(fixture.Remote.DeviceId, fixture.Settings.PublicEndpoint);
        using var content = new StringContent(WireJson.Serialize(new PairRequest(bundle.Code, "Rejected TLS client")), Encoding.UTF8, "application/json");
        var exception = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => rejected.Client.PostAsync("/v1/pair", content));
        Assert.AreEqual(HttpRequestError.SecureConnectionError, exception.HttpRequestError);
        Assert.IsInstanceOfType<AuthenticationException>(exception.InnerException);
        Assert.IsTrue(rejected.ValidationCalls > 0);
        Assert.AreEqual(0, rejected.AcceptedCertificates);
        Assert.AreEqual(0, fixture.Remote.Security.Clients.Count);
        Assert.AreEqual(0, fixture.Remote.Controller.Calls.Length);
        using var valid = await fixture.SendAsync("/v1/pair", new PairRequest(bundle.Code, "Accepted TLS client"));
        Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControlAuthenticatesBeforeParsingMalformedBody(bool invalidToken)
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var client = await fixture.PairAsync();
        using var response = await fixture.SendRawAsync("/v1/control", "{not-json", invalidToken ? new string('f', 64) : null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, fixture.Remote.Controller.Calls.Length);
        using var authenticated = await fixture.SendRawAsync("/v1/control", "{not-json", client.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, authenticated.StatusCode);
        Assert.AreEqual(0, fixture.Remote.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow("/v1/control", "null", 400)]
    [DataRow("/v1/control", "{broken", 400)]
    [DataRow("/v1/pair", "null", 401)]
    [DataRow("/v1/pair", "{broken", 400)]
    public async Task InvalidJsonOrNullBodiesCannotReachController(string path, string body, int status)
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var client = await fixture.PairAsync();
        using var response = await fixture.SendRawAsync(path, body, client.Token);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        Assert.AreEqual(0, fixture.Remote.Controller.Calls.Length);
        Assert.AreEqual(1, fixture.Remote.Security.Clients.Count);
    }

    [DataTestMethod]
    [DataRow("/v1/pair", 65536, false)]
    [DataRow("/v1/pair", 65537, false)]
    [DataRow("/v1/pair", 65536, true)]
    [DataRow("/v1/pair", 65537, true)]
    [DataRow("/v1/control", 65536, false)]
    [DataRow("/v1/control", 65537, false)]
    [DataRow("/v1/control", 65536, true)]
    [DataRow("/v1/control", 65537, true)]
    public async Task BodySizeBoundaryIsEnforcedForContentLengthAndChunkedTransfer(string path, int bytes, bool chunked)
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var client = await fixture.PairAsync();
        var bundle = fixture.Remote.Security.BeginPairing(fixture.Remote.DeviceId, fixture.Settings.PublicEndpoint);
        var pairRequest = new PairRequest(bundle.Code, "Body boundary client");
        var body = path == "/v1/pair" ? WireJson.Serialize(pairRequest) : WireJson.Serialize(fixture.Remote.Command("set"));
        body += new string(' ', bytes - Encoding.UTF8.GetByteCount(body));
        Assert.AreEqual(bytes, Encoding.UTF8.GetByteCount(body));
        using var response = await fixture.SendRawAsync(path, body, client.Token, chunked);
        if (bytes == 65536)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(path == "/v1/control" ? 1 : 0, fixture.Remote.Controller.Calls.Length);
            Assert.AreEqual(path == "/v1/pair" ? 2 : 1, fixture.Remote.Security.Clients.Count);
        }
        else
        {
            Assert.IsTrue(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
                $"Oversized body returned HTTP {(int)response.StatusCode} instead of a body rejection.");
            Assert.AreEqual(0, fixture.Remote.Controller.Calls.Length);
            Assert.AreEqual(1, fixture.Remote.Security.Clients.Count);
            using var valid = await fixture.SendAsync("/v1/pair", pairRequest);
            Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
        }
    }

    [TestMethod]
    public async Task PairLimiterRejectsEleventhAttemptWithoutConsumingCodeOrBlockingControl()
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var client = fixture.Remote.Pair();
        var bundle = fixture.Remote.Security.BeginPairing(fixture.Remote.DeviceId, fixture.Settings.PublicEndpoint);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var rejected = await fixture.SendAsync("/v1/pair", new PairRequest("incorrect-code", "Rate limited client"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode, $"Attempt {attempt + 1} did not reach pairing rejection.");
        }
        using var limited = await fixture.SendAsync("/v1/pair", new PairRequest(bundle.Code, "Rate limited client"));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual(1, fixture.Remote.Security.Clients.Count);
        using var control = await fixture.SendAsync("/v1/control", fixture.Remote.Command(), client.Token);
        Assert.AreEqual(HttpStatusCode.OK, control.StatusCode);
        Assert.IsNotNull(fixture.Remote.Gateway.Pair(new(bundle.Code, "Still valid code")));
    }

    [TestMethod]
    public async Task PairCodeCannotBeReplayedOverHttps()
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var bundle = fixture.Remote.Security.BeginPairing(fixture.Remote.DeviceId, fixture.Settings.PublicEndpoint);
        var request = new PairRequest(bundle.Code, "Single-use HTTPS client");
        using var first = await fixture.SendAsync("/v1/pair", request);
        using var second = await fixture.SendAsync("/v1/pair", request);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.AreEqual(1, fixture.Remote.Security.Clients.Count);
    }
}
