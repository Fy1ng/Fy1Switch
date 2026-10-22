package com.fy1ng.fy1switch

import android.Manifest
import android.app.NotificationManager
import android.app.StatusBarManager
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.drawable.Icon
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.fy1ng.fy1switch.ui.Fy1SwitchScreen

class MainActivity : ComponentActivity() {
    private val app get() = application as Fy1SwitchApplication
    private var tileMessage by mutableStateOf<String?>(null)
    private var showTileGuide by mutableStateOf(false)
    private var tileRequestPending = false
    private var backgroundSyncMessage by mutableStateOf<String?>(null)
    private var backgroundSyncPermissionPending by mutableStateOf(false)
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val requested = backgroundSyncPermissionPending
        backgroundSyncPermissionPending = false
        if (requested) {
            if (granted) enableBackgroundSync() else {
                app.backgroundSync.setEnabled(false)
                backgroundSyncMessage = "未获得通知权限, 后台同步未开启. 请在系统通知设置中允许通知后重试."
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        showTileGuide = savedInstanceState?.getBoolean("tileGuide") ?: false
        backgroundSyncPermissionPending = savedInstanceState?.getBoolean("backgroundSyncPermissionPending") ?: false
        setContent {
            Fy1SwitchScreen(app.repository, app.scope, onAddTile = ::addTile,
                tileMessage = tileMessage, showTileGuide = showTileGuide,
                onDismissTileGuide = { showTileGuide = false }, onPaired = ::offerTile,
                onTileMessageConsumed = { if (tileMessage == it) tileMessage = null },
                backgroundSync = app.backgroundSync,
                backgroundSyncMessage = backgroundSyncMessage,
                backgroundSyncPermissionPending = backgroundSyncPermissionPending,
                onBackgroundSyncEnabled = ::setBackgroundSyncEnabled,
                onOpenNotificationSettings = ::openNotificationSettings,
                onPairingVisibilityChanged = { pairing ->
                    if (pairing) window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
                    else window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
                })
        }
    }

    override fun onResume() {
        super.onResume()
        if (app.backgroundSync.state.value.enabled && !notificationsEnabled()) {
            app.backgroundSync.setEnabled(false)
            backgroundSyncMessage = "系统已关闭通知, 后台同步已停用. 允许通知后可重新开启."
        } else app.backgroundSync.resumeIfEnabled()
    }

    private fun notificationsEnabled(): Boolean =
        (Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) &&
            getSystemService(NotificationManager::class.java)?.areNotificationsEnabled() == true

    private fun setBackgroundSyncEnabled(enabled: Boolean) {
        backgroundSyncMessage = null
        if (!enabled) {
            backgroundSyncPermissionPending = false
            app.backgroundSync.setEnabled(false)
        } else if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            if (!backgroundSyncPermissionPending) {
                backgroundSyncPermissionPending = true
                notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        } else enableBackgroundSync()
    }

    private fun enableBackgroundSync() {
        if (!notificationsEnabled()) {
            app.backgroundSync.setEnabled(false)
            backgroundSyncMessage = "系统通知已关闭, 后台同步未开启. 请先允许本应用显示通知."
        } else if (!app.backgroundSync.setEnabled(true)) {
            backgroundSyncMessage = app.backgroundSync.state.value.error ?: "后台服务未能启动, 请稍后重试."
        } else backgroundSyncMessage = null
    }

    private fun openNotificationSettings() {
        startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, packageName))
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean("tileGuide", showTileGuide)
        outState.putBoolean("backgroundSyncPermissionPending", backgroundSyncPermissionPending)
        super.onSaveInstanceState(outState)
    }

    private fun offerTile() {
        val preferences = getSharedPreferences("ui_preferences", MODE_PRIVATE)
        if (!preferences.getBoolean("tile_guide_seen", false)) {
            preferences.edit().putBoolean("tile_guide_seen", true).apply()
            showTileGuide = true
        }
    }

    private fun addTile() {
        if (tileRequestPending) return
        if (Build.VERSION.SDK_INT < 33) {
            tileMessage = "请展开系统快捷设置，点编辑，将“远程显示器”拖入开关区域。"
            return
        }
        tileRequestPending = true
        tileMessage = "请在系统提示中添加快捷开关。"
        try {
            val manager = getSystemService(StatusBarManager::class.java)
                ?: throw IllegalStateException()
            manager.requestAddTileService(
                ComponentName(this, DisplayTileService::class.java), getString(R.string.tile_name),
                Icon.createWithResource(this, R.drawable.ic_display), mainExecutor,
            ) { result ->
                tileRequestPending = false
                tileMessage = when (result) {
                    StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ADDED -> "快捷开关已添加。"
                    StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ALREADY_ADDED -> "快捷开关已经添加，无需重复操作。"
                    StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_NOT_ADDED -> "已取消或未添加；可稍后从设置重试。"
                    else -> "系统暂时无法添加，请在快捷设置的编辑页面手动添加。"
                }
            }
        } catch (_: RuntimeException) {
            tileRequestPending = false
            tileMessage = "系统不支持此次添加请求，请在快捷设置的编辑页面手动添加。"
        }
    }
}
