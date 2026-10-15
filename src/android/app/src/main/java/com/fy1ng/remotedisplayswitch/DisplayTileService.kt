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
import com.fy1ng.remotedisplayswitch.core.RepositoryAction
import com.fy1ng.remotedisplayswitch.core.StatePolicy
import com.fy1ng.remotedisplayswitch.core.TileClickCoordinator
import com.fy1ng.remotedisplayswitch.core.TilePower
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.yield

class DisplayTileService : TileService() {
    private val app get() = application as RemoteDisplayApplication
    private var listening = false
    private var unlockAttempt = 0
    private var observer: Job? = null
    private val clicks = TileClickCoordinator()

    override fun onStartListening() {
        super.onStartListening()
        listening = true
        unlockAttempt += 1
        clicks.startListening()
        observer?.cancel()
        observer = app.scope.launch {
            app.repository.initialize()
            app.repository.state.collect {
                deliverQueuedClick()
                render(app.repository.state.value)
            }
        }
        app.scope.launch {
            app.repository.initialize()
            app.repository.state.value.selected?.let { app.repository.refresh(it.profile.deviceId, live = true) }
        }
    }

    override fun onStopListening() {
        listening = false
        clicks.stopListening()
        observer?.cancel()
        observer = null
        super.onStopListening()
    }

    override fun onDestroy() {
        listening = false
        clicks.stopListening()
        unlockAttempt += 1
        observer?.cancel()
        observer = null
        super.onDestroy()
    }

    override fun onClick() {
        super.onClick()
        if (!listening) return
        val current = app.repository.state.value
        val refreshing = StatePolicy.refreshing(current)
        if (current.busy && !refreshing) return
        val deviceId = current.defaultDeviceId
        val targetId = current.selected?.status?.targetId
        val requestId = current.request?.id
        val attempt = ++unlockAttempt
        val startedAt = SystemClock.elapsedRealtime()
        val queued = if (refreshing) clicks.queue(current, startedAt) else null
        val initiallyAvailable = StatePolicy.tile(current) in setOf(TilePower.Active, TilePower.Inactive)
        val action = Runnable {
            // Unlock may collapse the shade before this callback; only refresh queues require listening.
            if ((!refreshing || listening) && attempt == unlockAttempt && SystemClock.elapsedRealtime() - startedAt < 30_000 && !isLocked) {
                unlockAttempt += 1
                val latest = app.repository.state.value
                if (refreshing) {
                    if (queued != null && clicks.authorize(queued)) deliverQueuedClick()
                    else if (listening) openConfiguration()
                } else if (!initiallyAvailable || latest.defaultDeviceId != deviceId || latest.selected?.status?.targetId != targetId ||
                    latest.request?.id != requestId || !StatePolicy.canSet(latest, PowerIntent.Toggle)) {
                    openConfiguration()
                } else if (deviceId != null) {
                    app.scope.launch { app.repository.set(deviceId, PowerIntent.Toggle) }
                }
            }
        }
        render(current)
        if (isLocked) unlockAndRun(action) else action.run()
    }

    private fun deliverQueuedClick() {
        if (!clicks.hasPending) return
        app.scope.launch {
            yield()
            val deviceId = clicks.takeReady(app.repository.state.value, SystemClock.elapsedRealtime(), isLocked)
            if (deviceId != null) app.repository.set(deviceId, PowerIntent.Toggle)
            render(app.repository.state.value)
        }
    }

    private fun render(state: AppState) {
        if (!listening) return
        val tile = qsTile ?: return
        val power = StatePolicy.tile(state)
        tile.state = when (power) {
            TilePower.Active -> Tile.STATE_ACTIVE
            TilePower.Inactive -> Tile.STATE_INACTIVE
            TilePower.Refreshing -> Tile.STATE_INACTIVE
            TilePower.Unavailable -> Tile.STATE_UNAVAILABLE
        }
        tile.label = state.selected?.profile?.name ?: getString(R.string.tile_name)
        tile.subtitle = when {
            power == TilePower.Refreshing -> if (clicks.hasPending) "等待刷新后切换" else "正在刷新, 可点击等待"
            state.busy && state.request?.action == RepositoryAction.Set -> "正在切换..."
            state.busy -> "正在处理..."
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
