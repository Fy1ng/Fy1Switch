package com.fy1ng.remotedisplayswitch.ui

import com.fy1ng.remotedisplayswitch.core.AppState
import com.fy1ng.remotedisplayswitch.core.DisplayPower
import com.fy1ng.remotedisplayswitch.core.PowerIntent
import com.fy1ng.remotedisplayswitch.core.RepositoryAction
import com.fy1ng.remotedisplayswitch.core.StatePolicy

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
            query && state.request?.action == RepositoryAction.Other -> "正在查询"
            state.request?.action == RepositoryAction.Set -> "正在切换"
            else -> "正在连接"
        } else if (query) "查询操作结果" else when (intent) {
            PowerIntent.On -> "开启显示器"
            PowerIntent.Off -> "关闭显示器"
            else -> if (state.selected?.connected == false) "重新连接" else "刷新状态"
        }
        return HomeAction(label, intent, enabled, query)
    }

    fun status(state: AppState): String {
        val device = state.selected ?: return "尚未配对"
        if (state.busy && state.request?.action == RepositoryAction.Set) return "正在切换显示器"
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
