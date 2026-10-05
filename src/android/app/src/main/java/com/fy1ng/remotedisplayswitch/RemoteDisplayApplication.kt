package com.fy1ng.remotedisplayswitch

import android.app.Application
import com.fy1ng.remotedisplayswitch.core.DeviceRepository
import com.fy1ng.remotedisplayswitch.network.HttpsTransport
import com.fy1ng.remotedisplayswitch.storage.EncryptedStateStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class RemoteDisplayApplication : Application() {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    lateinit var repository: DeviceRepository
        private set

    override fun onCreate() {
        super.onCreate()
        repository = DeviceRepository(EncryptedStateStore(this), HttpsTransport())
        scope.launch { repository.initialize() }
    }
}
