package com.fy1ng.fy1switch

import android.app.Application
import android.os.SystemClock
import com.fy1ng.fy1switch.background.BackgroundSyncManager
import com.fy1ng.fy1switch.core.DeviceRepository
import com.fy1ng.fy1switch.network.HttpsTransport
import com.fy1ng.fy1switch.storage.EncryptedStateStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class Fy1SwitchApplication : Application() {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    lateinit var repository: DeviceRepository
        private set
    lateinit var backgroundSync: BackgroundSyncManager
        private set

    override fun onCreate() {
        super.onCreate()
        repository = DeviceRepository(EncryptedStateStore(this), HttpsTransport(), monotonicMillis = SystemClock::elapsedRealtime)
        backgroundSync = BackgroundSyncManager(this)
        scope.launch { repository.initialize() }
    }
}
