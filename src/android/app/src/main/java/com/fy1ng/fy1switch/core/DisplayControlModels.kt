package com.fy1ng.fy1switch.core

import java.time.Instant
import java.util.UUID

enum class DisplayControl { Brightness, Contrast, Volume, InputSource, ColorTemperature, Rotation }
data class DisplayControlState(val control: DisplayControl, val supported: Boolean, val value: Int?, val supportedValues: List<Int>)
data class VcpDescriptor(val code: Int, val name: String, val supportedValues: List<Int>)
data class DisplayControls(val targetId: UUID, val controls: List<DisplayControlState>, val vcpCodes: List<VcpDescriptor>, val observedAt: Instant)
data class ControlWriteResult(val writeAccepted: Boolean?, val value: Int?, val confirmed: Boolean, val errorCode: String?)
data class ControlOperation(
    val operationId: UUID, val targetId: UUID, val control: DisplayControl, val requestedValue: Int,
    val outcome: OperationOutcome, val errorCode: String?, val result: ControlWriteResult?, val updatedAt: Instant,
)
enum class ControlsFeedback(val text: String) {
    Ready("展开后读取这台显示器的控制能力"),
    Updated("已读取显示器参数"),
    Unsupported("当前 Windows Host 或 Relay 不支持显示参数控制, 请升级后重试. 原显示器开关仍可使用."),
    Unavailable("请先连接电脑并确认显示器绑定"),
    TargetChanged("显示器目标已变化, 未应用参数. 请刷新后重新操作"),
    InvalidValue("该参数或数值不在显示器支持范围内, 请刷新参数后重试"),
    Pending("参数操作正在处理, 请查询原请求"),
    Confirmed("已通过读回确认参数"),
    Unconfirmed("写入已接受, 但参数尚未确认. 请查询结果或刷新读回"),
    Unknown("参数结果未知, 可能已执行. 请查询原请求, 不会自动重新应用"),
    Failed("参数操作未成功, 请刷新参数后检查"),
    NotFound("未找到原参数操作, 不能据此断定没有执行. 可刷新当前参数"),
    Security("参数请求的身份或协议验证失败, 已停止应用"),
    Offline("无法读取参数, 请检查连接后重试"),
    Busy("已有请求正在处理, 请稍后重试"),
}

object DisplayControlPolicy {
    val commands = setOf("controls", "set-control", "control-operation")
    fun continuous(control: DisplayControl) = control in setOf(DisplayControl.Brightness, DisplayControl.Contrast, DisplayControl.Volume)
    fun legal(control: DisplayControl, value: Int) = when {
        continuous(control) -> value in 0..100
        control == DisplayControl.Rotation -> value in 0..3
        else -> value in 0..255
    }
    fun unresolved(operation: ControlOperation?) = operation?.outcome in setOf(OperationOutcome.Pending, OperationOutcome.Unknown, OperationOutcome.AcceptedUnconfirmed)
    fun bound(device: DeviceState): Boolean = device.connected && device.status?.deviceId == device.profile.deviceId &&
        device.status.available && !device.status.busy && device.status.targetId != null
    fun canApply(state: AppState, device: DeviceState, control: DisplayControl, value: Int, fresh: Boolean): Boolean {
        val capability = device.controls?.controls?.find { it.control == control } ?: return false
        return state.loaded && !state.storageFailed && !state.busy && state.defaultDeviceId == device.profile.deviceId &&
            bound(device) && device.controls.targetId == device.status?.targetId && !device.controlsRequireRefresh &&
            fresh && capability.supported && legal(control, value) && (continuous(control) || value in capability.supportedValues)
    }
    fun label(control: DisplayControl) = when (control) {
        DisplayControl.Brightness -> "亮度"
        DisplayControl.Contrast -> "对比度"
        DisplayControl.Volume -> "音量"
        DisplayControl.InputSource -> "输入源"
        DisplayControl.ColorTemperature -> "色温"
        DisplayControl.Rotation -> "旋转"
    }
    fun valueLabel(control: DisplayControl, value: Int): String = when (control) {
        DisplayControl.Rotation -> when (value) { 0 -> "0 deg (横向)"; 1 -> "90 deg (纵向)"; 2 -> "180 deg (横向翻转)"; 3 -> "270 deg (纵向翻转)"; else -> "未知旋转值 $value" }
        DisplayControl.ColorTemperature -> when (value) { 1 -> "sRGB"; 2 -> "显示器原生色温"; 4 -> "5000K"; 5 -> "6500K"; 6 -> "7500K"; 8 -> "9300K"; 11 -> "用户预设 1"; else -> "色温预设 0x%02X".format(value) }
        DisplayControl.InputSource -> when (value) { 1 -> "VGA-1"; 3 -> "DVI-1"; 4 -> "DVI-2"; 15 -> "DisplayPort-1"; 16 -> "DisplayPort-2"; 17 -> "HDMI-1"; 18 -> "HDMI-2"; else -> "输入源 0x%02X".format(value) }
        else -> "$value%"
    }
}
