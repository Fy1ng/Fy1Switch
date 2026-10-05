package com.fy1ng.remotedisplayswitch

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.os.SystemClock
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.fy1ng.remotedisplayswitch.core.AppState
import com.fy1ng.remotedisplayswitch.core.PowerIntent
import com.fy1ng.remotedisplayswitch.core.StatePolicy
import com.fy1ng.remotedisplayswitch.core.TilePower
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

class DisplayTileService : TileService() {
    private val app get() = application as RemoteDisplayApplication
    private var listening = false
    private var unlockAttempt = 0
    private var observer: Job? = null

    override fun onStartListening() {
        super.onStartListening()
        listening = true
        observer?.cancel()
        observer = app.scope.launch {
            app.repository.initialize()
            app.repository.state.collect { render(it) }
        }
        app.scope.launch {
            app.repository.initialize()
            app.repository.state.value.selected?.let { app.repository.refresh(it.profile.deviceId, live = true) }
        }
    }

    override fun onStopListening() {
        listening = false
        observer?.cancel()
        observer = null
        super.onStopListening()
    }

    override fun onDestroy() {
        listening = false
        unlockAttempt += 1
        observer?.cancel()
        observer = null
        super.onDestroy()
    }

    override fun onClick() {
        super.onClick()
        if (!listening) return
        val current = app.repository.state.value
        if (current.busy) return
        val deviceId = current.defaultDeviceId
        val attempt = ++unlockAttempt
        val startedAt = SystemClock.elapsedRealtime()
        val initiallyAvailable = StatePolicy.tile(current) != TilePower.Unavailable
        val action = Runnable {
            if (attempt == unlockAttempt && SystemClock.elapsedRealtime() - startedAt < 30_000 && !isLocked) {
                unlockAttempt += 1
                val latest = app.repository.state.value
                if (!initiallyAvailable || latest.defaultDeviceId != deviceId || StatePolicy.tile(latest) == TilePower.Unavailable) {
                    openConfiguration()
                } else if (deviceId != null) {
                    app.scope.launch { app.repository.set(deviceId, PowerIntent.Toggle) }
                }
            }
        }
        if (isLocked) unlockAndRun(action) else action.run()
    }

    private fun render(state: AppState) {
        if (!listening) return
        val tile = qsTile ?: return
        val power = StatePolicy.tile(state)
        tile.state = when (power) {
            TilePower.Active -> Tile.STATE_ACTIVE
            TilePower.Inactive -> Tile.STATE_INACTIVE
            TilePower.Unavailable -> Tile.STATE_UNAVAILABLE
        }
        tile.label = state.selected?.profile?.name ?: getString(R.string.tile_name)
        tile.subtitle = when {
            state.busy -> "正在连接…"
            state.storageFailed -> "请打开应用检查"
            state.selected == null -> "长按配对"
            power == TilePower.Active -> "已确认开启"
            power == TilePower.Inactive -> "已确认关闭"
            else -> "未知 · 展开刷新 / 长按"
        }
        tile.contentDescription = tile.label.toString() + "，" + tile.subtitle
        if (Build.VERSION.SDK_INT >= 30) tile.stateDescription = tile.subtitle
        tile.updateTile()
    }

    private fun configurationIntent(): PendingIntent = PendingIntent.getActivity(
        this, 0, Intent(this, MainActivity::class.java)
            .setAction(Intent.ACTION_MAIN).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    @Suppress("DEPRECATION")
    @SuppressLint("StartActivityAndCollapseDeprecated")
    private fun openConfiguration() {
        if (Build.VERSION.SDK_INT >= 34) startActivityAndCollapse(configurationIntent())
        else startActivityAndCollapse(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}
