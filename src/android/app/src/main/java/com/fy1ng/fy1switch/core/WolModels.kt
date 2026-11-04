package com.fy1ng.fy1switch.core

import java.time.Instant
import java.util.UUID

data class WolTarget(val deviceId: UUID, val name: String)
data class WolAuthorization(val endpoint: String, val clientId: UUID, val accessToken: String, val devices: List<WolTarget>) {
    override fun toString() = "WolAuthorization(redacted)"
}
data class WolBinding(val endpoint: String, val targetId: UUID, val enabled: Boolean = false)
enum class WolStatus { Pending, PacketSent, Failed, Unknown }
data class WolOperation(
    val operationId: UUID, val deviceId: UUID, val status: WolStatus,
    val issuedAt: Instant, val expiresAt: Instant, val updatedAt: Instant,
    val errorCode: String? = null,
)
data class WolUiState(val busy: Boolean = false, val deviceId: UUID? = null, val message: String? = null, val errorCode: String? = null)

object WolPolicy {
    fun target(state: AppState): DeviceState? = (state.wolTileDeviceId ?: state.defaultDeviceId)?.let { id ->
        state.devices.find { it.profile.deviceId == id }
    }
    fun authorization(state: AppState, binding: WolBinding?): WolAuthorization? = binding?.let { value ->
        state.wolAuthorizations.find { Protocol.sameEndpoint(it.endpoint, value.endpoint) }
            ?.takeIf { it.devices.any { target -> target.deviceId == value.targetId } }
    }
    fun configured(state: AppState, device: DeviceState?): Boolean = state.loaded && !state.storageFailed &&
        device?.profile?.wol?.enabled == true && authorization(state, device.profile.wol) != null

    fun result(operation: WolOperation?, hostFresh: Boolean, now: Instant): String = when {
        hostFresh -> "Windows Host 已上线. 显示器状态由原连接单独确认."
        operation == null -> "远程唤醒不会确认电脑是否关机."
        operation.status == WolStatus.Pending -> "正在发送唤醒请求"
        operation.status == WolStatus.PacketSent && now < operation.issuedAt.plusSeconds(90) -> "唤醒包已发送, 等待 Windows Host 上线"
        operation.status == WolStatus.PacketSent -> "唤醒包已发送, 尚未确认 Windows Host 上线. 可稍后刷新连接."
        operation.status == WolStatus.Unknown -> "发送结果未知, 请查询原请求. 不会自动重新发送唤醒包." +
            (operation.errorCode?.let { " " + error(it) } ?: "")
        else -> error(operation.errorCode)
    }
    fun error(code: String?): String = when (code) {
        "unauthorized", "registration_rejected" -> "授权无效或注册码已失效, 请检查后重新授权."
        "device_forbidden" -> "本手机未获此电脑的唤醒权限."
        "rate_limited", "device_cooldown", "device_busy" -> "请求过于频繁或电脑正在处理唤醒, 请稍后重试."
        "tunnel_unavailable", "ssh_unreachable" -> "无法通过 WireGuard 连接 OpenWrt, 请检查隧道和路由器."
        "ssh_auth_failed" -> "OpenWrt SSH 身份验证失败, 请检查 Gateway 密钥配置."
        "ssh_host_key_failed" -> "OpenWrt 主机密钥验证失败, 已停止连接."
        "ssh_failed", "remote_configuration_invalid" -> "OpenWrt 唤醒命令执行失败, 请检查服务端配置."
        "ssh_configuration_invalid", "unsupported_platform" -> "Gateway 的 SSH 执行配置不可用, 请检查 Linux 服务配置."
        "invalid_ssh_response" -> "OpenWrt 返回了无法确认的执行结果, 请检查受限脚本."
        "gateway_restarted" -> "Gateway 执行期间重启, 无法确认唤醒包是否已发送."
        "client_capacity", "operation_capacity", "replay_capacity" -> "Gateway 已达到请求或授权容量限制, 请稍后重试或检查服务配置."
        "execution_timeout" -> "OpenWrt 执行超时, 发送结果可能无法确认."
        "request_expired" -> "请求已过期, 请检查手机时间."
        "operation_not_found" -> "未找到原请求, 不能据此确认唤醒包未发送."
        "operation_conflict", "request_replayed" -> "请求标识冲突或重复, 已停止发送."
        "security" -> "Gateway HTTPS 证书验证失败."
        "offline" -> "无法连接 WOL Gateway, 请检查网络和服务地址."
        "storage" -> "安全存储不可用, 唤醒已停止."
        "storage_unavailable" -> "Gateway 无法保存执行结果, 发送结果未知. 请查询原唤醒请求."
        "not_configured" -> "请先在设备设置中配置远程开机."
        "busy" -> "已有远程开机请求正在处理."
        "invalid_request" -> "WOL 配置或输入无效, 请检查服务地址和注册码."
        else -> "WOL 服务返回异常, 请检查配置或服务日志."
    }
}

/** A captured unlock intent may never change its target or authorization. */
class WakeTileClick(val deviceId: UUID, val binding: WolBinding, val clientId: UUID, val startedAt: Long) {
    fun valid(state: AppState, now: Long): Boolean {
        val device = WolPolicy.target(state)
        return now - startedAt in 0 until 30_000 && WolPolicy.configured(state, device) &&
            device?.profile?.deviceId == deviceId && device.profile.wol == binding &&
            WolPolicy.authorization(state, binding)?.clientId == clientId
    }
}
