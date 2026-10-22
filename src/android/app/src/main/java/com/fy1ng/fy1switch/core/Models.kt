package com.fy1ng.fy1switch.core

import java.time.Instant
import java.util.UUID

enum class PowerIntent { On, Off, Toggle }
enum class DisplayPower { Unknown, On, Off }
enum class OperationOutcome { Pending, Confirmed, AcceptedUnconfirmed, Failed, Unknown }

data class Observation(val state: DisplayPower, val rawValue: Int?, val observedAt: Instant, val source: String)
data class RemoteStatus(
    val deviceId: UUID,
    val targetId: UUID?,
    val displayName: String?,
    val observation: Observation,
    val available: Boolean,
    val busy: Boolean,
    val tailscaleEndpoint: String? = null,
)
data class OperationRecord(
    val operationId: UUID,
    val targetId: UUID,
    val requested: PowerIntent,
    val resolved: PowerIntent?,
    val outcome: OperationOutcome,
    val errorCode: String?,
    val observation: Observation?,
    val updatedAt: Instant,
)
data class RemoteCommand(
    val requestId: UUID,
    val deviceId: UUID,
    val command: String,
    val expiresAt: Instant,
    val targetId: UUID? = null,
    val intent: PowerIntent? = null,
    val operationId: UUID? = null,
    val version: Int = 1,
)
data class RemoteReply(
    val requestId: UUID,
    val success: Boolean,
    val errorCode: String?,
    val status: RemoteStatus?,
    val operation: OperationRecord?,
    val version: Int,
)
data class PairBundle(
    val deviceId: UUID,
    val endpoint: String,
    val certificatePin: String,
    val code: String,
    val expiresAt: Instant,
    val tailscaleEndpoint: String? = null,
) {
    override fun toString() = "PairBundle(redacted)"
}
data class PairReply(val deviceId: UUID, val clientId: UUID, val token: String) {
    override fun toString() = "PairReply(redacted)"
}
data class RelayConfig(val endpoint: String, val controllerToken: String) {
    override fun toString() = "RelayConfig(redacted)"
}
data class DeviceProfile(
    val deviceId: UUID,
    val clientId: UUID,
    val name: String,
    val endpoint: String,
    val certificatePin: String,
    val deviceToken: String,
    val relay: RelayConfig? = null,
    val tailscaleEndpoint: String? = null,
    val autoLearnTailscale: Boolean = true,
) {
    override fun toString() = "DeviceProfile(redacted)"
}

enum class Feedback(val text: String) {
    Ready("请连接或刷新显示器状态"),
    Paired("已配对，请刷新状态"),
    Updated("已获取状态"),
    Confirmed("操作已确认"),
    AcceptedUnconfirmed("命令已接受，但显示器状态未确认；请查询结果或实时刷新"),
    Pending("操作尚在进行；请查询结果"),
    Unknown("操作结果未知，可能已执行；请先查询结果，不要重复切换"),
    Failed("操作未成功；请刷新状态后检查 Windows 端"),
    Offline("无法连接；请检查网络或配置 Relay 后刷新"),
    Expired("请求已过期，未重放；请刷新或查询已有操作"),
    Security("安全验证失败；请核对配对证书、地址及手机时间"),
    Unauthorized("授权失效；请重新配对或检查 Relay 凭据"),
    Protocol("响应与当前设备或协议不符；未采用返回状态"),
    InvalidInput("输入无效或配对码已过期，请检查后重试"),
    InvalidConfiguration("设置未能保存，请检查连接设置后重试"),
    DeviceMissing("当前设备记录已变化，请返回主页重新选择电脑"),
    InvalidName("电脑名称应为 1-80 个字符，不能包含换行或控制字符"),
    InvalidRelayEndpoint("Relay 地址无效，请填写 HTTPS 地址，不带路径、查询参数或片段"),
    RelayTokenRequired("首次配置或更换 Relay 地址时，请填写手机使用的控制端凭据"),
    InvalidRelayToken("Relay 控制端凭据包含无效字符，请只粘贴凭据本身，不包含说明文字或换行"),
    RelayDeviceTokenConflict("Relay 控制端凭据不能使用设备配对 token，请填写中继的控制端凭据"),
    InvalidTailscaleEndpoint("Tailscale 地址无效，请填写 HTTPS 地址或留空"),
    Storage("安全存储不可用，已停止发送命令；请重启或清除应用数据后重新配对"),
    Busy("已有请求正在进行，请稍候"),
    Saved("配置已保存"),
    NotFound("未查到操作结果；仍不能据此断定命令没有执行"),
}

