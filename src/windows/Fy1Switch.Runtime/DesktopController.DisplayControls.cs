using Fy1Switch.Contracts;

namespace Fy1Switch.Runtime;

public sealed partial class DesktopController
{
    public async Task<DisplayControlsSnapshot> GetDisplayControlsAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        if (targetId == Guid.Empty) throw new ControlException("invalid_request");
        await _commands.WaitAsync(cancellationToken);
        Publish(_snapshot with { Busy = true });
        try
        {
            var binding = RequireBinding(targetId);
            var generation = Interlocked.Read(ref _topologyGeneration);
            var settingsGeneration = Interlocked.Read(ref _displaySettingsGeneration);
            var reply = await _worker.SendAsync(new(Guid.NewGuid(), "read-controls", binding.TargetKey, binding.TopologyFingerprint), cancellationToken);
            if (_invalidated || generation != Interlocked.Read(ref _topologyGeneration)) throw new ControlException("topology_changed");
            if (settingsGeneration != Interlocked.Read(ref _displaySettingsGeneration)) throw new ControlException("display_settings_changed");
            if (!reply.Success) throw new ControlException(reply.ErrorCode ?? "controls_read_failed");
            if (reply.Controls is not { } controls || controls.TargetKey != binding.TargetKey ||
                controls.Controls is null || controls.VcpCodes is null ||
                controls.Controls.Any(item => item is null || !Enum.IsDefined(item.Control) || item.SupportedValues is null) ||
                controls.Controls.Select(item => item.Control).Distinct().Count() != controls.Controls.Count)
                throw new ControlException("worker_protocol_error");
            return controls;
        }
        catch (ControlException exception) { HandleDisplayControlError(exception.Code); throw; }
        finally { Publish(_snapshot with { Busy = false }); _commands.Release(); }
    }

    public async Task<DisplayControlWriteResult> SetDisplayControlAsync(Guid targetId, DisplayControl control, int value, CancellationToken cancellationToken = default)
    {
        if (targetId == Guid.Empty || !Enum.IsDefined(control) || !ValidControlValue(control, value))
            throw new ControlException("invalid_control_value");
        await _commands.WaitAsync(cancellationToken);
        var issued = false;
        Publish(_snapshot with { Busy = true });
        try
        {
            var binding = RequireBinding(targetId);
            var generation = Interlocked.Read(ref _topologyGeneration);
            issued = true;
            var reply = await _worker.SendAsync(new(Guid.NewGuid(), "set-control", binding.TargetKey, binding.TopologyFingerprint,
                Control: control, Value: value), cancellationToken);
            var result = reply.ControlWrite ?? new DisplayControlWriteResult(reply.WriteAccepted, null, false,
                reply.ErrorCode ?? "worker_protocol_error");
            if (_invalidated || generation != Interlocked.Read(ref _topologyGeneration))
            {
                HandleDisplayControlError("topology_changed");
                return result with { Confirmed = false, Value = null, ErrorCode = "topology_changed" };
            }
            // The Worker confirms continuous values in the monitor's raw range.
            // A 0-50 device may read back 34% after a requested 33% due to quantization.
            var continuous = control is DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume;
            if (result.Confirmed && (!reply.Success || result.WriteAccepted != true || result.Value is null ||
                !ValidControlValue(control, result.Value.Value) || !continuous && result.Value != value ||
                result.ErrorCode is not null || reply.ErrorCode is not null))
                return new(result.WriteAccepted, result.Value, false, "worker_protocol_error");
            if (reply.ErrorCode is not null && result.ErrorCode is null) result = result with { ErrorCode = reply.ErrorCode, Confirmed = false };
            HandleDisplayControlError(result.ErrorCode);
            return result;
        }
        catch (Exception exception) when (issued)
        {
            var code = exception is ControlException controlException ? controlException.Code
                : exception is OperationCanceledException ? "request_cancelled" : "worker_unavailable";
            HandleDisplayControlError(code);
            // A lost reply cannot prove whether the native write ran. Never replay it.
            return new(null, null, false, code);
        }
        finally { Publish(_snapshot with { Busy = false }); _commands.Release(); }
    }

    private static bool ValidControlValue(DisplayControl control, int value) => control switch
    {
        DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume => value is >= 0 and <= 100,
        DisplayControl.Rotation => value is >= 0 and <= 3,
        _ => value is >= 0 and <= 255,
    };

    private void HandleDisplayControlError(string? code)
    {
        if (code is "topology_changed" or "target_not_found" or "target_mismatch" or "target_ambiguous" or
            "profile_unsupported" or "unsupported_profile" or "unsupported_topology" or "target_not_unique_or_not_ddc")
            HandleError(code);
    }

    public async Task<MonitorPreferences> GetMonitorPreferencesAsync(string targetKey, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        try { RequirePreferencesTarget(targetKey); return _store.LoadMonitorPreferences(targetKey); }
        finally { _commands.Release(); }
    }

    public async Task<MonitorPreferences> SetMonitorPreferencesAsync(string targetKey, MonitorPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _commands.WaitAsync(cancellationToken);
        try
        {
            RequirePreferencesTarget(targetKey);
            _store.SaveMonitorPreferences(targetKey, preferences);
            Publish(_snapshot);
            return preferences;
        }
        finally { _commands.Release(); }
    }

    private void RequirePreferencesTarget(string targetKey)
    {
        if (_snapshot.Binding is not { } binding) throw new ControlException("not_bound");
        if (string.IsNullOrWhiteSpace(targetKey) || targetKey != binding.TargetKey) throw new ControlException("target_changed");
    }
}
