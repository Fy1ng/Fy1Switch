using System.Text.Json;
using Fy1Switch.Contracts;
using Fy1Switch.Remote;
using Fy1Switch.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class RemoteDisplayControlTests
{
    internal static DisplayControlsSnapshot Controls(RemoteFixture fixture) => new(FakeDesktopController.PrivateTargetKey,
        "Private monitor", "private-monitor-id", [
            new(DisplayControl.Brightness, true, 50, []), new(DisplayControl.Contrast, true, 60, []),
            new(DisplayControl.Volume, false, null, []), new(DisplayControl.InputSource, true, 15, [15, 17]),
            new(DisplayControl.ColorTemperature, true, 5, [5, 8]), new(DisplayControl.Rotation, true, 0, [0, 1, 2, 3])],
        [new(0x10, "Brightness", [])], fixture.Clock.Now);

    internal static void Enable(RemoteFixture fixture)
    {
        fixture.Controller.ControlsHandler = (_, _) => Task.FromResult(Controls(fixture));
        fixture.Controller.ControlWriteHandler = (_, _, value, _) => Task.FromResult(new DisplayControlWriteResult(true, value, true));
    }

    internal static RemoteCommand Request(RemoteFixture fixture, string command = "set-control") =>
        new(Guid.NewGuid(), fixture.DeviceId, command, fixture.Clock.Now.AddSeconds(60), fixture.Controller.Snapshot.Binding!.TargetId,
            OperationId: command == "controls" ? null : Guid.NewGuid(),
            Control: command == "set-control" ? DisplayControl.Brightness : null, Value: command == "set-control" ? 75 : null);

    [TestMethod]
    public async Task ControlsProjectionExcludesPrivateTopologyAndDoesNotWrite()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var reply = await fixture.Gateway.ControlAsync(client.Token, Request(fixture, "controls"));
        Assert.AreEqual(200, reply.StatusCode);
        Assert.AreEqual(fixture.Controller.Snapshot.Binding!.TargetId, reply.Reply.Controls!.TargetId);
        Assert.AreEqual(6, reply.Reply.Controls.Controls.Count);
        var json = WireJson.Serialize(reply.Reply);
        foreach (var privateValue in new[] { "private-monitor-id", FakeDesktopController.PrivateTargetKey, FakeDesktopController.PrivateFingerprint })
            Assert.IsFalse(json.Contains(privateValue, StringComparison.Ordinal));
        Assert.IsFalse(fixture.Controller.Calls.Any(call => call.Method == "set-control"));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory.Path, "remote-display-controls.db")));
    }

    [DataTestMethod]
    [DataRow("controls")]
    [DataRow("set-control")]
    [DataRow("control-operation")]
    public async Task AuthenticationAndRevocationPrecedeParameterAccess(string command)
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        Assert.AreEqual(401, (await fixture.Gateway.ControlAsync(null, Request(fixture, command))).StatusCode);
        fixture.Security.Revoke(client.ClientId);
        Assert.AreEqual(401, (await fixture.Gateway.ControlAsync(client.Token, Request(fixture, command))).StatusCode);
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory.Path, "remote-display-controls.db")));
    }

    [DataTestMethod]
    [DataRow("target")]
    [DataRow("empty-target")]
    [DataRow("operation")]
    [DataRow("control")]
    [DataRow("unknown-control")]
    [DataRow("value")]
    [DataRow("negative")]
    [DataRow("over-range")]
    [DataRow("intent")]
    public async Task InvalidSetParametersNeverReachController(string invalid)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var valid = Request(fixture);
        var request = invalid switch
        {
            "target" => valid with { TargetId = null }, "empty-target" => valid with { TargetId = Guid.Empty },
            "operation" => valid with { OperationId = null }, "control" => valid with { Control = null },
            "unknown-control" => valid with { Control = (DisplayControl)100 }, "value" => valid with { Value = null },
            "negative" => valid with { Value = -1 }, "over-range" => valid with { Value = 101 },
            _ => valid with { Intent = PowerIntent.Toggle }
        };
        var result = await fixture.Gateway.ControlAsync(client.Token, request);
        Assert.AreEqual(400, result.StatusCode);
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow("status")]
    [DataRow("refresh")]
    [DataRow("set")]
    [DataRow("operation")]
    public async Task NewFieldsCannotChangeTheMeaningOfOldPowerCommands(string command)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var reply = await fixture.Gateway.ControlAsync(client.Token, fixture.Command(command) with { Control = DisplayControl.Brightness, Value = 10 });
        Assert.AreEqual(400, reply.StatusCode);
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow("controls")]
    [DataRow("control-operation")]
    public async Task ReadCommandsRejectWriteFields(string command)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        Assert.AreEqual(400, (await fixture.Gateway.ControlAsync(client.Token, Request(fixture, command) with { Value = 1 })).StatusCode);
        Assert.AreEqual(400, (await fixture.Gateway.ControlAsync(client.Token, Request(fixture, command) with { Intent = PowerIntent.On })).StatusCode);
        if (command == "controls")
            Assert.AreEqual(400, (await fixture.Gateway.ControlAsync(client.Token, Request(fixture, command) with { OperationId = Guid.NewGuid() })).StatusCode);
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow(DisplayControl.Volume, 20, "control_unsupported")]
    [DataRow(DisplayControl.InputSource, 3, "invalid_control_value")]
    [DataRow(DisplayControl.ColorTemperature, 7, "invalid_control_value")]
    public async Task DeviceCapabilitiesGateWrites(DisplayControl control, int value, string expectedError)
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var reply = await fixture.Gateway.ControlAsync(client.Token, Request(fixture) with { Control = control, Value = value });
        Assert.AreEqual(409, reply.StatusCode);
        Assert.AreEqual(expectedError, reply.Reply.ErrorCode);
        Assert.IsFalse(fixture.Controller.Calls.Any(call => call.Method == "set-control"));
    }

    [DataTestMethod]
    [DataRow("unbound", "not_bound")]
    [DataRow("different", "target_changed")]
    [DataRow("invalidated", "topology_changed")]
    public async Task UnboundChangedAndInvalidatedTargetsCannotBeReadOrWritten(string state, string error)
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var request = Request(fixture);
        fixture.Controller.Snapshot = fixture.Controller.Snapshot with { Binding = state switch
        {
            "unbound" => null, "different" => fixture.Controller.Snapshot.Binding! with { TargetId = Guid.NewGuid() },
            _ => fixture.Controller.Snapshot.Binding! with { NeedsConfirmation = true }
        } };
        foreach (var command in new[] { request, request with { Command = "controls", Control = null, Value = null, OperationId = null } })
        {
            var result = await fixture.Gateway.ControlAsync(client.Token, command);
            Assert.AreEqual(409, result.StatusCode);
            Assert.AreEqual(error, result.Reply.ErrorCode);
        }
        Assert.IsFalse(fixture.Controller.Calls.Any(call => call.Method is "read-controls" or "set-control"));
    }

    [TestMethod]
    public async Task OperationIsDurableBeforeDispatchAndDuplicateIdsNeverWriteAgain()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var request = Request(fixture);
        fixture.Controller.ControlWriteHandler = (_, _, value, _) =>
        {
            var pending = ReadJournal(fixture, request.OperationId!.Value);
            Assert.AreEqual(OperationOutcome.Pending, pending.Outcome);
            return Task.FromResult(new DisplayControlWriteResult(true, value, true));
        };
        var first = await fixture.Gateway.ControlAsync(client.Token, request);
        var second = await fixture.Gateway.ControlAsync(client.Token, request with { RequestId = Guid.NewGuid() });
        var restarted = new RemoteGateway(fixture.Controller, fixture.Security, fixture.DeviceId, fixture.Clock.Read);
        var third = await restarted.ControlAsync(client.Token, request with { RequestId = Guid.NewGuid() });
        Assert.AreEqual(OperationOutcome.Confirmed, first.Reply.ControlOperation!.Outcome);
        Assert.AreEqual(first.Reply.ControlOperation, second.Reply.ControlOperation);
        Assert.AreEqual(first.Reply.ControlOperation, third.Reply.ControlOperation);
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task DuplicateConflictsDoNotWriteOrExposeAnUnrelatedTarget()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var request = Request(fixture);
        await fixture.Gateway.ControlAsync(client.Token, request);
        foreach (var changed in new[] { request with { Value = 20 }, request with { Control = DisplayControl.Contrast }, request with { TargetId = Guid.NewGuid() } })
        {
            var reply = await fixture.Gateway.ControlAsync(client.Token, changed);
            Assert.AreEqual(409, reply.StatusCode);
            Assert.AreEqual("operation_conflict", reply.Reply.ErrorCode);
        }
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task RestartRecoversPendingAsUnknownAndQueryNeverWritesEvenAfterUnbinding()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var request = Request(fixture);
        await fixture.Gateway.ControlAsync(client.Token, request);
        var pending = ReadJournal(fixture, request.OperationId!.Value) with { Outcome = OperationOutcome.Pending, Result = null };
        WriteJournal(fixture, pending);
        var restarted = new RemoteGateway(fixture.Controller, fixture.Security, fixture.DeviceId, fixture.Clock.Read);
        fixture.Controller.Snapshot = fixture.Controller.Snapshot with { Binding = null };
        var query = request with { Command = "control-operation", Control = null, Value = null };
        var result = await restarted.ControlAsync(client.Token, query);
        Assert.AreEqual(OperationOutcome.Unknown, result.Reply.ControlOperation!.Outcome);
        Assert.AreEqual("host_restarted", result.Reply.ControlOperation.ErrorCode);
        Assert.AreEqual(OperationOutcome.Unknown, (await restarted.ControlAsync(client.Token, request)).Reply.ControlOperation!.Outcome);
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task LeftoverPendingAfterPersistenceFailureDoesNotRemainActiveOrReplay()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var phone = fixture.Pair();
        var request = Request(fixture);
        await fixture.Gateway.ControlAsync(phone.Token, request);
        WriteJournal(fixture, ReadJournal(fixture, request.OperationId!.Value) with { Outcome = OperationOutcome.Pending, Result = null });
        var reply = await fixture.Gateway.ControlAsync(phone.Token, request);
        Assert.AreEqual(OperationOutcome.Unknown, reply.Reply.ControlOperation!.Outcome);
        Assert.AreEqual("result_persistence_failed", reply.Reply.ControlOperation.ErrorCode);
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task CapacityFailsClosedButKeepsAllExistingResultsQueryable()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var service = new RemoteDisplayControlService(fixture.Controller, fixture.Security, fixture.DeviceId, fixture.Clock.Read, journalCapacity: 1);
        var request = Request(fixture);
        await service.HandleAsync(client.Token, request, CancellationToken.None);
        var denied = await service.HandleAsync(client.Token, Request(fixture), CancellationToken.None);
        Assert.AreEqual(503, denied.StatusCode);
        Assert.AreEqual("operation_journal_full", denied.Reply.ErrorCode);
        Assert.AreEqual(OperationOutcome.Confirmed, (await service.HandleAsync(client.Token, request, CancellationToken.None)).Reply.ControlOperation!.Outcome);
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [DataTestMethod]
    [DataRow(true, true, OperationOutcome.Confirmed)]
    [DataRow(true, false, OperationOutcome.AcceptedUnconfirmed)]
    [DataRow(false, false, OperationOutcome.Failed)]
    [DataRow(null, false, OperationOutcome.Unknown)]
    public async Task WriteAcknowledgementIsNotMisrepresentedAsConfirmed(bool? accepted, bool confirmed, OperationOutcome outcome)
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        fixture.Controller.ControlWriteHandler = (_, _, value, _) => Task.FromResult(new DisplayControlWriteResult(accepted, confirmed ? value : null, confirmed));
        var reply = await fixture.Gateway.ControlAsync(client.Token, Request(fixture));
        Assert.AreEqual(outcome, reply.Reply.ControlOperation!.Outcome);
    }

    [TestMethod]
    public async Task RevocationDuringCapabilityReadPreventsDispatch()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        fixture.Controller.ControlsHandler = (_, _) =>
        {
            fixture.Security.Revoke(client.ClientId);
            return Task.FromResult(Controls(fixture));
        };
        Assert.AreEqual(401, (await fixture.Gateway.ControlAsync(client.Token, Request(fixture))).StatusCode);
        Assert.IsFalse(fixture.Controller.Calls.Any(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task LostReplyAfterDispatchIsUnknownAndIsNotRetried()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        fixture.Controller.ControlWriteHandler = (_, _, _, _) => throw new OperationCanceledException();
        var request = Request(fixture);
        var reply = await fixture.Gateway.ControlAsync(client.Token, request);
        Assert.AreEqual(OperationOutcome.Unknown, reply.Reply.ControlOperation!.Outcome);
        Assert.AreEqual("deadline_exceeded_result_unknown", reply.Reply.ControlOperation.ErrorCode);
        Assert.AreEqual(reply.Reply.ControlOperation, (await fixture.Gateway.ControlAsync(client.Token, request)).Reply.ControlOperation);
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task ConcurrentPhonesAndRetransmitsSerializeAndDeduplicate()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var firstPhone = fixture.Pair();
        var secondPhone = fixture.Pair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.ControlWriteHandler = async (_, _, value, cancellation) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return new(true, value, true);
        };
        var request = Request(fixture);
        var first = fixture.Gateway.ControlAsync(firstPhone.Token, request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = fixture.Gateway.ControlAsync(firstPhone.Token, request with { RequestId = Guid.NewGuid() });
        var other = fixture.Gateway.ControlAsync(secondPhone.Token, Request(fixture) with { Value = 30 });
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
        release.TrySetResult();
        await Task.WhenAll(first, duplicate, other).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(first.Result.Reply.ControlOperation, duplicate.Result.Reply.ControlOperation);
        Assert.AreEqual(2, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task CancelBeforeDispatchHasNoOperationAndNoWrite()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        fixture.Controller.ControlsHandler = (_, _) => throw new OperationCanceledException();
        var request = Request(fixture);
        var result = await fixture.Gateway.ControlAsync(client.Token, request);
        Assert.AreEqual(504, result.StatusCode);
        Assert.AreEqual("request_cancelled_before_dispatch", result.Reply.ErrorCode);
        Assert.IsFalse(fixture.Controller.Calls.Any(call => call.Method == "set-control"));
        var query = request with { Command = "control-operation", Control = null, Value = null };
        Assert.AreEqual(404, (await fixture.Gateway.ControlAsync(client.Token, query)).StatusCode);
    }

    [TestMethod]
    public async Task TopologyInvalidatedBetweenReadAndDispatchPersistsDefiniteFailure()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var snapshots = 0;
        fixture.Controller.SnapshotHandler = _ =>
        {
            if (++snapshots == 3) fixture.Controller.Snapshot = fixture.Controller.Snapshot with
            { Binding = fixture.Controller.Snapshot.Binding! with { NeedsConfirmation = true } };
            return Task.FromResult(fixture.Controller.Snapshot);
        };
        var request = Request(fixture);
        var result = await fixture.Gateway.ControlAsync(client.Token, request);
        Assert.AreEqual(OperationOutcome.Failed, result.Reply.ControlOperation!.Outcome);
        Assert.AreEqual("topology_changed", result.Reply.ControlOperation.ErrorCode);
        Assert.AreEqual(result.Reply.ControlOperation, (await fixture.Gateway.ControlAsync(client.Token, request)).Reply.ControlOperation);
        Assert.IsFalse(fixture.Controller.Calls.Any(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task RevokedQueuedPhoneCannotDispatchAfterAnotherPhoneCompletes()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var firstPhone = fixture.Pair();
        var queuedPhone = fixture.Pair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.ControlWriteHandler = async (_, _, value, cancellation) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return new(true, value, true);
        };
        var first = fixture.Gateway.ControlAsync(firstPhone.Token, Request(fixture));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = fixture.Gateway.ControlAsync(queuedPhone.Token, Request(fixture));
        fixture.Security.Revoke(queuedPhone.ClientId);
        release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(401, (await queued.WaitAsync(TimeSpan.FromSeconds(5))).StatusCode);
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task RevocationAfterDispatchPreservesResultButRejectsTheRevokedCaller()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        var remaining = fixture.Pair();
        fixture.Controller.ControlWriteHandler = (_, _, value, _) =>
        {
            fixture.Security.Revoke(client.ClientId);
            return Task.FromResult(new DisplayControlWriteResult(true, value, true));
        };
        var request = Request(fixture);
        Assert.AreEqual(401, (await fixture.Gateway.ControlAsync(client.Token, request)).StatusCode);
        var query = request with { Command = "control-operation", Control = null, Value = null };
        Assert.AreEqual(OperationOutcome.Confirmed, (await fixture.Gateway.ControlAsync(remaining.Token, query)).Reply.ControlOperation!.Outcome);
        Assert.AreEqual(1, fixture.Controller.Calls.Count(call => call.Method == "set-control"));
    }

    [TestMethod]
    public async Task ContinuousReadbackMayBeQuantizedButDiscreteReadbackMustMatch()
    {
        using var fixture = new RemoteFixture();
        Enable(fixture);
        var client = fixture.Pair();
        fixture.Controller.ControlWriteHandler = (_, _, _, _) => Task.FromResult(new DisplayControlWriteResult(true, 34, true));
        var quantized = await fixture.Gateway.ControlAsync(client.Token, Request(fixture) with { Value = 33 });
        Assert.AreEqual(OperationOutcome.Confirmed, quantized.Reply.ControlOperation!.Outcome);
        Assert.AreEqual(34, quantized.Reply.ControlOperation.Result!.Value);
        fixture.Controller.ControlWriteHandler = (_, _, _, _) => Task.FromResult(new DisplayControlWriteResult(true, 15, true));
        var inconsistent = await fixture.Gateway.ControlAsync(client.Token, Request(fixture) with { Control = DisplayControl.InputSource, Value = 17 });
        Assert.AreEqual(OperationOutcome.Unknown, inconsistent.Reply.ControlOperation!.Outcome);
        Assert.AreEqual("worker_protocol_error", inconsistent.Reply.ControlOperation.ErrorCode);
    }

    [TestMethod]
    public async Task ExistingLegacyPowerRequestDoesNotCreateParameterJournal()
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        Assert.AreEqual(200, (await fixture.Gateway.ControlAsync(client.Token, fixture.Command("set"))).StatusCode);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory.Path, "remote-display-controls.db")));
    }

    [TestMethod]
    public void OldCommandAndReplySerializationHasNoNewNullFields()
    {
        using var fixture = new RemoteFixture();
        using var command = JsonDocument.Parse(WireJson.Serialize(fixture.Command()));
        using var reply = JsonDocument.Parse(WireJson.Serialize(new RemoteReply(Guid.NewGuid(), true)));
        Assert.IsFalse(command.RootElement.TryGetProperty("control", out _));
        Assert.IsFalse(command.RootElement.TryGetProperty("value", out _));
        Assert.IsFalse(reply.RootElement.TryGetProperty("controls", out _));
        Assert.IsFalse(reply.RootElement.TryGetProperty("controlOperation", out _));
    }

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("99")]
    [DataRow("\"0\"")]
    [DataRow("\"brightness\"")]
    [DataRow("\"Unknown\"")]
    public void RemoteControlEnumRejectsNumbersAndUnknownStrings(string value)
    {
        Assert.ThrowsException<JsonException>(() => WireJson.Parse<RemoteCommand>("{\"control\":" + value + "}"));
        Assert.AreEqual(DisplayControl.Brightness, WireJson.Parse<RemoteCommand>("{\"control\":\"Brightness\"}").Control);
    }

    private static SqliteConnection Journal(RemoteFixture fixture)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(fixture.Directory.Path, "remote-display-controls.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static DisplayControlOperation ReadJournal(RemoteFixture fixture, Guid id)
    {
        using var connection = Journal(fixture);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM control_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return WireJson.Parse<DisplayControlOperation>((string)command.ExecuteScalar()!);
    }

    private static void WriteJournal(RemoteFixture fixture, DisplayControlOperation operation)
    {
        using var connection = Journal(fixture);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE control_operations SET payload=$payload WHERE id=$id";
        command.Parameters.AddWithValue("$id", operation.OperationId.ToString());
        command.Parameters.AddWithValue("$payload", WireJson.Serialize(operation));
        command.ExecuteNonQuery();
    }
}
