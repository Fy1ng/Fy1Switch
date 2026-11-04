using Fy1Switch.Contracts;

namespace Fy1Switch.Relay;

// This is envelope/schema validation, not monitor capability discovery or
// confirmation. Only Windows executes controls and reports its observations.
internal static class RelayDisplayControls
{
    public static bool ValidRequest(RemoteCommand command)
    {
        if (command.Command is "status" or "refresh" or "set" or "operation")
            return command.Control is null && command.Value is null;
        if (command.TargetId is null || command.TargetId == Guid.Empty || command.Intent is not null) return false;
        return command.Command switch
        {
            "controls" => command.Control is null && command.Value is null && command.OperationId is null,
            "set-control" => command.OperationId is { } id && id != Guid.Empty && command.Control is { } control &&
                command.Value is { } value && Enum.IsDefined(control) && ValidValue(control, value),
            "control-operation" => command.OperationId is { } id && id != Guid.Empty && command.Control is null && command.Value is null,
            _ => false,
        };
    }

    public static bool ValidReply(RemoteCommand command, RemoteReply reply)
    {
        var hasNewPayload = reply.Controls is not null || reply.ControlOperation is not null;
        if (hasNewPayload && (reply.Status is not null || reply.Operation is not null || reply.Controls is not null && reply.ControlOperation is not null)) return false;
        if (command.Command is "status" or "refresh" or "set" or "operation") return !hasNewPayload;
        if (reply.Status is not null || reply.Operation is not null) return false;
        if (!reply.Success) return !hasNewPayload && !string.IsNullOrEmpty(reply.ErrorCode);
        if (reply.ErrorCode is not null) return false;
        if (command.Command == "controls")
        {
            if (reply.Controls is not { } controls || reply.ControlOperation is not null || controls.TargetId != command.TargetId ||
                controls.Controls is null || controls.VcpCodes is null || controls.ObservedAt == default || controls.Controls.Count > 6 || controls.VcpCodes.Count > 256)
                return false;
            if (controls.Controls.Any(item => item is null || !Enum.IsDefined(item.Control) || item.SupportedValues is null || item.SupportedValues.Count > 256 ||
                item.Value is { } value && !ValidValue(item.Control, value) || item.SupportedValues.Any(value => !ValidValue(item.Control, value))) ||
                controls.Controls.Select(item => item.Control).Distinct().Count() != controls.Controls.Count) return false;
            return controls.VcpCodes.All(code => code is not null && code.Code is >= 0 and <= 255 && code.Name is { Length: > 0 and <= 256 } &&
                code.SupportedValues is not null && code.SupportedValues.Count <= 256 && code.SupportedValues.All(value => value is >= 0 and <= 65535));
        }
        if (command.Command is not ("set-control" or "control-operation") || reply.Controls is not null || reply.ControlOperation is not { } operation ||
            operation.TargetId != command.TargetId || operation.OperationId != command.OperationId || !Enum.IsDefined(operation.Control) ||
            !Enum.IsDefined(operation.Outcome) || !ValidValue(operation.Control, operation.RequestedValue) || operation.UpdatedAt == default)
            return false;
        if (command.Command == "set-control" && (operation.Control != command.Control || operation.RequestedValue != command.Value)) return false;
        return operation.Result?.Value is not { } readBack || ValidValue(operation.Control, readBack);
    }

    private static bool ValidValue(DisplayControl control, int value) => control switch
    {
        DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume => value is >= 0 and <= 100,
        DisplayControl.Rotation => value is >= 0 and <= 3,
        DisplayControl.InputSource or DisplayControl.ColorTemperature => value is >= 0 and <= 255,
        _ => false,
    };
}
