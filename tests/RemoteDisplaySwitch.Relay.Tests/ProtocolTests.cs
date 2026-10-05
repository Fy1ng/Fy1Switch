using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Relay;

namespace RemoteDisplaySwitch.Relay.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    [DataRow("status")]
    [DataRow("refresh")]
    [DataRow("set")]
    [DataRow("operation")]
    public async Task ControlPreservesSameUuidFieldsEnumsAndDeviceResponse(string verb)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command(verb) with { Intent = PowerIntent.Off, TargetId = Guid.NewGuid(), OperationId = Guid.NewGuid() };
        var post = fixture.PostAsync("control", command);
        var bytes = await RelayFixture.ReceiveBytesAsync(socket);
        using var json = JsonDocument.Parse(bytes);
        Assert.AreEqual("Off", json.RootElement.GetProperty("request").GetProperty("intent").GetString());
        var envelope = JsonSerializer.Deserialize<RelayEnvelope>(bytes, RelayJson.Options)!;
        Assert.AreEqual(command.RequestId, envelope.Id);
        Assert.AreEqual(command, envelope.Request);
        Assert.AreEqual("control", envelope.Kind);
        Assert.IsTrue(string.Equals(fixture.DeviceToken, envelope.DeviceToken, StringComparison.Ordinal));
        Assert.IsNull(envelope.Pair);
        var reply = new RemoteReply(command.RequestId, false, "device_denied");
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 423, reply));
        using var response = await post;
        Assert.AreEqual((HttpStatusCode)423, response.StatusCode);
        Assert.AreEqual(reply, JsonSerializer.Deserialize<RemoteReply>(await response.Content.ReadAsStringAsync(), RelayJson.Options));
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
    }

    [TestMethod]
    public async Task SuccessfulControlUsesWindowsReplyAndContractEnumCasing()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        var observation = new PowerObservation(DisplayPower.On, 1, DateTimeOffset.UtcNow, "fixture");
        var reply = new RemoteReply(command.RequestId, true, Status: new RemoteStatus(fixture.DeviceId, null, "fixture", observation, true, false));
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 200, reply));
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(text, "\"state\":\"On\"");
        Assert.AreEqual(reply, JsonSerializer.Deserialize<RemoteReply>(text, RelayJson.Options));
    }

    [TestMethod]
    public async Task PairPreservesRequestAndReturnsOnlyWindowsGeneratedCredentials()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var pair = new PairRequest("fixture-code", "fixture-phone");
        var post = fixture.PostAsync("pair", pair, includeDeviceToken: false);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        Assert.AreEqual("pair", envelope.Kind);
        Assert.AreNotEqual(Guid.Empty, envelope.Id);
        Assert.IsNull(envelope.DeviceToken);
        Assert.IsNull(envelope.Request);
        Assert.AreEqual(pair, envelope.Pair);
        var reply = new PairReply(fixture.DeviceId, Guid.NewGuid(), fixture.DeviceToken);
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 201, Pair: reply));
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.IsTrue(reply == JsonSerializer.Deserialize<PairReply>(await response.Content.ReadAsStringAsync(), RelayJson.Options));
    }

    [TestMethod]
    public async Task PairErrorIsNotFabricatedSuccess()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var post = fixture.PostAsync("pair", new PairRequest("wrong", "fixture"));
        var envelope = await RelayFixture.ReceiveAsync(socket);
        await RelayFixture.ReplyAsync(socket, new RelayResponse(envelope.Id, 403, ErrorCode: "pair_code_invalid"));
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "pair_code_invalid");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("wrong-id")]
    [DataRow("wrong-version")]
    [DataRow("wrong-device")]
    [DataRow("wrong-kind")]
    [DataRow("bad-status")]
    public async Task InvalidDeviceResponsesReturnBadGateway(string problem)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        var reply = new RemoteReply(command.RequestId, true);
        var response = problem switch
        {
            "missing" => new RelayResponse(envelope.Id, 200),
            "wrong-id" => new RelayResponse(envelope.Id, 200, reply with { RequestId = Guid.NewGuid() }),
            "wrong-version" => new RelayResponse(envelope.Id, 200, reply with { Version = 2 }),
            "wrong-device" => new RelayResponse(envelope.Id, 200, reply with { Status = new RemoteStatus(Guid.NewGuid(), null, null, new PowerObservation(DisplayPower.On, null, DateTimeOffset.UtcNow, "fixture"), true, false) }),
            "wrong-kind" => new RelayResponse(envelope.Id, 200, Pair: new PairReply(fixture.DeviceId, Guid.NewGuid(), fixture.DeviceToken)),
            _ => new RelayResponse(envelope.Id, 101, reply)
        };
        await RelayFixture.ReplyAsync(socket, response);
        using var result = await post;
        Assert.AreEqual(HttpStatusCode.BadGateway, result.StatusCode);
    }

    [TestMethod]
    public async Task FragmentedResponseAtExact64KiBLimitIsAccepted()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new RelayResponse(envelope.Id, 200, new RemoteReply(command.RequestId, true)), RelayJson.Options);
        var bytes = Enumerable.Repeat((byte)' ', RelayRouter.MessageLimit).ToArray();
        serialized.CopyTo(bytes, 0);
        await socket.SendAsync(bytes.AsMemory(0, 30000), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(bytes.AsMemory(30000), WebSocketMessageType.Text, true, CancellationToken.None);
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
