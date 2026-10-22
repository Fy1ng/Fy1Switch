package com.fy1ng.fy1switch.background

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class BackgroundSyncState(val enabled: Boolean = false, val running: Boolean = false, val error: String? = null)

class BackgroundSyncManager(context: Context) {
    private val context = context.applicationContext
    private val preferences = this.context.getSharedPreferences("background_display_sync", Context.MODE_PRIVATE)
    private val mutableState = MutableStateFlow(BackgroundSyncState(enabled = preferences.getBoolean("enabled", false)))
    val state: StateFlow<BackgroundSyncState> = mutableState.asStateFlow()

    fun setEnabled(enabled: Boolean): Boolean {
        if (!enabled) {
            val saved = preferences.edit().putBoolean("enabled", false).commit()
            mutableState.value = BackgroundSyncState(error = if (saved) null else "未能保存设置，后台同步已停止。")
            context.stopService(Intent(context, BackgroundDisplaySyncService::class.java))
            return saved
        }
        notificationProblem()?.let { reportFailure(it); return false }
        if (!preferences.edit().putBoolean("enabled", true).commit()) {
            reportFailure("未能保存设置，请稍后重试。")
            return false
        }
        mutableState.value = state.value.copy(enabled = true, error = null)
        return startIfNeeded()
    }

    // Called by the visible Activity only; a saved preference does not grant permission
    // to start a foreground service from an arbitrary background callback.
    fun resumeIfEnabled() {
        if (!state.value.enabled) return
        notificationProblem()?.let { reportFailure(it); return }
        startIfNeeded()
    }

    private fun startIfNeeded(): Boolean {
        if (state.value.running) return true
        return try {
            context.startForegroundService(Intent(context, BackgroundDisplaySyncService::class.java))
            true
        } catch (_: RuntimeException) {
            reportFailure("系统未允许启动后台同步，请保持应用在前台后重新开启。")
            false
        }
    }

    internal fun reportRunning(running: Boolean) {
        mutableState.value = state.value.copy(running = running)
    }

    internal fun reportFailure(message: String) {
        preferences.edit().putBoolean("enabled", false).commit()
        mutableState.value = BackgroundSyncState(error = message)
        context.stopService(Intent(context, BackgroundDisplaySyncService::class.java))
    }

    internal fun notificationProblem(): String? {
        if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            return "请允许通知权限后再开启后台同步。"
        val notifications = context.getSystemService(NotificationManager::class.java)
            ?: return "系统通知服务不可用，请稍后重试。"
        if (!notifications.areNotificationsEnabled()) return "应用通知已关闭，请在系统设置中允许通知。"
        if (notifications.getNotificationChannel(CHANNEL_ID)?.importance == NotificationManager.IMPORTANCE_NONE)
            return "后台同步通知类别已关闭，请在系统通知设置中开启。"
        return null
    }

    internal fun createNotificationChannel() {
        context.getSystemService(NotificationManager::class.java)?.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "显示器后台同步", NotificationManager.IMPORTANCE_LOW).apply {
                description = "后台读取已配对电脑的显示器状态，可随时停止"
                setShowBadge(false)
            },
        )
    }

    internal companion object { const val CHANNEL_ID = "background_display_sync" }
}
