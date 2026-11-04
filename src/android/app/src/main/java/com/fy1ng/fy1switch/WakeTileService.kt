package com.fy1ng.fy1switch

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import android.os.SystemClock
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.fy1ng.fy1switch.core.*
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.util.UUID

class WakeTileService : TileService() {
    private val app get() = application as Fy1SwitchApplication
    private var listening = false
    private var generation = 0
    private var observer: Job? = null
    private var watching: Job? = null
    private var watched: Pair<UUID, UUID>? = null

    override fun onStartListening() {
        super.onStartListening()
        listening = true
        generation += 1
        observer?.cancel()
        observer = app.scope.launch {
            app.repository.initialize()
            while (listening) {
                render()
                val state = app.repository.state.value
                val target = WolPolicy.target(state)
                val operation = target?.wolOperation?.takeIf { it.status != WolStatus.Pending }
                val key = if (operation != null) target.profile.deviceId to operation.operationId else null
                if (key != watched) {
                    watching?.cancel()
                    watched = key
                    if (key != null) watching = app.scope.launch {
                        app.repository.wol.observeHost(key.first, key.second) {
                            listening && WolPolicy.target(app.repository.state.value)?.profile?.deviceId == key.first
                        }
                    }
                }
                delay(500)
            }
        }
    }

    override fun onStopListening() {
        listening = false
        observer?.cancel(); observer = null
        watching?.cancel(); watching = null; watched = null
        super.onStopListening()
    }
    override fun onDestroy() {
        generation += 1
        listening = false
        observer?.cancel(); watching?.cancel()
        super.onDestroy()
    }
    override fun onClick() {
        super.onClick()
        if (!listening || app.repository.wol.ui.value.busy) return
        val state = app.repository.state.value
        val target = WolPolicy.target(state)
        if (!WolPolicy.configured(state, target)) { openConfiguration(); return }
        val binding = requireNotNull(target?.profile?.wol)
        val auth = requireNotNull(WolPolicy.authorization(state, binding))
        val click = WakeTileClick(target.profile.deviceId, binding, auth.clientId, SystemClock.elapsedRealtime())
        val attempt = ++generation
        val action = Runnable {
            if (generation == attempt && !isLocked && click.valid(app.repository.state.value, SystemClock.elapsedRealtime())) {
                generation += 1
                // The captured target is immutable. The short manual request can finish after the shade closes.
                app.scope.launch { app.repository.wol.wake(click.deviceId) }
            }
        }
        if (isLocked) unlockAndRun(action) else action.run()
    }
    private fun render() {
        if (!listening) return
        val tile = qsTile ?: return
        val state = app.repository.state.value
        val target = WolPolicy.target(state)
        val ui = app.repository.wol.ui.value
        val configured = WolPolicy.configured(state, target)
        tile.label = getString(R.string.wake_tile_name)
        tile.icon = Icon.createWithResource(this, R.drawable.ic_fluent_power_24_regular)
        // An action tile never equates a delivered packet with a computer's power state.
        tile.state = if (ui.busy || state.storageFailed || !state.loaded) Tile.STATE_UNAVAILABLE else Tile.STATE_INACTIVE
        tile.subtitle = when {
            state.storageFailed -> "请打开应用检查安全存储"
            !configured -> "打开应用配置"
            ui.busy -> "正在处理唤醒请求"
            target?.wolOperation?.status == WolStatus.PacketSent -> "唤醒包已发送"
            target?.wolOperation?.status in setOf(WolStatus.Unknown, WolStatus.Pending) -> "点按查询发送结果"
            target?.wolOperation?.status == WolStatus.Failed -> "上次发送失败"
            else -> target?.profile?.name ?: "请选择电脑"
        }
        tile.contentDescription = tile.label.toString() + ", " + tile.subtitle
        if (Build.VERSION.SDK_INT >= 30) tile.stateDescription = tile.subtitle
        tile.updateTile()
    }
    @Suppress("DEPRECATION")
    @SuppressLint("StartActivityAndCollapseDeprecated")
    private fun openConfiguration() {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        if (Build.VERSION.SDK_INT >= 34) startActivityAndCollapse(PendingIntent.getActivity(this, 1, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        else startActivityAndCollapse(intent)
    }
}
