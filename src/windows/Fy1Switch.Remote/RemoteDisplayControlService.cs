using Fy1Switch.Contracts;
using Fy1Switch.Runtime;
using Microsoft.Data.Sqlite;

namespace Fy1Switch.Remote;

/// <summary>Authenticated, durable remote parameter writes. Separate from power intents and the local WPF write path.</summary>
public sealed class RemoteDisplayControlService
{
    private readonly IDesktopController _controller;
    private readonly DeviceSecurity _security;
    private readonly Guid _deviceId;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly RemoteDisplayControlJournal _journal;

    public RemoteDisplayControlService(IDesktopController controller, DeviceSecurity security, Guid deviceId,
        Func<DateTimeOffset>? clock = null, int journalCapacity = 100_000)
    {
        _controller = controller;
        _security = security;
        _deviceId = deviceId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _journal = new(controller.DataDirectory, journalCapacity);
    }

    public static bool IsRequestValid(RemoteCommand request) => request.TargetId is { } target && target != Guid.Empty
        && request.Intent is null && request.Command switch
        {
            "controls" => request.OperationId is null && request.Control is null && request.Value is null,
            "control-operation" => request.OperationId is { } id && id != Guid.Empty && request.Control is null && request.Value is null,
            "set-control" => request.OperationId is { } id && id != Guid.Empty && request.Control is { } control
                && Enum.IsDefined(control) && request.Value is { } value && ValidValue(control, value),
            _ => false
        };

    public static bool ValidValue(DisplayControl control, int value) => control switch
    {
        DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume => value is >= 0 and <= 100,
        DisplayControl.Rotation => value is >= 0 and <= 3,
        DisplayControl.InputSource or DisplayControl.ColorTemperature => value is >= 0 and <= 255,
        _ => false
    };

    public async Task<(int StatusCode, RemoteReply Reply)> HandleAsync(string? token, RemoteCommand request, CancellationToken cancellationToken)
    {
        (int, RemoteReply) Error(int status, string code) => (status, new(request.RequestId, false, code));
        if (!_security.Authenticate(token)) return Error(401, "unauthorized");
        if (request.Version != 1 || request.RequestId == Guid.Empty || request.DeviceId != _deviceId || !IsRequestValid(request))
            return Error(400, "invalid_request");
        var now = _clock();
        if (request.ExpiresAt <= now || request.ExpiresAt > now.AddSeconds(120)) return Error(400, "request_expired_or_invalid_deadline");
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (!_security.Authenticate(token)) return Error(401, "unauthorized");
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ExpiresAt <= _clock()) return Error(400, "request_expired_or_invalid_deadline");
            var target = request.TargetId!.Value;
            if (request.Command == "controls")
            {
                var controls = await ReadAsync(target, cancellationToken);
                if (!_security.Authenticate(token)) return Error(401, "unauthorized");
                return (200, new(request.RequestId, true, Controls: controls));
            }

            using var connection = _journal.Open(_clock());
            var previous = _journal.Find(connection, request.OperationId!.Value);
            if (previous is not null && (previous.TargetId != target || request.Command == "set-control"
                && (previous.Control != request.Control || previous.RequestedValue != request.Value)))
                return Error(409, "operation_conflict");
            if (previous?.Outcome == OperationOutcome.Pending)
            {
                // The serial queue proves no earlier request is still executing. A leftover Pending means
                // final persistence failed in this process; never leave it looking active or dispatch it again.
                previous = previous with { Outcome = OperationOutcome.Unknown, ErrorCode = "result_persistence_failed", Result = null, UpdatedAt = _clock() };
                _journal.Save(connection, previous);
            }
            // A query reads history even if the monitor has since been unbound. It never touches the Worker.
            if (previous is not null) return !_security.Authenticate(token) ? Error(401, "unauthorized")
                : (200, new(request.RequestId, true, ControlOperation: previous));
            if (request.Command == "control-operation") return Error(404, "operation_not_found");
            if (!_journal.HasCapacity(connection)) return Error(503, "operation_journal_full");

            var capability = await ReadAsync(target, cancellationToken);
            var control = request.Control!.Value;
            var value = request.Value!.Value;
            var supported = capability.Controls.SingleOrDefault(item => item.Control == control);
            if (supported is not { Supported: true }) return Error(409, "control_unsupported");
            if (control is not (DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume)
                && !supported.SupportedValues.Contains(value)) return Error(409, "invalid_control_value");
            if (!_security.Authenticate(token)) return Error(401, "unauthorized");
            cancellationToken.ThrowIfCancellationRequested();

