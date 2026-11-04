namespace Fy1Switch.WolGateway;

public sealed class WakeService(GatewayStore store, GatewaySettings settings, IWolExecutor executor, TimeProvider time, ILogger<WakeService> logger)
{
    private readonly SemaphoreSlim slots = new(4, 4);
    public async Task<WakeOperation> Wake(Guid client, WakeRequest request, CancellationToken stopping)
    {
        if (!await slots.WaitAsync(0, stopping)) throw new GatewayException(429, "rate_limited");
        try
        {
            var admission = store.AdmitWake(client, request);
            if (!admission.Execute) return admission.Operation;
            SshExecution result;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            var remaining = request.ExpiresAt - time.GetUtcNow();
            deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try
            {
                // Recheck immediately before starting a side effect, not just before reserving it.
                store.Authorize(client, request.DeviceId);
                if (deadline.IsCancellationRequested) result = new("failed", "request_expired");
                else result = await executor.ExecuteAsync(settings.Devices.Single(d => d.DeviceId == request.DeviceId), true, deadline.Token);
            }
            catch (GatewayException error) { result = new("failed", error.Code); }
            catch (OperationCanceledException) { result = new("unknown", "execution_timeout"); }
            catch { result = new("unknown", "ssh_failed"); }
            if (result.Status is not ("packet_sent" or "failed" or "unknown")) result = new("unknown", "invalid_ssh_response");
            var operation = store.Finish(client, request.DeviceId, request.OperationId, result);
            logger.LogInformation("Wake operation {OperationId}: {Status}, {ErrorCode}", request.OperationId, operation.Status, operation.ErrorCode);
            return operation;
        }
        finally { slots.Release(); }
    }

    public async Task Check(Guid client, CheckRequest request, CancellationToken cancellationToken)
    {
        if (!await slots.WaitAsync(0, cancellationToken)) throw new GatewayException(429, "rate_limited");
        try
        {
            store.AdmitCheck(client, request);
            store.Authorize(client, request.DeviceId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = request.ExpiresAt - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new GatewayException(400, "request_expired");
            deadline.CancelAfter(remaining);
            SshExecution result;
            try { result = await executor.ExecuteAsync(settings.Devices.Single(d => d.DeviceId == request.DeviceId), false, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new GatewayException(504, "execution_timeout"); }
            catch (OperationCanceledException) { throw; }
            catch { throw new GatewayException(502, "ssh_failed"); }
            if (result.Status == "ready") return;
            throw new GatewayException(result.ErrorCode switch
            {
                "execution_timeout" => 504,
                "tunnel_unavailable" or "ssh_unreachable" => 503,
                _ => 502,
            }, result.ErrorCode ?? "ssh_failed");
        }
        finally { slots.Release(); }
    }
}
