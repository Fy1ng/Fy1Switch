using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Fy1Switch.Contracts;
using Fy1Switch.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class RemoteDisplayControlHttpsTests
{
    [TestMethod]
    public async Task LanAndSecondaryHttpsRetryTheSameDurableOperationAndPreservePowerControl()
    {
        using var fixture = new RemoteFixture();
        RemoteDisplayControlTests.Enable(fixture);
        await using var server = new HttpsControlServer(fixture.Gateway, fixture.Security);
        var port = HttpsFixture.GetLoopbackPort();
        var settings = new RemoteSettings(true, "127.0.0.1", port, $"https://127.0.0.1:{port}",
            TailscaleEndpoint: $"https://127.0.0.2:{port}", TailscaleListenAddress: "127.0.0.2");
        await server.StartAsync(settings);
        var phone = fixture.Pair();
        using var lan = Client(settings.PublicEndpoint, fixture.Security);
        using var alternate = Client(settings.TailscaleEndpoint!, fixture.Security);
        var request = RemoteDisplayControlTests.Request(fixture);
        foreach (var path in new[] { lan, alternate })
        {
            using var response = await Send(path, request with { RequestId = Guid.NewGuid() }, phone.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(OperationOutcome.Confirmed, WireJson.Parse<RemoteReply>(await response.Content.ReadAsStringAsync()).ControlOperation!.Outcome);
        }
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
        using var controls = await Send(alternate, RemoteDisplayControlTests.Request(fixture, "controls"), phone.Token);
        Assert.AreEqual(6, WireJson.Parse<RemoteReply>(await controls.Content.ReadAsStringAsync()).Controls!.Controls.Count);
        using var power = await Send(lan, fixture.Command("set"), phone.Token);
        Assert.AreEqual(OperationOutcome.Confirmed, WireJson.Parse<RemoteReply>(await power.Content.ReadAsStringAsync()).Operation!.Outcome);
        fixture.Security.Revoke(phone.ClientId);
        using var revoked = await Send(alternate, request, phone.Token);
        Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("\"0\"")]
    [DataRow("\"not-a-control\"")]
    public async Task HttpsRejectsInvalidControlWireTypesBeforeAnyWrite(string control)
    {
        await using var fixture = await HttpsFixture.StartAsync();
        RemoteDisplayControlTests.Enable(fixture.Remote);
        var phone = await fixture.PairAsync();
        var json = WireJson.Serialize(RemoteDisplayControlTests.Request(fixture.Remote));
        json = json.Replace("\"Brightness\"", control, StringComparison.Ordinal);
        using var response = await fixture.SendRawAsync("/v1/control", json, phone.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsFalse(fixture.Remote.Controller.Calls.Any(call => call.Method == "set-control"));
    }

    private static HttpClient Client(string endpoint, DeviceSecurity security)
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        // Both fixture endpoints are loopback and must present this exact temporary test certificate.
        handler.ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
            request.RequestUri is { IsLoopback: true } uri && uri.GetLeftPart(UriPartial.Authority) == endpoint
            && certificate is not null && CryptographicOperations.FixedTimeEquals(certificate.RawData, security.Certificate.RawData);
        return new(handler) { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(10) };
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, RemoteCommand command, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/control")
        { Content = new StringContent(WireJson.Serialize(command), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