            var operation = new DisplayControlOperation(request.OperationId.Value, target, control, value,
                OperationOutcome.Pending, null, null, _clock());
            _journal.Save(connection, operation); // Durable acceptance always precedes a native write.
            var dispatched = false;
            try
            {
                await RequireBindingAsync(target, cancellationToken);
                if (!_security.Authenticate(token))
                {
                    operation = operation with { Outcome = OperationOutcome.Failed, ErrorCode = "unauthorized", UpdatedAt = _clock() };
                    _journal.Save(connection, operation);
                    return Error(401, "unauthorized");
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (request.ExpiresAt <= _clock()) throw new OperationCanceledException();
                dispatched = true;
                var result = await _controller.SetDisplayControlAsync(target, control, value, cancellationToken);
                result = ValidateResult(control, value, result);
                var outcome = result.WriteAccepted switch
                {
                    null => OperationOutcome.Unknown,
                    false => OperationOutcome.Failed,
                    true when result.Confirmed => OperationOutcome.Confirmed,
                    _ => OperationOutcome.AcceptedUnconfirmed
                };
                operation = operation with { Outcome = outcome, ErrorCode = result.ErrorCode, Result = result, UpdatedAt = _clock() };
            }
            catch (Exception exception)
            {
                // Runtime throws these binding/value errors before Worker dispatch; post-dispatch failures return a result.
                var rejectedBeforeWrite = exception is ControlException { Code: "not_bound" or "target_changed" or "topology_changed" or "invalid_control_value" };
                var unknown = dispatched && !rejectedBeforeWrite;
                var code = exception is ControlException error ? error.Code : exception is OperationCanceledException
                    ? unknown ? "deadline_exceeded_result_unknown" : "request_cancelled_before_dispatch" : "control_write_failed";
                operation = operation with { Outcome = unknown ? OperationOutcome.Unknown : OperationOutcome.Failed, ErrorCode = code, UpdatedAt = _clock() };
            }
            // Ignore request cancellation when persisting the final state. Losing this write leaves Pending, recovered as Unknown.
            _journal.Save(connection, operation);
            if (!_security.Authenticate(token)) return Error(401, "unauthorized");
            return (200, new(request.RequestId, true, ControlOperation: operation));
        }
        catch (SqliteException) { return Error(503, "operation_journal_unavailable"); }
        catch (IOException) { return Error(503, "operation_journal_unavailable"); }
        finally { _serial.Release(); }
    }

    private async Task<RemoteDisplayControls> ReadAsync(Guid target, CancellationToken cancellationToken)
    {
        var binding = await RequireBindingAsync(target, cancellationToken);
        var result = await _controller.GetDisplayControlsAsync(target, cancellationToken);
        if (result.TargetKey != binding.TargetKey || result.Controls.Count > 6 || result.VcpCodes.Count > 256
            || result.Controls.Any(item => !Enum.IsDefined(item.Control) || item.SupportedValues is null
                || item.Value is { } value && !ValidValue(item.Control, value)
                || item.SupportedValues.Any(value => !ValidValue(item.Control, value)))
            || result.Controls.Select(item => item.Control).Distinct().Count() != result.Controls.Count
            || result.VcpCodes.Any(item => item.Code is < 0 or > 255 || item.Name is null || item.Name.Length > 256
                || item.SupportedValues is null || item.SupportedValues.Any(value => value is < 0 or > 65535)))
            throw new ControlException("worker_protocol_error");
        await RequireBindingAsync(target, cancellationToken);
        return new(target, result.Controls, result.VcpCodes, result.ObservedAt);
    }

    private async Task<MonitorBinding> RequireBindingAsync(Guid target, CancellationToken cancellationToken)
    {
        var snapshot = await _controller.GetSnapshotAsync(cancellationToken);
        if (snapshot.DeviceId != _deviceId) throw new ControlException("device_mismatch");
        var binding = snapshot.Binding ?? throw new ControlException("not_bound");
        if (binding.TargetId != target) throw new ControlException("target_changed");
        if (binding.NeedsConfirmation) throw new ControlException("topology_changed");
        return binding;
    }

    private static DisplayControlWriteResult ValidateResult(DisplayControl control, int requested, DisplayControlWriteResult result)
    {
        var continuous = control is DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume;
        if (result.Value is { } actual && !ValidValue(control, actual)
            || result.Confirmed && (result.WriteAccepted != true || result.Value is null || result.ErrorCode is not null
                || !continuous && result.Value != requested))
            return new(null, null, false, "worker_protocol_error");
        return result;
    }
}
