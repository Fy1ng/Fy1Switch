using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Runtime;

namespace RemoteDisplaySwitch.Remote.Tests;

[TestClass]
public sealed class RemoteGatewayTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public async Task AuthenticationPrecedesValidationAndControllerAccess(string? token)
    {
        using var fixture = new RemoteFixture();
        fixture.Pair();
        var request = fixture.Command("bind") with { Version = 99, DeviceId = Guid.NewGuid(), RequestId = Guid.Empty, ExpiresAt = fixture.Clock.Now.AddDays(-1) };
        var result = await fixture.Gateway.ControlAsync(token, request);
        AssertRejected(result, 401, "unauthorized");
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CrossDeviceAndEmptyDeviceIdsAreRejected(bool empty)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var request = fixture.Command() with { DeviceId = empty ? Guid.Empty : Guid.NewGuid() };
        AssertRejected(await fixture.Gateway.ControlAsync(client.Token, request), 400, "invalid_request");
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(2)]
    public async Task UnsupportedVersionsAreRejected(int version)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        AssertRejected(await fixture.Gateway.ControlAsync(client.Token, fixture.Command() with { Version = version }), 400, "invalid_request");
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [TestMethod]
    public async Task EmptyRequestIdIsRejected()
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        AssertRejected(await fixture.Gateway.ControlAsync(client.Token, fixture.Command() with { RequestId = Guid.Empty }), 400, "invalid_request");
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow(-1, false)]
    [DataRow(0, false)]
    [DataRow(10000, true)]
    [DataRow(120000, true)]
    [DataRow(120001, false)]
    public async Task DeadlineBoundariesUseInjectedClock(int milliseconds, bool accepted)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        var request = fixture.Command() with { ExpiresAt = fixture.Clock.Now.AddMilliseconds(milliseconds) };
        var result = await fixture.Gateway.ControlAsync(client.Token, request);
        if (accepted) Assert.AreEqual(200, result.StatusCode);
        else AssertRejected(result, 400, "request_expired_or_invalid_deadline");
        Assert.AreEqual(accepted ? 1 : 0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow("discover")]
    [DataRow("bind")]
    [DataRow("unbind")]
    [DataRow("startup")]
    [DataRow("set_startup")]
    [DataRow("shutdown")]
    [DataRow("topology")]
    [DataRow("Status")]
    [DataRow("")]
    [DataRow(null)]
    public async Task IllegalCommandsNeverReachLocalManagement(string? command)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var result = await fixture.Gateway.ControlAsync(client.Token, fixture.Command(command!));
        AssertRejected(result, 400, "invalid_command");
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow("missing-target")]
    [DataRow("missing-intent")]
    [DataRow("missing-operation")]
    [DataRow("invalid-intent")]
    [DataRow("empty-target")]
    [DataRow("empty-operation")]
    public async Task InvalidSetFieldsAreRejectedBeforeControllerAccess(string invalidField)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var valid = fixture.Command("set");
        var request = invalidField switch
        {
            "missing-target" => valid with { TargetId = null },
            "missing-intent" => valid with { Intent = null },
            "missing-operation" => valid with { OperationId = null },
            "invalid-intent" => valid with { Intent = (PowerIntent)99 },
            "empty-target" => valid with { TargetId = Guid.Empty },
            "empty-operation" => valid with { OperationId = Guid.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };
        var result = await fixture.Gateway.ControlAsync(client.Token, request);
        Assert.AreEqual(0, fixture.Controller.Calls.Length, $"Invalid set field '{invalidField}' reached IDesktopController.ExecuteAsync (HTTP {result.StatusCode}).");
        AssertRejected(result, 400, "invalid_request");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OperationRequiresNonemptyId(bool empty)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var request = fixture.Command("operation") with { OperationId = empty ? Guid.Empty : null };
        AssertRejected(await fixture.Gateway.ControlAsync(client.Token, request), 400, "invalid_request");
        Assert.AreEqual(0, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow(PowerIntent.On)]
    [DataRow(PowerIntent.Off)]
    [DataRow(PowerIntent.Toggle)]
    public async Task SetPassesSameOperationIdThroughWithoutGatewayDeduplication(PowerIntent intent)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var request = fixture.Command("set") with { Intent = intent };
        var expected = new OperationRecord(request.OperationId!.Value, request.TargetId!.Value, intent, PowerIntent.On,
            OperationOutcome.AcceptedUnconfirmed, null, fixture.Controller.Snapshot.Observation, fixture.Clock.Now);
        fixture.Controller.Operation = expected;
        var first = await fixture.Gateway.ControlAsync(client.Token, request);
        var secondRequest = request with { RequestId = Guid.NewGuid() };
        var second = await fixture.Gateway.ControlAsync(client.Token, secondRequest);
        Assert.AreEqual(200, first.StatusCode);
        Assert.AreEqual(200, second.StatusCode);
        Assert.AreEqual(request.RequestId, first.Reply.RequestId);
        Assert.AreEqual(secondRequest.RequestId, second.Reply.RequestId);
        Assert.AreSame(expected, first.Reply.Operation);
        Assert.AreSame(expected, second.Reply.Operation);
        CollectionAssert.AreEqual(new[] { new ControllerCall("set", request.TargetId, intent, request.OperationId), new ControllerCall("set", request.TargetId, intent, request.OperationId) }, fixture.Controller.Calls);
    }

    [TestMethod]
    public async Task OperationLookupPassesIdThroughAndReturnsNotFoundOrExactRecord()
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var request = fixture.Command("operation");
        AssertRejected(await fixture.Gateway.ControlAsync(client.Token, request), 404, "operation_not_found");
        var expected = new OperationRecord(request.OperationId!.Value, request.TargetId!.Value, PowerIntent.Off, PowerIntent.Off,
            OperationOutcome.Confirmed, null, fixture.Controller.Snapshot.Observation, fixture.Clock.Now);
        fixture.Controller.Operation = expected;
        var result = await fixture.Gateway.ControlAsync(client.Token, request);
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreSame(expected, result.Reply.Operation);
        Assert.IsTrue(fixture.Controller.Calls.All(call => call == new ControllerCall("operation", OperationId: request.OperationId)));
    }

    [DataTestMethod]
    [DataRow("status", "ready", true)]
    [DataRow("refresh", "ready", true)]
    [DataRow("status", "confirmation", false)]
    [DataRow("status", "error", false)]
    [DataRow("status", "unbound", false)]
    public async Task StatusProjectionExposesOnlyRemoteFields(string command, string state, bool available)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var original = fixture.Controller.Snapshot;
        fixture.Controller.Snapshot = state switch
        {
            "confirmation" => original with { Binding = original.Binding! with { NeedsConfirmation = true } },
            "error" => original with { ErrorCode = "private-local-failure" },
            "unbound" => original with { Binding = null },
            _ => original with { Busy = true }
        };
        var request = fixture.Command(command);
        var result = await fixture.Gateway.ControlAsync(client.Token, request);
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsTrue(result.Reply.Success);
        Assert.AreEqual(1, result.Reply.Version);
        Assert.AreEqual(request.RequestId, result.Reply.RequestId);
        var status = result.Reply.Status;
        Assert.IsNotNull(status);
        Assert.AreEqual(fixture.DeviceId, status.DeviceId);
        Assert.AreEqual(fixture.Controller.Snapshot.Binding?.TargetId, status.TargetId);
        Assert.AreEqual(fixture.Controller.Snapshot.Binding?.Name, status.DisplayName);
        Assert.AreEqual(original.Observation, status.Observation);
        Assert.AreEqual(available, status.Available);
        Assert.AreEqual(fixture.Controller.Snapshot.Busy, status.Busy);
        CollectionAssert.AreEqual(new[] { new ControllerCall(command) }, fixture.Controller.Calls);
        var serialized = WireJson.Serialize(result.Reply);
        foreach (var secret in new[] { FakeDesktopController.PrivateFingerprint, FakeDesktopController.PrivateTargetKey, FakeDesktopController.PrivateProfile, "private-local-failure" })
            Assert.IsFalse(serialized.Contains(secret, StringComparison.Ordinal), "Remote status leaked local-only data.");
        using var document = JsonDocument.Parse(serialized);
        CollectionAssert.AreEquivalent(new[] { "deviceId", "targetId", "displayName", "observation", "available", "busy", "tailscaleEndpoint" },
            document.RootElement.GetProperty("status").EnumerateObject().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public async Task EightInFlightCallsRejectNinthWithoutQueuingAndReleaseSlots()
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.SnapshotHandler = async cancellation =>
        {
            await release.Task.WaitAsync(cancellation);
            return fixture.Controller.Snapshot;
        };
        var pending = Enumerable.Range(0, 8).Select(_ => fixture.Gateway.ControlAsync(client.Token, fixture.Command())).ToArray();
        try
        {
            Assert.AreEqual(8, fixture.Controller.Calls.Length);
            Assert.IsTrue(pending.All(task => !task.IsCompleted));
            var rejected = await fixture.Gateway.ControlAsync(client.Token, fixture.Command()).WaitAsync(TimeSpan.FromSeconds(5));
            AssertRejected(rejected, 429, "busy");
            Assert.AreEqual(8, fixture.Controller.Calls.Length);
        }
        finally { release.TrySetResult(); await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.IsTrue(pending.All(task => task.Result.StatusCode == 200));
        Assert.AreEqual(200, (await fixture.Gateway.ControlAsync(client.Token, fixture.Command())).StatusCode);
        Assert.AreEqual(9, fixture.Controller.Calls.Length);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ControllerFailuresAreMappedWithoutLeakingExceptionAndReleaseSlots(bool controlFailure)
    {
        using var fixture = new RemoteFixture();
        var client = fixture.Pair();
        fixture.Controller.SnapshotHandler = _ => throw (controlFailure ? new ControlException("binding_required") : new InvalidOperationException("private-test-exception"));
        for (var attempt = 0; attempt < 9; attempt++)
        {
            var result = await fixture.Gateway.ControlAsync(client.Token, fixture.Command());
            AssertRejected(result, controlFailure ? 409 : 500, controlFailure ? "binding_required" : "internal_error");
            Assert.IsFalse(WireJson.Serialize(result.Reply).Contains("private-test-exception", StringComparison.Ordinal));
        }
        fixture.Controller.SnapshotHandler = null;
        Assert.AreEqual(200, (await fixture.Gateway.ControlAsync(client.Token, fixture.Command())).StatusCode);
    }

    [TestMethod]
    public async Task CancellationReachesControllerAndMapsUnknownResultWithoutLeakingSlot()
    {
        using var fixture = new RemoteFixture();
        using var cancellation = new CancellationTokenSource();
        var client = fixture.Pair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.SnapshotHandler = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return fixture.Controller.Snapshot;
        };
        var pending = fixture.Gateway.ControlAsync(client.Token, fixture.Command(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        AssertRejected(await pending.WaitAsync(TimeSpan.FromSeconds(5)), 504, "deadline_exceeded_result_unknown");
        fixture.Controller.SnapshotHandler = null;
        Assert.AreEqual(200, (await fixture.Gateway.ControlAsync(client.Token, fixture.Command())).StatusCode);
    }

    private static void AssertRejected((int StatusCode, RemoteReply Reply) result, int status, string error)
    {
        Assert.AreEqual(status, result.StatusCode);
        Assert.IsFalse(result.Reply.Success);
        Assert.AreEqual(error, result.Reply.ErrorCode);
        Assert.IsNull(result.Reply.Status);
        Assert.IsNull(result.Reply.Operation);
    }
}
