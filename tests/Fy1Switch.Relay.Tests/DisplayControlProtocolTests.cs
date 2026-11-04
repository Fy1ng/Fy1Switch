using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fy1Switch.Contracts;
using Fy1Switch.Relay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Relay.Tests;

[TestClass]
public sealed class DisplayControlProtocolTests
{
    private static RemoteCommand Command(RelayFixture fixture, string verb = "set-control") => fixture.Command(verb) with
    {
        TargetId = Guid.NewGuid(), OperationId = verb == "controls" ? null : Guid.NewGuid(),
        Control = verb == "set-control" ? DisplayControl.Brightness : null, Value = verb == "set-control" ? 33 : null,
    };
    private static DisplayControlOperation Operation(RemoteCommand command) => new(command.OperationId!.Value, command.TargetId!.Value,
        command.Control ?? DisplayControl.Brightness, command.Value ?? 33, OperationOutcome.Confirmed, null, new(true, 34, true), DateTimeOffset.UtcNow);
    private static RemoteDisplayControls Controls(RemoteCommand command) => new(command.TargetId!.Value,
        [new(DisplayControl.Brightness, true, 33, []), new(DisplayControl.InputSource, true, 15, [15, 17])],
        [new(0x10, "Brightness", []), new(0x60, "Input source", [15, 17])], DateTimeOffset.UtcNow);

