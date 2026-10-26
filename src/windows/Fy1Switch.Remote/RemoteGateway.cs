using Fy1Switch.Contracts;
using Fy1Switch.Runtime;

namespace Fy1Switch.Remote;

public sealed class RemoteGateway(IDesktopController controller, DeviceSecurity security, Guid deviceId, Func<DateTimeOffset>? clock = null)
{
    private readonly SemaphoreSlim _slots = new(8, 8);
    private readonly SemaphoreSlim _relayAdmissions = new(1, 1);
    public Guid DeviceId => deviceId;
    public string? TailscaleEndpoint { get; set; }
    public string? PublicEndpoint { get; set; }
    public string? RelayEndpoint { get; set; }
    public Func<Guid, CancellationToken, Task<RelayControllerReply>>? IssueRelayController { get; set; }
    public PairReply? Pair(PairRequest request) => security.CompletePairing(deviceId, request);
    public RelayBootstrapReply? PairRelay(PairRequest request)
    {
        if (RelayEndpoint is null || PublicEndpoint is null) return null;
        var reply = security.CompleteRelayPairing(deviceId, request);
        return reply is null ? null : new(deviceId, reply.ClientId, reply.Token, PublicEndpoint, security.CertificatePin, TailscaleEndpoint, reply.DeviceName);
    }
    public async Task<RelayBootstrapReply?> PairRelayAsync(PairRequest request, CancellationToken cancellationToken = default)
    {
        await _relayAdmissions.WaitAsync(cancellationToken);
        try { return PairRelay(request); }
        finally { _relayAdmissions.Release(); }
    }
    public RelayBootstrapReply? AuthorizeRelay(string? token, RelayAuthorizeRequest request)
        => request.ClientId != Guid.Empty && security.AuthenticateClient(token)?.ClientId == request.ClientId
            ? new(deviceId, request.ClientId) : null;
    public async Task<(int StatusCode, RelayConfigurationReply? Reply)> RelayConfigurationAsync(string? token, RelayAuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        if (AuthorizeRelay(token, request) is null) return (401, null);
        var endpoint = RelayEndpoint;
        var issue = IssueRelayController;
        if (endpoint is null || issue is null) return (503, null);
        await _relayAdmissions.WaitAsync(cancellationToken);
        try
        {
            if (AuthorizeRelay(token, request) is null) return (401, null);
            var controller = await issue(request.ClientId, cancellationToken);
            if (AuthorizeRelay(token, request) is null) return (401, null);
            if (endpoint != RelayEndpoint) return (503, null);
            if (controller.DeviceId != deviceId || controller.ClientId != request.ClientId) return (502, null);
            return (200, new(deviceId, request.ClientId, endpoint, controller.ControllerToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (504, null); }
        catch (RelayApiException) { return (503, null); }
        catch (HttpRequestException) { return (503, null); }
        catch (InvalidDataException) { return (502, null); }
        finally { _relayAdmissions.Release(); }
    }
    public async Task<(int StatusCode, RemoteReply Reply)> ControlAsync(string? token, RemoteCommand request, CancellationToken cancellationToken = default)
    {
        if (!security.Authenticate(token)) return (401, new(request.RequestId, false, "unauthorized"));
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        if (request.Version != 1 || request.RequestId == Guid.Empty || request.DeviceId != deviceId) return (400, new(request.RequestId, false, "invalid_request"));
        if (request.ExpiresAt <= now || request.ExpiresAt > now.AddSeconds(120)) return (400, new(request.RequestId, false, "request_expired_or_invalid_deadline"));
        if (request.Command is not ("status" or "refresh" or "set" or "operation")) return (400, new(request.RequestId, false, "invalid_command"));
        if (!await _slots.WaitAsync(0, cancellationToken)) return (429, new(request.RequestId, false, "busy"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.ExpiresAt - now);
        try
        {
            if (request.Command == "set")
            {
                if (request.TargetId is null || request.TargetId == Guid.Empty || request.Intent is null || request.OperationId is null || request.OperationId == Guid.Empty || !Enum.IsDefined(request.Intent.Value)) return (400, new(request.RequestId, false, "invalid_request"));
                var result = await controller.ExecuteAsync(request.TargetId.Value, request.Intent.Value, request.OperationId.Value, deadline.Token);
                return (200, new(request.RequestId, true, Operation: result));
            }
            if (request.Command == "operation")
            {
                if (request.OperationId is null || request.OperationId == Guid.Empty) return (400, new(request.RequestId, false, "invalid_request"));
                var result = await controller.GetOperationAsync(request.OperationId.Value, deadline.Token);
                return result is null ? (404, new(request.RequestId, false, "operation_not_found")) : (200, new(request.RequestId, true, Operation: result));
            }
            var snapshot = request.Command == "refresh" ? await controller.RefreshAsync(deadline.Token) : await controller.GetSnapshotAsync(deadline.Token);
            var available = snapshot.Binding is not null && !snapshot.Binding.NeedsConfirmation && snapshot.ErrorCode is null;
            return (200, new(request.RequestId, true, Status: new(snapshot.DeviceId, snapshot.Binding?.TargetId, snapshot.Binding?.Name, snapshot.Observation, available, snapshot.Busy, TailscaleEndpoint, RelayEndpoint)));
        }
        catch (ControlException exception) { return (409, new(request.RequestId, false, exception.Code)); }
        catch (OperationCanceledException) { return (504, new(request.RequestId, false, "deadline_exceeded_result_unknown")); }
        catch { return (500, new(request.RequestId, false, "internal_error")); }
        finally { _slots.Release(); }
    }
}
