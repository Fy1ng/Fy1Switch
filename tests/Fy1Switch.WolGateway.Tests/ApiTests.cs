using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fy1Switch.WolGateway;

namespace Fy1Switch.WolGateway.Tests;

[TestClass]
public sealed class ApiTests
{
    [TestMethod]
    public async Task PublicRegistrationAuthorizedWakeQueryAndRevokeRoundTrip()
    {
        await using var fixture = await GatewayFixture.Start();
        var data = fixture.Data;
        var code = data.Store.GenerateCode([data.DeviceId]);
        using var registered = await fixture.Send(HttpMethod.Post, "/v1/wol/register", new RegistrationRequest(1, code.Code, "Test phone"));
        Assert.AreEqual(HttpStatusCode.OK, registered.StatusCode);
        Assert.AreEqual("no-store", registered.Headers.CacheControl!.ToString());
        var credential = await GatewayFixture.Body<RegistrationReply>(registered);
        using var devices = await fixture.Send(HttpMethod.Get, "/v1/wol/devices", token: credential.AccessToken);
        Assert.AreEqual(data.DeviceId, (await GatewayFixture.Body<DeviceList>(devices)).Devices.Single().DeviceId);
        var request = data.Wake();
        using var sent = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{data.DeviceId}/wake", request, credential.AccessToken);
        var operation = await GatewayFixture.Body<WakeOperation>(sent);
        Assert.AreEqual(HttpStatusCode.OK, sent.StatusCode);
        Assert.AreEqual("packet_sent", operation.Status);
        Assert.AreEqual(1, fixture.Executor.WakeCount);
        using var queried = await fixture.Send(HttpMethod.Get, $"/v1/wol/devices/{data.DeviceId}/operations/{request.OperationId}", token: credential.AccessToken);
        Assert.AreEqual(operation, await GatewayFixture.Body<WakeOperation>(queried));
        using var revoke = await fixture.Send(HttpMethod.Delete, "/v1/wol/clients/me", token: credential.AccessToken);
        Assert.AreEqual(HttpStatusCode.NoContent, revoke.StatusCode);
        using var denied = await fixture.Send(HttpMethod.Get, "/v1/wol/devices", token: credential.AccessToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [TestMethod]
    public async Task MissingTokenAndWrongDeviceNeverExecute()
    {
        await using var fixture = await GatewayFixture.Start();
        using var noToken = await fixture.Send(HttpMethod.Get, "/v1/wol/devices");
        Assert.AreEqual(HttpStatusCode.Unauthorized, noToken.StatusCode);
        var phone = fixture.Data.Register();
        using var forbidden = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.OtherDeviceId}/wake", fixture.Data.Wake(fixture.Data.OtherDeviceId), phone.AccessToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.AreEqual(0, fixture.Executor.WakeCount);
    }

    [TestMethod]
    public async Task RejectMalformedUnknownDuplicateAndOversizedBody()
    {
        await using var fixture = await GatewayFixture.Start();
        foreach (var json in new[] { "null", "{", "[]", "{}", "{\"version\":1,\"version\":1,\"registrationCode\":\"x\",\"clientName\":\"x\"}",
            "{\"version\":1,\"registrationCode\":\"x\",\"clientName\":\"x\",\"command\":\"reboot\"}" })
        {
            using var response = await fixture.Client.PostAsync("/v1/wol/register", new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, json);
        }
        using var large = await fixture.Client.PostAsync("/v1/wol/register", new StringContent(new string('x', 8193), Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        using var text = await fixture.Client.PostAsync("/v1/wol/register", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
    }

    [TestMethod]
    public async Task EnvelopeValidationIncludesRouteExpiryAndNonemptyOperation()
    {
        await using var fixture = await GatewayFixture.Start();
        var phone = fixture.Data.Register();
        var request = fixture.Data.Wake();
        foreach (var invalid in new[] { request with { DeviceId = fixture.Data.OtherDeviceId }, request with { OperationId = Guid.Empty },
            request with { RequestId = Guid.Empty }, request with { Version = 2 }, request with { ExpiresAt = request.IssuedAt },
            request with { ExpiresAt = request.ExpiresAt.AddMinutes(10) } })
        {
            using var response = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/wake", invalid, phone.AccessToken);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.AreEqual(0, fixture.Executor.WakeCount);
    }

    [TestMethod]
    public async Task SuccessfulSendDoesNotExposeWindowsOrDisplayPowerState()
    {
        await using var fixture = await GatewayFixture.Start();
        var phone = fixture.Data.Register();
        using var sent = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/wake", fixture.Data.Wake(), phone.AccessToken);
        var body = await sent.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("packet_sent", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("online", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(body.Contains("display", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(body.Contains(phone.AccessToken, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains(fixture.Data.Settings.Ssh.Host, StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("tunnel_unavailable", 503)]
    [DataRow("ssh_unreachable", 503)]
    [DataRow("ssh_auth_failed", 502)]
    [DataRow("ssh_host_key_failed", 502)]
    [DataRow("execution_timeout", 504)]
    public async Task CheckReturnsSpecificErrorWithoutSendingWake(string error, int status)
    {
        await using var fixture = await GatewayFixture.Start();
        fixture.Executor.CheckResult = new("failed", error);
        var phone = fixture.Data.Register();
        using var response = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/check", fixture.Data.Check(), phone.AccessToken);
        Assert.AreEqual(status, (int)response.StatusCode);
        Assert.AreEqual(error, (await GatewayFixture.Body<GatewayError>(response)).ErrorCode);
        Assert.AreEqual(0, fixture.Executor.WakeCount);
    }

    [TestMethod]
    public async Task WakeErrorsRemainQueryableOperations()
    {
        await using var fixture = await GatewayFixture.Start();
        fixture.Executor.WakeResult = new("unknown", "execution_timeout");
        var phone = fixture.Data.Register();
        var request = fixture.Data.Wake();
        using var response = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/wake", request, phone.AccessToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("unknown", (await GatewayFixture.Body<WakeOperation>(response)).Status);
        using var repeated = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/wake", request with { RequestId = Guid.NewGuid() }, phone.AccessToken);
        Assert.AreEqual("unknown", (await GatewayFixture.Body<WakeOperation>(repeated)).Status);
        Assert.AreEqual(1, fixture.Executor.WakeCount);
    }

    [TestMethod]
    public async Task RevocationFromSeparateStoreImmediatelyDisablesHttpToken()
    {
        await using var fixture = await GatewayFixture.Start();
        var phone = fixture.Data.Register();
        new GatewayStore(fixture.Data.Settings, fixture.Data.Clock).Revoke(phone.ClientId);
        using var response = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/wake", fixture.Data.Wake(), phone.AccessToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, fixture.Executor.WakeCount);
    }

    [TestMethod]
    public async Task ExpiredReplayCannotSendButOriginalOperationCanBeQueried()
    {
        await using var fixture = await GatewayFixture.Start();
        var phone = fixture.Data.Register();
        var request = fixture.Data.Wake();
        using var initial = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/wake", request, phone.AccessToken);
        fixture.Data.Clock.Advance(TimeSpan.FromMinutes(2));
        using var replay = await fixture.Send(HttpMethod.Post, $"/v1/wol/devices/{fixture.Data.DeviceId}/wake", request, phone.AccessToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.AreEqual("request_expired", (await GatewayFixture.Body<GatewayError>(replay)).ErrorCode);
        using var query = await fixture.Send(HttpMethod.Get, $"/v1/wol/devices/{fixture.Data.DeviceId}/operations/{request.OperationId}", token: phone.AccessToken);
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
        Assert.AreEqual(1, fixture.Executor.WakeCount);
    }

    [TestMethod]
    public async Task ForgedForwardingHeadersCannotBypassGlobalRateLimit()
    {
        await using var fixture = await GatewayFixture.Start();
        var limited = false;
        for (var i = 0; i < 125; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            request.Headers.Add("X-Forwarded-For", "192.0.2." + (i + 1));
            using var response = await fixture.Client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { limited = true; break; }
        }
        Assert.IsTrue(limited);
    }
}
