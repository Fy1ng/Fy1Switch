package com.fy1ng.remotedisplayswitch

import android.app.StatusBarManager
import android.content.ComponentName
import android.graphics.drawable.Icon
import android.os.Build
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.fy1ng.remotedisplayswitch.ui.RemoteDisplayScreen

class MainActivity : ComponentActivity() {
    private val app get() = application as RemoteDisplayApplication
    private var tileMessage by mutableStateOf<String?>(null)
    private var showTileGuide by mutableStateOf(false)
    private var tileRequestPending = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        enableEdgeToEdge()
        showTileGuide = savedInstanceState?.getBoolean("tileGuide") ?: false
        tileMessage = savedInstanceState?.getString("tileMessage")
        setContent {
            RemoteDisplayScreen(app.repository, app.scope, onAddTile = ::addTile,
                tileMessage = tileMessage, showTileGuide = showTileGuide,
                onDismissTileGuide = { showTileGuide = false }, onPaired = ::offerTile)
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean("tileGuide", showTileGuide)
        outState.putString("tileMessage", tileMessage)
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
