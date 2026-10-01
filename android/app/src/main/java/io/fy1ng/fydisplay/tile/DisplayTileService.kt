package io.fy1ng.fydisplay.tile

import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import io.fy1ng.fydisplay.network.DisplayResult
import io.fy1ng.fydisplay.network.RemoteClient
import java.util.concurrent.Executors

class DisplayTileService : TileService() {
    private val executor = Executors.newSingleThreadExecutor()

    override fun onStartListening() {
        super.onStartListening()
        refresh()
    }

    override fun onClick() {
        super.onClick()
        val tile = qsTile ?: return
        tile.state = Tile.STATE_UNAVAILABLE
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) tile.subtitle = "正在切换…"
        tile.updateTile()

        executor.execute {
            updateTile(RemoteClient(applicationContext).toggle())
        }
    }

    override fun onDestroy() {
        executor.shutdownNow()
        super.onDestroy()
    }

    private fun refresh() {
        executor.execute {
            updateTile(RemoteClient(applicationContext).status())
        }
    }

    private fun updateTile(result: DisplayResult) {
        val tile = qsTile ?: return
        if (!result.ok) {
            tile.state = Tile.STATE_UNAVAILABLE
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) tile.subtitle = "PC 离线"
        } else if (result.displayState.equals("on", ignoreCase = true)) {
            tile.state = Tile.STATE_ACTIVE
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) tile.subtitle = if (result.transport == "relay") "已开启 · 中继" else "已开启"
        } else {
            tile.state = Tile.STATE_INACTIVE
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) tile.subtitle = if (result.transport == "relay") "已关闭 · 中继" else "已关闭"
        }
        tile.label = "显示器"
        tile.updateTile()
    }
}
