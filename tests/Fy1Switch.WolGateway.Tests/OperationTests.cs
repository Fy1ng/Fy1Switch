using Fy1Switch.WolGateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fy1Switch.WolGateway.Tests;

[TestClass]
public sealed class OperationTests
{
    private static WakeService Service(TestStore data, FakeExecutor executor) => new(data.Store, data.Settings, executor, data.Clock, NullLogger<WakeService>.Instance);

    [TestMethod]
    public async Task DuplicateRequestAndNetworkRetryExecuteOnce()
    {
        using var data = new TestStore();
        var client = data.Register().ClientId;
        var executor = new FakeExecutor();
        var service = Service(data, executor);
        data.Clock.Advance(TimeSpan.FromTicks(12345));
        var request = data.Wake();
        var result = await service.Wake(client, request, CancellationToken.None);
        var same = await service.Wake(client, request, CancellationToken.None);
        var retried = await service.Wake(client, request with { RequestId = Guid.NewGuid() }, CancellationToken.None);
        Assert.AreEqual("packet_sent", result.Status);
        Assert.AreEqual(result, same);
        Assert.AreEqual(result, retried);
        Assert.AreEqual(1, executor.WakeCount);
    }

    [TestMethod]
    public async Task ConcurrentRetryObservesPendingWithoutStartingSecondExecution()
    {
        using var data = new TestStore();
        var client = data.Register().ClientId;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeExecutor { BeforeReturn = ct => release.Task.WaitAsync(ct) };
        var service = Service(data, executor);
        var request = data.Wake();
        var initial = service.Wake(client, request, CancellationToken.None);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await service.Wake(client, request with { RequestId = Guid.NewGuid() }, CancellationToken.None);
        Assert.AreEqual("pending", duplicate.Status);
        Assert.AreEqual("pending", data.Store.Operation(client, data.DeviceId, request.OperationId).Status);
        Assert.AreEqual(1, executor.WakeCount);
        release.SetResult();
        Assert.AreEqual("packet_sent", (await initial).Status);
    }

    [TestMethod]
    public void SameOperationWithChangedPayloadAndRequestReuseAreRejected()
    {
        using var data = new TestStore();
        var client = data.Register(data.DeviceId, data.OtherDeviceId).ClientId;
        var request = data.Wake();
        data.Store.AdmitWake(client, request);
        foreach (var changed in new[] { request with { RequestId = Guid.NewGuid(), DeviceId = data.OtherDeviceId },
            request with { RequestId = Guid.NewGuid(), ExpiresAt = request.ExpiresAt.AddSeconds(1) } })
            Assert.AreEqual("operation_conflict", Assert.ThrowsException<GatewayException>(() => data.Store.AdmitWake(client, changed)).Code);
        Assert.AreEqual("request_replayed", Assert.ThrowsException<GatewayException>(() => data.Store.AdmitWake(client, request with { OperationId = Guid.NewGuid() })).Code);
    }

    [TestMethod]
    public async Task DifferentPhonesShareDevicePendingAndCooldown()
    {
        using var data = new TestStore();
        var first = data.Register().ClientId;
        var second = data.Register(data.DeviceId, data.OtherDeviceId).ClientId;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeExecutor { BeforeReturn = ct => release.Task.WaitAsync(ct) };
        var service = Service(data, executor);
        var operation = service.Wake(first, data.Wake(), CancellationToken.None);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("device_busy", (await Assert.ThrowsExceptionAsync<GatewayException>(() => service.Wake(second, data.Wake(), CancellationToken.None))).Code);
        release.SetResult();
        await operation;
        Assert.AreEqual("device_cooldown", (await Assert.ThrowsExceptionAsync<GatewayException>(() => service.Wake(second, data.Wake(), CancellationToken.None))).Code);
        Assert.AreEqual("packet_sent", (await service.Wake(second, data.Wake(data.OtherDeviceId), CancellationToken.None)).Status);
        data.Clock.Advance(TimeSpan.FromSeconds(30));
        await service.Wake(second, data.Wake(), CancellationToken.None);
        Assert.AreEqual(3, executor.WakeCount);
    }

