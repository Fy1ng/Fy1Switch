package com.fy1ng.remotedisplayswitch.background

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import android.os.SystemClock
import com.fy1ng.remotedisplayswitch.MainActivity
import com.fy1ng.remotedisplayswitch.R
import com.fy1ng.remotedisplayswitch.RemoteDisplayApplication
import com.fy1ng.remotedisplayswitch.core.DisplayPower
import com.fy1ng.remotedisplayswitch.core.Feedback
import com.fy1ng.remotedisplayswitch.core.StatePolicy
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

class BackgroundDisplaySyncService : Service() {
    private val app get() = application as RemoteDisplayApplication
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var polling: Job? = null
    private var observing: Job? = null
    private var foreground = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            app.backgroundSync.setEnabled(false)
            stopSelf()
            return START_NOT_STICKY
        }
        if (!app.backgroundSync.state.value.enabled) {
            stopSelf()
            return START_NOT_STICKY
        }
        app.backgroundSync.notificationProblem()?.let {
            app.backgroundSync.reportFailure(it)
            stopSelf()
            return START_NOT_STICKY
        }
        try {
            app.backgroundSync.createNotificationChannel()
            startForeground(NOTIFICATION_ID, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
            foreground = true
            app.backgroundSync.reportRunning(true)
        } catch (_: RuntimeException) {
            app.backgroundSync.reportFailure("无法显示后台同步通知，请检查系统权限后重新开启。")
            stopSelf()
            return START_NOT_STICKY
        }
        if (polling?.isActive != true) polling = scope.launch {
            try {
                app.repository.initialize()
                app.repository.state.map { it.defaultDeviceId to it.storageFailed }.distinctUntilChanged().collectLatest { (deviceId, failed) ->
                    if (failed || deviceId == null) {
                        app.backgroundSync.reportFailure(if (failed) "安全存储不可用，后台同步已停止。" else "没有可同步的电脑，后台同步已停止。")
                        stopSelf()
                        return@collectLatest
                    }
                    var failures = 0
                    while (currentCoroutineContext().isActive && app.backgroundSync.state.value.enabled) {
                        app.backgroundSync.notificationProblem()?.let {
                            app.backgroundSync.reportFailure(it)
                            stopSelf()
                            return@collectLatest
                        }
                        val result = app.repository.backgroundRefresh(deviceId)
                        if (BackgroundSyncPolicy.mustStop(result)) {
                            app.backgroundSync.reportFailure(result.text)
                            stopSelf()
                            return@collectLatest
                        }
                        failures = if (result in setOf(Feedback.Updated, Feedback.Busy)) 0 else failures + 1
                        updateNotification()
                        delay(BackgroundSyncPolicy.delayMillis(result, failures))
                    }
                }
            } catch (exception: CancellationException) { throw exception }
            catch (_: Exception) {
                app.backgroundSync.reportFailure("后台同步已停止，请打开应用检查后重新开启。")
                stopSelf()
            }
        }
        if (observing?.isActive != true) observing = scope.launch {
            app.repository.state.collectLatest { state ->
                updateNotification()
                val readAt = state.selected?.lastLiveReadAtMillis
                if (readAt != null && state.defaultDeviceId?.let { app.repository.hasFreshPower(it) } == true) {
                    val untilExpired = 25_001L - (SystemClock.elapsedRealtime() - readAt)
                    if (untilExpired > 0) {
                        delay(untilExpired)
                        updateNotification()
                    }
                }
            }
        }
        return START_NOT_STICKY
    }

    private fun notification(): Notification {
        val device = app.repository.state.value.selected
        val fresh = device != null && app.repository.hasFreshPower(device.profile.deviceId)
        val status = if (!fresh) "等待获取最新状态" else when (StatePolicy.confirmedPower(device)) {
            DisplayPower.On -> "显示器已开启"
            DisplayPower.Off -> "显示器已关闭"
            else -> "等待获取最新状态"
        }
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, BackgroundDisplaySyncService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, BackgroundSyncManager.CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_display)
            .setContentTitle("显示器后台同步")
            .setContentText(device?.let { "${it.profile.name} · $status" } ?: "正在读取电脑配置")
            .setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true)
            .setVisibility(Notification.VISIBILITY_PRIVATE).setShowWhen(false)
            .setCategory(Notification.CATEGORY_SERVICE)
            .addAction(Notification.Action.Builder(null, "停止同步", stop).build())
            .build()
    }

    private fun updateNotification() {
        if (foreground && app.backgroundSync.state.value.enabled) try {
            getSystemService(NotificationManager::class.java)?.notify(NOTIFICATION_ID, notification())
        } catch (_: RuntimeException) {
            app.backgroundSync.reportFailure("无法更新后台同步通知，服务已停止，请检查系统通知设置。")
            stopSelf()
        }
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        app.backgroundSync.reportFailure("系统已停止后台同步，请回到应用后重新开启。")
        stopSelf()
    }

    override fun onDestroy() {
        foreground = false
        scope.cancel()
        app.backgroundSync.reportRunning(false)
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }

    private companion object {
        const val NOTIFICATION_ID = 1002
        const val ACTION_STOP = "com.fy1ng.remotedisplayswitch.STOP_BACKGROUND_SYNC"
    }
}
