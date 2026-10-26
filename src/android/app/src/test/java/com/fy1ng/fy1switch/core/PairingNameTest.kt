package com.fy1ng.fy1switch.core

import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class PairingNameTest {
    private class NameTransport(deviceName: Any? = "DESKTOP-WINDOWS", val base: FakeTransport = FakeTransport()) : Transport by base {
        val submittedNames = mutableListOf<String>()
        private val reply = JSONObject().put("deviceId", DEVICE).put("clientId", CLIENT)
            .put("token", "fixture-device-token").put("deviceToken", "fixture-device-token")
            .put("controllerToken", "fixture-controller-token").put("endpoint", "https://192.0.2.1:8443")
            .put("certificatePin", PIN).apply { if (deviceName != null) put("deviceName", deviceName) }

        override suspend fun pair(bundle: PairBundle, clientName: String, timeoutMillis: Long): HttpResult {
            submittedNames += clientName
            return HttpResult(200, reply.toString())
        }

        override suspend fun pairRelay(endpoint: String, code: String, clientName: String, timeoutMillis: Long): HttpResult {
            submittedNames += clientName
            return HttpResult(200, reply.toString())
        }
    }

    private suspend fun DeviceRepository.pairUsing(relay: Boolean): Feedback =
        if (relay) pairRelay("https://relay.example", "000042", "Android phone")
        else pair(bundleJson(), "Android phone")

    @Test fun newPairingUsesWindowsNameWhileWindowsStillReceivesPhoneName() = runBlocking {
        for (relay in listOf(false, true)) {
            val store = MemoryStore(AppState(loaded = true))
            val transport = NameTransport("  DESKTOP-WINDOWS  ")
            val repository = DeviceRepository(store, transport, MutableClock())
            assertEquals(if (relay) Feedback.Paired else Feedback.Updated, repository.pairUsing(relay))
            assertEquals(listOf("Android phone"), transport.submittedNames)
            assertEquals("DESKTOP-WINDOWS", repository.state.value.selected!!.profile.name)
            assertEquals("DESKTOP-WINDOWS", StateCodec.decode(StateCodec.encode(store.value)).selected!!.profile.name)
            assertEquals(Feedback.Updated, repository.refresh(DEVICE))
            assertEquals("DESKTOP-WINDOWS", repository.state.value.selected!!.profile.name)
        }
    }

    @Test fun pairingAgainPreservesLocallyCustomizedComputerName() = runBlocking {
        for (relay in listOf(false, true)) {
            val device = activeState().selected!!.let { it.copy(profile = it.profile.copy(name = "Study PC")) }
            val store = MemoryStore(activeState().copy(devices = listOf(device)))
            val transport = NameTransport()
            val repository = DeviceRepository(store, transport, MutableClock())
            assertEquals(if (relay) Feedback.Paired else Feedback.Updated, repository.pairUsing(relay))
            assertEquals("Study PC", StateCodec.decode(StateCodec.encode(store.value)).selected!!.profile.name)
            assertEquals(listOf("Android phone"), transport.submittedNames)
        }
    }

    @Test fun olderWindowsWithoutNameNeverDefaultsToPhoneName() = runBlocking {
        for (relay in listOf(false, true)) for (name in listOf(null, JSONObject.NULL)) {
            val store = MemoryStore(AppState(loaded = true))
            val repository = DeviceRepository(store, NameTransport(name), MutableClock())
            assertEquals(if (relay) Feedback.Paired else Feedback.Updated, repository.pairUsing(relay))
            assertEquals("Windows 电脑", StateCodec.decode(StateCodec.encode(store.value)).selected!!.profile.name)
        }
    }

    @Test fun malformedWindowsNameCannotCreateSavedDevice() = runBlocking {
        for (relay in listOf(false, true)) for (name in listOf("", "bad\nname", "x".repeat(81), 42)) {
            val store = MemoryStore(AppState(loaded = true))
            val transport = NameTransport(name)
            val repository = DeviceRepository(store, transport, MutableClock())
            assertEquals(Feedback.Protocol, repository.pairUsing(relay))
            assertTrue(store.value.devices.isEmpty())
            assertEquals(0, store.writes)
            assertTrue(transport.base.attempts.isEmpty())
        }
    }
}
