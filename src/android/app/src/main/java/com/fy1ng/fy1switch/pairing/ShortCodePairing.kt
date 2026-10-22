package com.fy1ng.fy1switch.pairing

import com.fy1ng.fy1switch.core.DeviceRepository
import com.fy1ng.fy1switch.core.Protocol
import java.time.Instant

object ShortCodePairing {
    suspend fun acceptBundle(repository: DeviceRepository, text: String, clientName: String): Boolean {
        val bundle = runCatching { Protocol.pairBundle(text, Instant.now()) }.getOrNull() ?: return false
        if (repository.state.value.devices.any { it.profile.deviceId == bundle.deviceId }) return false
        repository.pair(text, clientName)
        return repository.state.value.devices.any { it.profile.deviceId == bundle.deviceId && it.profile.certificatePin == bundle.certificatePin }
    }
}
