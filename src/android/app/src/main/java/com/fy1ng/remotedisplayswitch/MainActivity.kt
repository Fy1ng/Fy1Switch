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
import com.fy1ng.remotedisplayswitch.ui.RemoteDisplayScreen

class MainActivity : ComponentActivity() {
    private val app get() = application as RemoteDisplayApplication

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        enableEdgeToEdge()
        setContent {
            RemoteDisplayScreen(app.repository, app.scope, onAddTile = if (Build.VERSION.SDK_INT >= 33) ::addTile else null)
        }
    }

    private fun addTile() {
        if (Build.VERSION.SDK_INT >= 33) {
            getSystemService(StatusBarManager::class.java).requestAddTileService(
                ComponentName(this, DisplayTileService::class.java), getString(R.string.tile_name),
                Icon.createWithResource(this, R.drawable.ic_display), mainExecutor,
            ) { }
        }
    }
}
