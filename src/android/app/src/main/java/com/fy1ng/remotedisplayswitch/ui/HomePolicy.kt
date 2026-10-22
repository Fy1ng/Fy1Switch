package com.fy1ng.remotedisplayswitch.ui

import com.fy1ng.remotedisplayswitch.core.AppState
import com.fy1ng.remotedisplayswitch.core.DisplayPower
import com.fy1ng.remotedisplayswitch.core.DeviceState
import com.fy1ng.remotedisplayswitch.core.Feedback
import com.fy1ng.remotedisplayswitch.core.PowerIntent
import com.fy1ng.remotedisplayswitch.core.RepositoryAction
import com.fy1ng.remotedisplayswitch.core.StatePolicy
import java.util.UUID

data class HomeAction(val label: String, val intent: PowerIntent?, val enabled: Boolean, val queryOperation: Boolean = false)

object HomePolicy {
    fun action(state: AppState): HomeAction {
        val query = state.selected?.let { it.requiresRefresh && StatePolicy.unresolved(it.operation) } == true
        val intent = if (query) null else when (StatePolicy.confirmedPower(state.selected)) {
            DisplayPower.On -> PowerIntent.Off
            DisplayPower.Off -> PowerIntent.On
            DisplayPower.Unknown -> null
        }
        val enabled = state.loaded && !state.busy && !state.storageFailed && state.selected != null &&
            (intent == null || StatePolicy.canSet(state, intent))
        val label = if (state.busy) when {
            StatePolicy.refreshing(state) -> "正在刷新"
            query && state.request?.action == RepositoryAction.Other -> "正在查询"
            state.request?.action == RepositoryAction.Set -> "正在切换"
            else -> "正在连接"
        } else if (query) "确认上次结果" else when (intent) {
            PowerIntent.On -> "开启显示器"
            PowerIntent.Off -> "关闭显示器"
            else -> if (state.selected?.connected == false) "重新连接" else "刷新状态"
        }
        return HomeAction(label, intent, enabled, query)
    }

    fun status(state: AppState): String {
        val device = state.selected ?: return "尚未配对"
        if (state.busy && state.request?.action == RepositoryAction.Set) return "正在切换显示器"
        if (StatePolicy.refreshing(state)) return "正在刷新显示器状态"
        if (state.busy && state.request?.action == RepositoryAction.Other &&
            device.requiresRefresh && StatePolicy.unresolved(device.operation)) return "正在确认显示器状态"
        if (StatePolicy.unresolved(device.operation) && device.requiresRefresh) return "显示器状态待确认"
        if (!device.connected || device.status?.busy == true) return "显示器状态待确认"
        return when (StatePolicy.confirmedPower(device)) {
            DisplayPower.On -> "显示器已开启"
            DisplayPower.Off -> "显示器已关闭"
            DisplayPower.Unknown -> "显示器状态待确认"
        }
    }

    fun completedUncertainSet(state: AppState, deviceId: UUID): UUID? {
        if (state.busy || state.defaultDeviceId != deviceId || state.selected?.requiresRefresh != true ||
            !StatePolicy.unresolved(state.selected?.operation)) return null
        return state.request?.takeIf { it.action == RepositoryAction.Set && it.deviceId == deviceId }?.id
    }

    fun connectionStatus(device: DeviceState): String = when {
        device.connected -> "最近连接成功"
        device.feedback == Feedback.Offline -> "最近连接失败"
        else -> "连接状态待确认"
    }
}