    [TestMethod]
    [DataRow("controls")]
    [DataRow("set-control")]
    [DataRow("control-operation")]
    public async Task NewCommandsPreserveIdentityDeadlineArgumentsAndWindowsResult(string verb)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = Command(fixture, verb);
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        Assert.AreEqual(command, envelope.Request);
        Assert.AreEqual(command.RequestId, envelope.Id);
        Assert.AreEqual("control", envelope.Kind);
        Assert.AreEqual(fixture.DeviceToken, envelope.DeviceToken);
        var reply = verb == "controls" ? new RemoteReply(command.RequestId, true, Controls: Controls(command))
            : new RemoteReply(command.RequestId, true, ControlOperation: Operation(command));
        await RelayFixture.ReplyAsync(socket, new(envelope.Id, 200, Reply: reply));
        using var response = await post;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(JsonSerializer.Serialize(reply, RelayJson.Options), await response.Content.ReadAsStringAsync());
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
    }

    [TestMethod]
    [DataRow(DisplayControl.Brightness, 0)]
    [DataRow(DisplayControl.Contrast, 100)]
    [DataRow(DisplayControl.Volume, 42)]
    [DataRow(DisplayControl.InputSource, 17)]
    [DataRow(DisplayControl.ColorTemperature, 5)]
    [DataRow(DisplayControl.Rotation, 3)]
    public async Task EverySupportedControlEnumIsForwardedAsItsStringName(DisplayControl control, int value)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = Command(fixture) with { Control = control, Value = value };
        var post = fixture.PostAsync("control", command);
        var bytes = await RelayFixture.ReceiveBytesAsync(socket);
        using var json = JsonDocument.Parse(bytes);
        Assert.AreEqual(control.ToString(), json.RootElement.GetProperty("request").GetProperty("control").GetString());
        var envelope = JsonSerializer.Deserialize<RelayEnvelope>(bytes, RelayJson.Options)!;
        Assert.AreEqual(command, envelope.Request);
        await RelayFixture.ReplyAsync(socket, new(envelope.Id, 409, Reply: new RemoteReply(command.RequestId, false, "unsupported_control")));
        using var result = await post;
        Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
    }

    [TestMethod]
    public async Task MalformedNewCommandIsRejectedBeforeWebsocketForward()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var valid = Command(fixture);
        foreach (var invalid in new[]
        {
            valid with { TargetId = null }, valid with { TargetId = Guid.Empty }, valid with { OperationId = null },
            valid with { OperationId = Guid.Empty }, valid with { Control = null }, valid with { Value = null },
            valid with { Value = -1 }, valid with { Value = 101 }, valid with { Intent = PowerIntent.On },
            valid with { Control = DisplayControl.Rotation, Value = 4 }, valid with { Control = DisplayControl.InputSource, Value = 256 },
            valid with { Command = "controls" }, valid with { Command = "control-operation" }, valid with { Command = "status" },
            valid with { Command = "wake" }, valid with { Command = "set-control", DeviceId = fixture.OtherDeviceId },
        })
        {
            using var result = await fixture.PostAsync("control", invalid);
            Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
        }
        var post = fixture.PostAsync("control", valid);
        Assert.AreEqual(valid, (await RelayFixture.ReceiveAsync(socket)).Request);
        await RelayFixture.ReplyAsync(socket, new(valid.RequestId, 409, Reply: new RemoteReply(valid.RequestId, false, "fixture")));
        using var accepted = await post;
        Assert.AreEqual(HttpStatusCode.Conflict, accepted.StatusCode);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("\"0\"")]
    [DataRow("\"brightness\"")]
    [DataRow("\"Shutdown\"")]
    public async Task ControlEnumRejectsIntegerAndUnknownWireValues(string replacement)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{fixture.DeviceId}/control");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.ControllerToken);
        request.Headers.Add("X-Device-Token", fixture.DeviceToken);
        var json = JsonSerializer.Serialize(Command(fixture), RelayJson.Options).Replace("\"Brightness\"", replacement, StringComparison.Ordinal);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("target")]
    [DataRow("operation")]
    [DataRow("control")]
    [DataRow("value")]
    [DataRow("status")]
    [DataRow("power-operation")]
    [DataRow("controls")]
    [DataRow("missing")]
    public async Task MismatchedOperationPayloadIsRejected(string mismatch)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = Command(fixture);
        var operation = Operation(command);
        var reply = new RemoteReply(command.RequestId, true, ControlOperation: operation);
        reply = mismatch switch
        {
            "target" => reply with { ControlOperation = operation with { TargetId = Guid.NewGuid() } },
            "operation" => reply with { ControlOperation = operation with { OperationId = Guid.NewGuid() } },
            "control" => reply with { ControlOperation = operation with { Control = DisplayControl.Contrast } },
            "value" => reply with { ControlOperation = operation with { RequestedValue = 80 } },
            "status" => reply with { Status = new(fixture.DeviceId, command.TargetId, "fixture", new(DisplayPower.Unknown, null, DateTimeOffset.UtcNow, "fixture"), false, false) },
            "power-operation" => reply with { Operation = new(command.OperationId!.Value, command.TargetId!.Value, PowerIntent.On, PowerIntent.On, OperationOutcome.Pending, null, null, DateTimeOffset.UtcNow) },
            "controls" => reply with { Controls = Controls(command) },
            _ => reply with { ControlOperation = null },
        };
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        await RelayFixture.ReplyAsync(socket, new(envelope.Id, 200, Reply: reply));
        using var result = await post;
        Assert.AreEqual(HttpStatusCode.BadGateway, result.StatusCode);
        StringAssert.Contains(await result.Content.ReadAsStringAsync(), "invalid_device_response");
    }

    [TestMethod]
    [DataRow("target")]
    [DataRow("duplicate-control")]
    [DataRow("null-controls")]
    [DataRow("null-vcp")]
    [DataRow("wrong-payload")]
    public async Task MismatchedCapabilitiesAreRejected(string mismatch)
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = Command(fixture, "controls");
        var controls = Controls(command);
        var reply = new RemoteReply(command.RequestId, true, Controls: mismatch switch
        {
            "target" => controls with { TargetId = Guid.NewGuid() },
            "duplicate-control" => controls with { Controls = [controls.Controls[0], controls.Controls[0]] },
            "null-controls" => controls with { Controls = null! },
            "null-vcp" => controls with { VcpCodes = null! },
            _ => controls,
        });
        if (mismatch == "wrong-payload") reply = reply with { Controls = null, ControlOperation = Operation(Command(fixture)) };
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        await RelayFixture.ReplyAsync(socket, new(envelope.Id, 200, Reply: reply));
        using var result = await post;
        Assert.AreEqual(HttpStatusCode.BadGateway, result.StatusCode);
    }

    [TestMethod]
    public async Task NewCommandsKeepExistingOfflineAndReplaySemantics()
    {
        await using var fixture = await RelayFixture.StartAsync();
        var command = Command(fixture);
        using var offline = await fixture.PostAsync("control", command);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        StringAssert.Contains(await offline.Content.ReadAsStringAsync(), "device_offline");
        using var socket = await fixture.ConnectAsync();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        using var duplicate = await fixture.PostAsync("control", command);
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        var reply = new RemoteReply(command.RequestId, true, ControlOperation: Operation(command) with
        { Outcome = OperationOutcome.AcceptedUnconfirmed, Result = new(true, null, false) });
        await RelayFixture.ReplyAsync(socket, new(envelope.Id, 200, Reply: reply));
        using var result = await post;
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        StringAssert.Contains(await result.Content.ReadAsStringAsync(), "AcceptedUnconfirmed");
    }

    [TestMethod]
    public async Task NewControlsDoNotBypassExistingCredentials()
    {
        await using var fixture = await RelayFixture.StartAsync();
        var command = Command(fixture);
        foreach (var token in new[] { fixture.WindowsToken, fixture.OtherControllerToken, "wrong" })
        {
            using var denied = await fixture.PostAsync("control", command, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var absent = await fixture.PostAsync("control", command, includeDeviceToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, absent.StatusCode);
    }

    [TestMethod]
    public async Task OldPowerRequestCannotReceiveNewControlsPayload()
    {
        await using var fixture = await RelayFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var command = fixture.Command();
        var post = fixture.PostAsync("control", command);
        var envelope = await RelayFixture.ReceiveAsync(socket);
        await RelayFixture.ReplyAsync(socket, new(envelope.Id, 200, Reply: new RemoteReply(command.RequestId, true, Controls: Controls(Command(fixture, "controls")))));
        using var result = await post;
        Assert.AreEqual(HttpStatusCode.BadGateway, result.StatusCode);
    }

    [TestMethod]
    public void LegacyMessagesOmitNewNullFieldsAndRemainStrictlyDeserializable()
    {
        var command = new RemoteCommand(Guid.NewGuid(), Guid.NewGuid(), "set", DateTimeOffset.UtcNow.AddSeconds(30), Guid.NewGuid(), PowerIntent.Off, Guid.NewGuid());
        var commandJson = JsonSerializer.Serialize(command, RelayJson.Options);
        using var requestDocument = JsonDocument.Parse(commandJson);
        Assert.IsFalse(requestDocument.RootElement.TryGetProperty("control", out _));
        Assert.IsFalse(requestDocument.RootElement.TryGetProperty("value", out _));
        Assert.IsNotNull(JsonSerializer.Deserialize<LegacyCommand>(commandJson, RelayJson.Options));
        var reply = new RemoteReply(command.RequestId, true, Status: new(command.DeviceId, command.TargetId, "fixture", new(DisplayPower.On, 1, DateTimeOffset.UtcNow, "fixture"), true, false));
        var replyJson = JsonSerializer.Serialize(reply, RelayJson.Options);
        using var replyDocument = JsonDocument.Parse(replyJson);
        Assert.IsFalse(replyDocument.RootElement.TryGetProperty("controls", out _));
        Assert.IsFalse(replyDocument.RootElement.TryGetProperty("controlOperation", out _));
        Assert.IsNotNull(JsonSerializer.Deserialize<LegacyReply>(replyJson, RelayJson.Options));
    }

    private sealed record LegacyCommand(Guid RequestId, Guid DeviceId, string Command, DateTimeOffset ExpiresAt, Guid? TargetId = null, PowerIntent? Intent = null, Guid? OperationId = null, int Version = 1);
    private sealed record LegacyReply(Guid RequestId, bool Success, string? ErrorCode = null, RemoteStatus? Status = null, OperationRecord? Operation = null, int Version = 1);
}
