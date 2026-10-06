package com.fy1ng.remotedisplayswitch.ui

import com.fy1ng.remotedisplayswitch.core.AppState
import com.fy1ng.remotedisplayswitch.core.DisplayPower
import com.fy1ng.remotedisplayswitch.core.PowerIntent
import com.fy1ng.remotedisplayswitch.core.StatePolicy

data class HomeAction(val label: String, val intent: PowerIntent?, val enabled: Boolean)

object HomePolicy {
    fun action(state: AppState): HomeAction {
        val intent = when (StatePolicy.confirmedPower(state.selected)) {
            DisplayPower.On -> PowerIntent.Off
            DisplayPower.Off -> PowerIntent.On
            DisplayPower.Unknown -> null
        }
        val enabled = state.loaded && !state.busy && !state.storageFailed && state.selected != null &&
            (intent == null || StatePolicy.canSet(state))
        return HomeAction(if (state.busy) "正在处理…" else when (intent) {
            PowerIntent.On -> "开启显示器"
            PowerIntent.Off -> "关闭显示器"
            else -> "刷新连接"
        }, intent, enabled)
    }

    fun status(state: AppState): String {
        val device = state.selected ?: return "尚未配对"
        if (StatePolicy.unresolved(device.operation) && device.requiresRefresh) return "结果待确认"
        if (!device.connected) return "离线 · 状态未知"
        if (device.status?.busy == true) return "主机忙碌 · 状态未知"
        return when (StatePolicy.confirmedPower(device)) {
            DisplayPower.On -> "显示器已开启"
            DisplayPower.Off -> "显示器已关闭"
            DisplayPower.Unknown -> "显示器状态未知"
        }
    }
}
