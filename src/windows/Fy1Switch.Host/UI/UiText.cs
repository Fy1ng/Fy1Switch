using System;
using Fy1Switch.Contracts;

namespace Fy1Switch.Host.UI;

internal static class UiText
{
    internal static bool CanAutomaticallyRefresh(HostSnapshot? snapshot) =>
        snapshot is { Busy: false, Binding.NeedsConfirmation: false } &&
        snapshot.ErrorCode is null or "state_unknown" or "read_failed" or "readback_failed" or
            "power_io_failed" or "topology_unavailable" or "worker_timeout" or "worker_unavailable" or
            "worker_disconnected" or "worker_start_failed";

    internal static bool IsTrusted(PowerObservation observation, bool simulation) =>
        observation.ObservedAt != default && (simulation
            ? observation.Source is "simulation" or "simulated"
            : observation.Source is "hardware_read" or "hardware" or "hardware_readback" or "hardware-readback" or "ddc" or "ddc-ci" or "ddc_readback");

    internal static string BriefError(string code) => code switch
    {
        "topology_changed" => "连接已变化，请重新确认绑定。",
        "topology_unavailable" => "暂时无法检查显示器连接，请稍后刷新。",
        "busy" or "controller_busy" or "operation_in_progress" => "已有操作进行中，请稍候。",
        "state_unknown" or "read_failed" or "readback_failed" => "状态尚不能确认，请先刷新。",
        "not_bound" or "binding_required" or "no_binding" => "请先在设置中绑定显示器。",
        "startup_owned_by_another_install" => "自动启动已指向另一份程序，请确认要使用的版本。",
        "startup_requires_executable" => "请使用完整程序目录中的 EXE 设置自动启动。",
        "startup_registration_changed" => "自动启动设置已被更改，请重新操作并确认。",
        "startup_failed" or "startup_registration_failed" => "无法保存自动启动设置，请检查系统权限或安全软件。",
        _ => "操作未完成，请查看诊断记录。"
    };

    internal static string Error(string? code) => code switch
    {
        "worker_unavailable" or "worker_start_failed" or "worker_disconnected" => "显示器控制进程暂时不可用。请稍后重试；若持续出现，请从托盘退出后重新启动程序。",
        "worker_missing" => "缺少显示器控制组件。请使用包含控制组件的完整程序目录启动。",
        "worker_protocol_error" => "主程序与显示器控制组件通信异常。请使用同一版本的完整程序目录，再退出并重启。",
        "timeout" or "worker_timeout" => "等待显示器响应超时，结果尚不能确认。请先刷新状态，避免连续重复开关。",
        "topology_changed" => "显示器连接或排列已变化。为避免控制错误的设备，请重新发现、选择并绑定目标。",
        "topology_unavailable" => "暂时无法检查显示器连接。请稍后刷新状态；连接检查恢复后可继续使用原绑定。",
        "other_controller_running" or "display_process_conflict" or "worker_already_running" => "另一个控制程序正在使用显示器。请先关闭其他控制实例，再重试。",
        "busy" or "controller_busy" or "operation_in_progress" => "已有操作正在进行，请等待完成后再试。",
        "not_bound" or "binding_required" or "no_binding" => "尚未绑定显示器。请先发现设备，明确选择候选并绑定。",
        "confirmation_required" or "needs_confirmation" => "当前绑定需要重新确认。请重新发现并选择目标后绑定。",
        "target_not_found" or "monitor_not_found" => "找不到所选显示器。请检查连接后重新发现，不要继续使用旧候选。",
        "target_mismatch" or "target_changed" or "stale_target" or "invalid_target" => "所选目标已失效或与当前绑定不同。请查看最新绑定并重新选择。",
        "state_unknown" => "显示器状态待确认。请先刷新状态，或选择开启、关闭。",
        "unsupported_profile" or "unsupported_monitor" or "unsupported_topology" or "target_not_unique_or_not_ddc" => "该显示器配置不在当前验证范围内。本版本仅面向已验证的 G4Q + MTT VDD 配置。",
        "discovery_restart_required" => "当前控制进程需要重新初始化。请点击重新扫描；程序不会自动重复硬件探测。",
        "operation_id_conflict" => "操作标识已被另一条命令使用，本次操作未执行。",
        "read_failed" or "readback_failed" => "未能读回显示器状态。当前结果未知，请检查连接后手动刷新。",
        "write_failed" => "控制请求未成功完成，不能确认显示器状态。请先刷新状态后再决定是否重试。",
        "startup_failed" or "startup_registration_failed" => "无法保存登录自动启动设置。请检查系统权限或安全软件，当前设置保持为实际保存的值。",
        "startup_owned_by_another_install" => "登录自动启动当前指向另一份 Fy1Switch。开启时可确认改用当前版本；取消会保留原设置，关闭也不会删除其他版本的启动项。",
        "startup_requires_executable" => "当前程序通过 dotnet 启动。请使用自包含发布目录中的 Fy1Switch.Host.exe 设置登录自动启动。",
        "startup_registration_changed" => "确认期间自动启动项发生变化，本次未替换。请重新查看当前设置后再操作。",
        "operation_cancelled" or "request_cancelled" => "操作已取消，显示器状态尚不能确认。请先刷新状态再重试。",
        _ => "操作未能完成，当前结果尚不能确认。请检查设备连接后重试；若持续失败，请查看详细信息。"
    };

    internal static string Intent(PowerIntent intent) => intent switch
    {
        PowerIntent.On => "开启",
        PowerIntent.Off => "关闭",
        _ => "切换"
    };

    internal static string Power(DisplayPower state) => state switch
    {
        DisplayPower.On => "开启",
        DisplayPower.Off => "关闭",
        _ => "未知"
    };

    internal static string Source(PowerObservation observation, bool simulation)
    {
        if (simulation || string.Equals(observation.Source, "simulation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(observation.Source, "simulated", StringComparison.OrdinalIgnoreCase))
            return "模拟（非实体硬件）";
        return observation.Source?.ToLowerInvariant() switch
        {
            "hardware_read" or "hardware" or "hardware_readback" or "hardware-readback" or "ddc" or "ddc-ci" or "ddc_readback" => "硬件读回",
            _ => "未知（未获得可信读回）"
        };
    }

    internal static string Time(DateTimeOffset time, DateTimeOffset? now = null)
    {
        if (time == default) return "尚无记录";
        var elapsed = (now ?? DateTimeOffset.UtcNow) - time;
        if (elapsed.TotalMinutes < 1) return "刚刚";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} 分钟前";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} 小时前";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} 天前";
        return time.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    }

    internal static string Operation(OperationRecord operation, bool simulation)
    {
        var prefix = simulation ? "模拟操作：" : string.Empty;
        return prefix + (operation.Outcome switch
        {
            OperationOutcome.Confirmed => simulation
                ? "模拟状态已确认；不代表实体显示器发生变化。"
                : "本次操作已由控制层确认。",
            OperationOutcome.AcceptedUnconfirmed => "请求已接受，结果尚未确认。请先刷新，勿连续开关。",
            OperationOutcome.Pending => "请求仍在处理中，尚未确认。请勿重复操作。",
            OperationOutcome.Failed => "操作失败，请查看诊断记录。",
            _ => "操作结果未知。请先刷新状态，勿连续开关。"
        });
    }
}