data class DeviceState(
    val profile: DeviceProfile,
    val status: RemoteStatus? = null,
    val operation: OperationRecord? = null,
    val connected: Boolean = false,
    val feedback: Feedback = Feedback.Ready,
    val requiresRefresh: Boolean = false,
    val lastSuccessfulRoute: Route? = null,
    // Process-local phone time. Never restore freshness from a saved snapshot or host timestamp.
    val lastLiveReadAtMillis: Long? = null,
)
data class AppState(
    val devices: List<DeviceState> = emptyList(),
    val defaultDeviceId: UUID? = null,
    val busy: Boolean = false,
    val loaded: Boolean = false,
    val storageFailed: Boolean = false,
    val notice: Feedback? = null,
    val request: RepositoryRequest? = null,
) {
    val selected: DeviceState? get() = devices.find { it.profile.deviceId == defaultDeviceId }
}

enum class RepositoryAction { Other, Refresh, Set }
data class RepositoryRequest(
    val id: UUID,
    val action: RepositoryAction,
    val deviceId: UUID?,
    val result: Feedback? = null,
)

enum class TilePower { Active, Inactive, Refreshing, Unavailable }

object StatePolicy {
    fun unresolved(operation: OperationRecord?) = operation?.outcome in setOf(
        OperationOutcome.Pending, OperationOutcome.Unknown, OperationOutcome.AcceptedUnconfirmed,
    )

    fun confirmedPower(device: DeviceState?): DisplayPower {
        if (device == null || !device.connected) return DisplayPower.Unknown
        val status = device.status ?: return DisplayPower.Unknown
        if (status.deviceId != device.profile.deviceId || !status.available || status.targetId == null || status.busy) return DisplayPower.Unknown
        if (device.requiresRefresh) return DisplayPower.Unknown
        return status.observation.state
    }

    fun refreshing(state: AppState): Boolean = state.loaded && !state.storageFailed && state.busy &&
        state.selected != null && state.request?.action == RepositoryAction.Refresh &&
        state.request.deviceId == state.defaultDeviceId

    fun tile(state: AppState): TilePower = when {
        !state.loaded || state.storageFailed -> TilePower.Unavailable
        refreshing(state) -> TilePower.Refreshing
        state.busy -> TilePower.Unavailable
        confirmedPower(state.selected) == DisplayPower.On -> TilePower.Active
        confirmedPower(state.selected) == DisplayPower.Off -> TilePower.Inactive
        else -> TilePower.Unavailable
    }

    // Unknown power is actionable only as a read. It must never queue a later toggle.
    fun tileRefreshOnly(state: AppState): Boolean = state.loaded && !state.storageFailed &&
        state.selected != null && (!state.busy || refreshing(state)) &&
        confirmedPower(state.selected) == DisplayPower.Unknown

    fun canSet(state: AppState, intent: PowerIntent? = null): Boolean {
        val device = state.selected ?: return false
        return state.loaded && !state.busy && !state.storageFailed && device.connected &&
            device.status?.available == true && device.status.deviceId == device.profile.deviceId &&
            device.status.targetId != null && !device.status.busy &&
            !device.requiresRefresh && (!unresolved(device.operation) || confirmedPower(device) != DisplayPower.Unknown) &&
            (intent != PowerIntent.Toggle || confirmedPower(device) != DisplayPower.Unknown)
    }
}