    [TestMethod]
    public async Task CrashAfterAdmissionBecomesUnknownAndIsNotReplayed()
    {
        using var data = new TestStore();
        var client = data.Register().ClientId;
        var request = data.Wake();
        Assert.IsTrue(data.Store.AdmitWake(client, request).Execute);
        var restarted = new GatewayStore(data.Settings, data.Clock);
        restarted.RecoverInterruptedOperations();
        var executor = new FakeExecutor();
        var service = new WakeService(restarted, data.Settings, executor, data.Clock, NullLogger<WakeService>.Instance);
        var result = await service.Wake(client, request with { RequestId = Guid.NewGuid() }, CancellationToken.None);
        Assert.AreEqual("unknown", result.Status);
        Assert.AreEqual("gateway_restarted", result.ErrorCode);
        Assert.AreEqual(0, executor.WakeCount);
    }

    [DataTestMethod]
    [DataRow("failed", "tunnel_unavailable")]
    [DataRow("failed", "ssh_auth_failed")]
    [DataRow("failed", "ssh_host_key_failed")]
    [DataRow("unknown", "execution_timeout")]
    [DataRow("unknown", "ssh_failed")]
    public async Task ExecutionFailureIsDurableAndNeverRetried(string status, string code)
    {
        using var data = new TestStore();
        var client = data.Register().ClientId;
        var executor = new FakeExecutor { WakeResult = new(status, code) };
        var request = data.Wake();
        var result = await Service(data, executor).Wake(client, request, CancellationToken.None);
        Assert.AreEqual(status, result.Status);
        Assert.AreEqual(code, result.ErrorCode);
        Assert.AreEqual(result, data.Store.Operation(client, data.DeviceId, request.OperationId));
        await Service(data, executor).Wake(client, request, CancellationToken.None);
        Assert.AreEqual(1, executor.WakeCount);
    }

    [TestMethod]
    public async Task CheckSendsNoPacketAndReplayIsRejected()
    {
        using var data = new TestStore();
        var client = data.Register().ClientId;
        var executor = new FakeExecutor();
        var request = data.Check();
        await Service(data, executor).Check(client, request, CancellationToken.None);
        Assert.AreEqual(0, executor.WakeCount);
        Assert.AreEqual(1, executor.CheckCount);
        Assert.AreEqual("request_replayed", (await Assert.ThrowsExceptionAsync<GatewayException>(() => Service(data, executor).Check(client, request, CancellationToken.None))).Code);
    }

    [TestMethod]
    public async Task AuthorizationFailureNeverReachesExecutor()
    {
        using var data = new TestStore();
        var client = data.Register().ClientId;
        var executor = new FakeExecutor();
        var service = Service(data, executor);
        await Assert.ThrowsExceptionAsync<GatewayException>(() => service.Wake(client, data.Wake(data.OtherDeviceId), CancellationToken.None));
        data.Store.Revoke(client);
        await Assert.ThrowsExceptionAsync<GatewayException>(() => service.Wake(client, data.Wake(), CancellationToken.None));
        Assert.AreEqual(0, executor.WakeCount);
    }

    [TestMethod]
    public void ResultsAreClientScopedAndExpireAfterRetention()
    {
        using var data = new TestStore();
        var first = data.Register().ClientId;
        var second = data.Register().ClientId;
        var request = data.Wake();
        data.Store.AdmitWake(first, request);
        data.Store.Finish(first, data.DeviceId, request.OperationId, SshExecution.Sent);
        Assert.AreEqual(404, Assert.ThrowsException<GatewayException>(() => data.Store.Operation(second, data.DeviceId, request.OperationId)).StatusCode);
        data.Clock.Advance(TimeSpan.FromHours(24));
        Assert.AreEqual(404, Assert.ThrowsException<GatewayException>(() => data.Store.Operation(first, data.DeviceId, request.OperationId)).StatusCode);
    }
}
