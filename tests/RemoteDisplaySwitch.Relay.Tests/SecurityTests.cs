using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Relay;

namespace RemoteDisplaySwitch.Relay.Tests;

[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    [DataRow("control")]
    [DataRow("pair")]
    public async Task ControllerAuthenticationIsRequiredAndRoleAndDeviceBound(string endpoint)
    {
        await using var fixture = await RelayFixture.StartAsync();
        object body = endpoint == "control" ? fixture.Command() : new PairRequest("fixture", "fixture");
        foreach (var token in new[] { "wrong", fixture.WindowsToken, fixture.OtherControllerToken })
        {
            using var response = await fixture.PostAsync(endpoint, body, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var absent = await fixture.Client.PostAsync($"/v1/devices/{fixture.DeviceId}/{endpoint}", new StringContent("{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, absent.StatusCode);
        using var unconfigured = await fixture.PostAsync(endpoint, body, routeId: Guid.NewGuid());
        Assert.AreEqual(HttpStatusCode.Unauthorized, unconfigured.StatusCode);
    }

    [TestMethod]
    public async Task WindowsAuthenticationRejectsWrongRoleWrongDeviceAndUnconfiguredDevice()
    {
        await using var fixture = await RelayFixture.StartAsync();
        foreach (var token in new[] { "wrong", fixture.ControllerToken, fixture.OtherWindowsToken })
        {
            using var socket = fixture.NewSocket(token);
            await Assert.ThrowsExactlyAsync<WebSocketException>(() => fixture.ConnectAsync(socket));
            Assert.AreEqual(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
        }
        using var unconfigured = fixture.NewSocket();
        await Assert.ThrowsExactlyAsync<WebSocketException>(() => fixture.ConnectAsync(unconfigured, Guid.NewGuid()));
        Assert.AreEqual(HttpStatusCode.Unauthorized, unconfigured.HttpStatusCode);
    }

    [TestMethod]
    public async Task SecondConnectionReturns409WithoutReplacingFirstAndDevicesStayIsolated()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var first = await fixture.ConnectAsync();
        using var second = fixture.NewSocket();
        await Assert.ThrowsExactlyAsync<WebSocketException>(() => fixture.ConnectAsync(second));
        Assert.AreEqual(HttpStatusCode.Conflict, second.HttpStatusCode);
        using var other = await fixture.ConnectAsync(fixture.OtherWindowsToken, fixture.OtherDeviceId);
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(first);
        await RelayFixture.ReplyAsync(other, new RelayResponse(envelope.Id, 200, new RemoteReply(command.RequestId, true)));
        await Task.Delay(40);
        Assert.IsFalse(post.IsCompleted);
        await RelayFixture.ReplyAsync(first, new RelayResponse(envelope.Id, 409, new RemoteReply(command.RequestId, false, "fixture")));
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    [DataRow("device")]
    [DataRow("version")]
    [DataRow("uuid")]
    [DataRow("deadline")]
    [DataRow("management")]
    [DataRow("casing")]
    public async Task InvalidCommandIsRejectedBeforeAnyWebSocketForward(string invalid)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var valid = fixture.Command();
        var command = invalid switch
        {
            "device" => valid with { DeviceId = fixture.OtherDeviceId },
            "version" => valid with { Version = 2 },
            "uuid" => valid with { RequestId = Guid.Empty },
            "deadline" => valid with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(121) },
            "management" => valid with { Command = "shutdown" },
            _ => valid with { Command = "Status" }
        };
        using var rejected = await fixture.PostAsync("control", command);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        var post = fixture.PostAsync("control", valid);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        Assert.AreEqual(valid, envelope.Request);
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 200, new RemoteReply(valid.RequestId, true)));
        using var accepted = await post;
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
    }

    [TestMethod]
    public async Task MissingDeviceTokenAndMalformedJsonAreRejected()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var missing = await fixture.PostAsync("control", fixture.Command(), includeDeviceToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.ControllerToken);
        fixture.Client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        foreach (var json in new[] { "{", "{}", "null", "{\"requestId\":null}", JsonSerializer.Serialize(fixture.Command() with { Intent = PowerIntent.On }, RelayJson.Options).Replace("\"On\"", "1", StringComparison.Ordinal) })
        {
            using var response = await fixture.Client.PostAsync($"/v1/devices/{fixture.DeviceId}/control", new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HttpBodyOver64KiBIsRejectedWithOrWithoutContentLength(bool chunked)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{fixture.DeviceId}/pair");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.ControllerToken);
        request.Headers.TransferEncodingChunked = chunked;
        request.Content = new StringContent(new string(' ', RelayRouter.MessageLimit + 1), Encoding.UTF8, "application/json");
        using var response = await fixture.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [TestMethod]
    public async Task SpoofedForwardedHeadersCannotBypassUnauthenticatedRateLimit()
    {
        await using var fixture = await RelayFixture.StartAsync(rateLimit: 2);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{fixture.DeviceId}/pair");
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{attempt + 1}");
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("Forwarded", $"for=198.51.100.{attempt + 1};proto=https");
            using var response = await fixture.Client.SendAsync(request);
            Assert.AreEqual(attempt < 2 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }
}
