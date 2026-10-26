using System.Net;
using System.Text.Json;
using Fy1Switch.Contracts;
using Fy1Switch.Relay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Relay.Tests;

[TestClass]
public sealed class EnrollmentTests
{
    [TestMethod]
    public async Task RegistrationCodeIsRequiredAndSingleUseAndCannotOverwriteRegisteredWindows()
    {
        await using var fixture = await ManagedRelayFixture.StartAsync();
        var code = fixture.Registry.GenerateRegistrationCode();
        var id = Guid.NewGuid();
        using var wrong = await fixture.PostAsync("/v1/relay/register", new RelayRegistrationRequest(id, "bad", "fixture"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var accepted = await fixture.PostAsync("/v1/relay/register", new RelayRegistrationRequest(id, code.Code, "fixture"));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        var registration = JsonSerializer.Deserialize<RelayRegistrationReply>(await accepted.Content.ReadAsStringAsync(), RelayJson.Options)!;
        using var replay = await fixture.PostAsync("/v1/relay/register", new RelayRegistrationRequest(Guid.NewGuid(), code.Code, "fixture"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);
        var other = fixture.Registry.GenerateRegistrationCode();
        using var overwrite = await fixture.PostAsync("/v1/relay/register", new RelayRegistrationRequest(id, other.Code, "overwrite"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, overwrite.StatusCode);
        Assert.IsTrue(fixture.Registry.Authenticate(id, registration.WindowsToken, true));
        Assert.IsFalse(fixture.Registry.Authenticate(id, registration.WindowsToken, false));
    }

    [TestMethod]
    public async Task FirstPhonePairsThroughWindowsThenExistingPhoneAuthorizesWithoutCode()
    {
        await using var fixture = await ManagedRelayFixture.StartAsync();
        var registration = await fixture.RegisterAsync();
        using var windows = await fixture.ConnectAsync(registration);
        var code = new RelayCode("000071", DateTimeOffset.UtcNow.AddMinutes(4));
        using var published = await fixture.PostAsync($"/v1/devices/{registration.DeviceId}/pairing-code", code, registration.WindowsToken);
        Assert.AreEqual(HttpStatusCode.OK, published.StatusCode);
        var pairing = fixture.PostAsync("/v1/relay/pair", new RelayJoinRequest(code.Code, "phone"));
        var first = await RelayFixture.ReceiveAsync(windows);
        Assert.AreEqual("relay-pair", first.Kind);
        Assert.AreEqual(code.Code, first.Pair!.Code);
        var clientId = Guid.NewGuid();
        var token = new string('a', 64);
        var identity = new RelayBootstrapReply(registration.DeviceId, clientId, token, "https://192.168.1.5:48152", "sha256/" + Convert.ToBase64String(new byte[32]), DeviceName: "Windows desktop");
        await RelayFixture.ReplyAsync(windows, new(first.Id, 200, Bootstrap: identity));
        using var paired = await pairing;
        Assert.AreEqual(HttpStatusCode.OK, paired.StatusCode);
        var grant = JsonSerializer.Deserialize<RelayPairingReply>(await paired.Content.ReadAsStringAsync(), RelayJson.Options)!;
        Assert.AreEqual(token, grant.DeviceToken);
        Assert.AreEqual(clientId, grant.ClientId);
        Assert.AreEqual(identity.DeviceName, grant.DeviceName);
        Assert.IsTrue(fixture.Registry.Authenticate(registration.DeviceId, grant.ControllerToken, false));
        Assert.IsFalse(fixture.Registry.Authenticate(registration.DeviceId, grant.ControllerToken, true));
        using var replay = await fixture.PostAsync("/v1/relay/pair", new RelayJoinRequest(code.Code, "other"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);

        var authorization = fixture.PostAsync($"/v1/devices/{registration.DeviceId}/authorize", new RelayAuthorizeRequest(clientId), deviceToken: token);
        var second = await RelayFixture.ReceiveAsync(windows);
        Assert.AreEqual("authorize", second.Kind);
        Assert.AreEqual(token, second.DeviceToken);
        Assert.AreEqual(clientId, second.Authorize!.ClientId);
        await RelayFixture.ReplyAsync(windows, new(second.Id, 200, Bootstrap: new(registration.DeviceId, clientId)));
        using var authorized = await authorization;
        Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode);
        var same = JsonSerializer.Deserialize<RelayPairingReply>(await authorized.Content.ReadAsStringAsync(), RelayJson.Options)!;
        Assert.AreEqual(grant.ControllerToken, same.ControllerToken);
        Assert.IsNull(same.DeviceToken);
        Assert.IsNull(same.CertificatePin);
        Assert.IsNull(same.DeviceName);
    }

    [TestMethod]
    [DataRow("reject")]
    [DataRow("wrong-client")]
    [DataRow("wrong-device")]
    [DataRow("unexpected-secret")]
    public async Task ExistingPhoneCannotGainGrantWithoutCorrectWindowsValidation(string problem)
    {
        await using var fixture = await ManagedRelayFixture.StartAsync();
        var registration = await fixture.RegisterAsync();
        using var socket = await fixture.ConnectAsync(registration);
        var clientId = Guid.NewGuid();
        var pending = fixture.PostAsync($"/v1/devices/{registration.DeviceId}/authorize", new RelayAuthorizeRequest(clientId), deviceToken: new string('b', 64));
        var request = await RelayFixture.ReceiveAsync(socket);
        var bootstrap = new RelayBootstrapReply(problem == "wrong-device" ? Guid.NewGuid() : registration.DeviceId,
            problem == "wrong-client" ? Guid.NewGuid() : clientId, problem == "unexpected-secret" ? "unexpected" : null);
        await RelayFixture.ReplyAsync(socket, problem == "reject" ? new(request.Id, 401, ErrorCode: "unauthorized") : new(request.Id, 200, Bootstrap: bootstrap));
        using var response = await pending;
        Assert.AreEqual(problem == "reject" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadGateway, response.StatusCode);
    }

    [TestMethod]
    public async Task OfflineAndRevokedWindowsCannotAuthorizeAndCredentialsStayRevoked()
    {
        await using var fixture = await ManagedRelayFixture.StartAsync();
        var registration = await fixture.RegisterAsync();
        var clientId = Guid.NewGuid();
        using var offline = await fixture.PostAsync($"/v1/devices/{registration.DeviceId}/authorize", new RelayAuthorizeRequest(clientId), deviceToken: new string('c', 64));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        var grant = fixture.Registry.ControllerFor(registration.DeviceId, clientId)!;
        using var socket = await fixture.ConnectAsync(registration);
        Assert.IsTrue(fixture.Registry.Revoke(registration.DeviceId));
        fixture.Router.Disconnect(registration.DeviceId);
        Assert.IsFalse(fixture.Registry.Authenticate(registration.DeviceId, registration.WindowsToken, true));
        Assert.IsFalse(fixture.Registry.Authenticate(registration.DeviceId, grant.ControllerToken, false));
        Assert.IsFalse(fixture.Router.IsConnected(registration.DeviceId));
        using var revoked = await fixture.PostAsync($"/v1/devices/{registration.DeviceId}/authorize", new RelayAuthorizeRequest(clientId), deviceToken: new string('c', 64));
        Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [TestMethod]
    public async Task OnlyWindowsCredentialCanPublishCodesOrProvisionPhoneGrants()
    {
        await using var fixture = await ManagedRelayFixture.StartAsync();
        var registration = await fixture.RegisterAsync();
        var clientId = Guid.NewGuid();
        var grant = fixture.Registry.ControllerFor(registration.DeviceId, clientId)!;
        using var rejected = await fixture.PostAsync($"/v1/devices/{registration.DeviceId}/controllers", new RelayAuthorizeRequest(Guid.NewGuid()), grant.ControllerToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var code = await fixture.PostAsync($"/v1/devices/{registration.DeviceId}/pairing-code", new RelayCode("987654", DateTimeOffset.UtcNow.AddMinutes(4)), grant.ControllerToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, code.StatusCode);
        using var noToken = await fixture.PostAsync($"/v1/devices/{registration.DeviceId}/authorize", new RelayAuthorizeRequest(clientId));
        Assert.AreEqual(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }
}
