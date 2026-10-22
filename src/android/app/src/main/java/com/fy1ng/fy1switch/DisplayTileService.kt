package com.fy1ng.fy1switch

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import android.os.SystemClock
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.fy1ng.fy1switch.core.AppState
import com.fy1ng.fy1switch.core.PowerIntent
import com.fy1ng.fy1switch.core.RepositoryAction
import com.fy1ng.fy1switch.core.StatePolicy
import com.fy1ng.fy1switch.core.TileClickCoordinator
import com.fy1ng.fy1switch.core.TilePower
import com.fy1ng.fy1switch.ui.HomePolicy
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.yield

class DisplayTileService : TileService() {
    private val app get() = application as Fy1SwitchApplication
    private var listening = false
    private var unlockAttempt = 0
    private var observer: Job? = null
    private var recovery: Job? = null
    private var freshnessObserver: Job? = null
    private var expiredRecoveryRequested = true
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
                render(currentState())
            }
        }
        recovery?.cancel()
        recovery = app.scope.launch {
            app.repository.initialize()
            val state = app.repository.state.value
            state.defaultDeviceId?.let { deviceId ->
                if (!HomePolicy.canReuseBackgroundPower(state, app.backgroundSync.state.value.running,
                        app.repository.hasFreshPower(deviceId))) app.repository.recover(deviceId)
            }
        }
        expiredRecoveryRequested = true
        freshnessObserver?.cancel()
        freshnessObserver = app.scope.launch {
            var previousFresh: Boolean? = null
            while (listening) {
                val state = app.repository.state.value
                val deviceId = state.defaultDeviceId
                val fresh = deviceId?.let { app.repository.hasFreshPower(it) } == true
                if (fresh) expiredRecoveryRequested = false
                else if (!expiredRecoveryRequested && !state.busy && deviceId != null) {
                    expiredRecoveryRequested = true
                    recovery?.cancel()
                    recovery = app.scope.launch { app.repository.recover(deviceId) }
                }
                if (fresh != previousFresh) {
                    previousFresh = fresh
                    deliverQueuedClick()
                    render(currentState())
                }
                delay(1_000)
            }
        }
    }

    override fun onStopListening() {
        listening = false
        clicks.stopListening()
        observer?.cancel()
        observer = null
        recovery?.cancel()
        recovery = null
        freshnessObserver?.cancel()
        freshnessObserver = null
        super.onStopListening()
    }

    override fun onDestroy() {
        listening = false
        clicks.stopListening()
        unlockAttempt += 1
        observer?.cancel()
        observer = null
        recovery?.cancel()
        recovery = null
        freshnessObserver?.cancel()
        freshnessObserver = null
        super.onDestroy()
    }

    override fun onClick() {
        super.onClick()
        if (!listening) return
        val current = currentState()
        val refreshing = StatePolicy.refreshing(current)
        val refreshOnly = StatePolicy.tileRefreshOnly(current)
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
                val latest = currentState()
                if (refreshOnly) {
                    // The click was made while power was unknown. A later successful read cannot turn it into a write.
                    if (latest.loaded && !latest.storageFailed && latest.defaultDeviceId == deviceId &&
                        latest.request?.id == requestId && deviceId != null) {
                        if (!latest.busy) app.scope.launch { app.repository.refresh(deviceId, live = true) }
                    } else openConfiguration()
                } else if (refreshing) {
                    if (queued != null && clicks.authorize(queued)) deliverQueuedClick()
                    else if (listening) openConfiguration()
                } else if (!initiallyAvailable || latest.defaultDeviceId != deviceId || latest.selected?.status?.targetId != targetId ||
                    latest.request?.id != requestId || !StatePolicy.canSet(latest, PowerIntent.Toggle)) {
                    openConfiguration()
                } else if (deviceId != null) {
                    app.scope.launch {
                        if (app.repository.hasFreshPower(deviceId)) app.repository.set(deviceId, PowerIntent.Toggle)
                        else app.repository.refresh(deviceId, live = true)
                    }
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
            val deviceId = clicks.takeReady(currentState(), SystemClock.elapsedRealtime(), isLocked)
            if (deviceId != null) app.repository.set(deviceId, PowerIntent.Toggle)
            render(currentState())
        }
    }

    private fun currentState(): AppState {
        val state = app.repository.state.value
        return HomePolicy.visibleState(state, state.defaultDeviceId?.let { app.repository.hasFreshPower(it) } == true)
    }

    private fun render(state: AppState) {
        if (!listening) return
        val tile = qsTile ?: return
        val power = StatePolicy.tile(state)
        tile.state = when (power) {
            TilePower.Active -> Tile.STATE_ACTIVE
            TilePower.Inactive -> Tile.STATE_INACTIVE
            TilePower.Refreshing -> Tile.STATE_INACTIVE
            TilePower.Unavailable -> if (StatePolicy.tileRefreshOnly(state)) Tile.STATE_INACTIVE else Tile.STATE_UNAVAILABLE
        }
        tile.icon = Icon.createWithResource(this, when (power) {
            TilePower.Active -> R.drawable.ic_display_on
            TilePower.Inactive -> R.drawable.ic_display_off
            else -> R.drawable.ic_display_refresh
        })
        tile.label = state.selected?.profile?.name ?: getString(R.string.tile_name)
        tile.subtitle = when {
            power == TilePower.Refreshing -> if (clicks.hasPending) "等待刷新后切换" else "正在刷新"
            state.busy && state.request?.action == RepositoryAction.Set -> "正在切换…"
            state.busy -> "正在处理…"
            state.storageFailed -> "请打开应用检查"
            state.selected == null -> "长按配对"
            power == TilePower.Active -> "显示器已开启"
            power == TilePower.Inactive -> "显示器已关闭"
            else -> "点按刷新"
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
