using System;
using Fy1Switch.Remote;

namespace Fy1Switch.Host.UI;

internal enum RelaySetupState { Disabled, NotRegistered, AddressChanged, Registering, RegistrationIncomplete, Saved, Connecting, Connected, Disconnected, Unavailable, Unauthorized, Conflict }

internal sealed record RelayRegistrationPresentation(RelaySetupState State, string Title, string Detail, bool HasCredential)
{
    internal bool Connected => State == RelaySetupState.Connected;
    internal bool Warning => State is RelaySetupState.RegistrationIncomplete or RelaySetupState.Unavailable or RelaySetupState.Unauthorized or RelaySetupState.Conflict;

    internal static bool SameEndpoint(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var first = RemoteSettings.RequireUrl(left.Trim(), "https");
            var second = RemoteSettings.RequireUrl(right.Trim(), "https");
            return Uri.Compare(first, second, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;
        }
        catch (ArgumentException) { return false; }
    }

    internal static RelayRegistrationPresentation Create(RemoteSettings saved, string draftEndpoint, bool enabled,
        bool hostRunning, string connection, bool registering = false, bool registrationIncomplete = false, bool justRegistered = false)
    {
        var credential = !string.IsNullOrWhiteSpace(saved.ProtectedRelayToken) && SameEndpoint(saved.RelayUrl, draftEndpoint);
        RelayRegistrationPresentation Result(RelaySetupState state, string title, string detail) => new(state, title, detail, credential);
        if (!enabled) return Result(RelaySetupState.Disabled, "中继未启用", "已保存的授权会保留, 重新启用后尝试连接.");
        if (registering) return Result(RelaySetupState.Registering, "正在注册到中继...", "正在验证接入码并保存授权, 请勿重复提交.");
        if (registrationIncomplete) return Result(RelaySetupState.RegistrationIncomplete, "本次注册未完成",
            credential ? "请核对连接和中继管理页. 已有授权时可取消重新注册并查看当前连接, 无需重复提交接入码."
                : "请检查中继管理页和网络后使用新的接入码. 请求中断时, 中继可能已经接纳注册.");
        if (!credential)
            return !string.IsNullOrWhiteSpace(saved.ProtectedRelayToken) && !SameEndpoint(saved.RelayUrl, draftEndpoint)
                ? Result(RelaySetupState.AddressChanged, "新中继尚未注册", "当前填写的地址与已保存授权不同. 新地址需要独立注册, 原连接在有效设置保存前保持运行.")
                : Result(RelaySetupState.NotRegistered, "尚未注册到中继", "填写中继 HTTPS 地址和管理页面生成的六位接入码, 然后点击注册.");
        if (!saved.Enabled || !saved.UseRelay || !hostRunning)
            return Result(RelaySetupState.Saved, "已保存中继授权", "连接尚未运行. 请确认中继已启用且连接设置保存成功.");
        return connection switch
        {
            "connected" => Result(RelaySetupState.Connected, "已注册到中继, 已连接", "中继连接已建立. 已配对手机可获取中继配置, 也可以生成手机中继配对码."),
            "connecting" => Result(RelaySetupState.Connecting, justRegistered ? "已注册到中继, 正在连接..." : "已保存授权, 正在连接...", "正在建立中继连接. 保存了凭据不代表当前已经在线."),
            "unauthorized" => Result(RelaySetupState.Unauthorized, "中继授权被拒绝", "本机仍保留凭据, 但中继拒绝了本次连接. 请检查管理页中的设备授权, 必要时重新注册."),
            "conflict" => Result(RelaySetupState.Conflict, "中继设备已有连接", "可能有另一份 Windows Host 正在连接此设备. 请检查运行实例, 无需重复注册."),
            "unavailable" => Result(RelaySetupState.Unavailable, "中继暂时无法连接", "已保存授权, 正在等待自动重试. 请检查网络、HTTPS 地址和中继服务."),
            _ => Result(RelaySetupState.Disconnected, justRegistered ? "已注册到中继, 等待连接" : "中继连接已断开", "已保存授权, 将自动重连. 断线不代表需要重新注册.")
        };
    }
}
