package com.fy1ng.fy1switch.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException

class RelayPairingTest {
    private val relay = "https://relay.example"

    private class RelayTransport(val base: FakeTransport = FakeTransport()) : Transport by base {
        var joins = 0
        var authorizations = 0
        var learning = 0
        var submittedCode: String? = null
        var joinedEndpoint: String? = null
        var authorizedDevice: DeviceProfile? = null
        var join: suspend () -> HttpResult = { error("Unexpected Relay pairing") }
        var authorize: suspend () -> HttpResult = { error("Unexpected Relay authorization") }
        var learn: suspend () -> HttpResult = { error("Unexpected Relay learning") }
        override suspend fun pairRelay(endpoint: String, code: String, clientName: String, timeoutMillis: Long): HttpResult {
            joins++
            submittedCode = code
            joinedEndpoint = endpoint
            return join()
        }
        override suspend fun authorizeRelay(device: DeviceProfile, endpoint: String, timeoutMillis: Long): HttpResult {
            authorizations++
            authorizedDevice = device
            joinedEndpoint = endpoint
            return authorize()
        }
        override suspend fun learnRelay(device: DeviceProfile, timeoutMillis: Long): HttpResult {
            learning++
            authorizedDevice = device
            return learn()
        }
    }

    private fun authorization(deviceId: java.util.UUID = DEVICE, clientId: java.util.UUID = CLIENT) = JSONObject()
        .put("deviceId", deviceId).put("clientId", clientId).put("controllerToken", "new-relay-controller-token")

    private fun bootstrap() = authorization().put("deviceToken", "new-windows-device-token")
        .put("endpoint", "https://192.0.2.1:8443").put("certificatePin", PIN)
        .put("tailscaleEndpoint", "https://100.64.0.2:8443")

    private fun learning(endpoint: String = relay) = authorization().put("endpoint", endpoint)

    private fun announcedTransport(endpoint: String = relay) = RelayTransport().apply {
        base.handler = { HttpResult(200, replyJson(it.body, status = status().copy(relayEndpoint = endpoint))) }
        learn = { HttpResult(200, learning(endpoint).toString()) }
    }

    @Test fun firstRelayPairingNeedsNoPreviousWindowsBindingAndPreservesLeadingZeros() = runBlocking {
        val store = MemoryStore(AppState(loaded = true))
        val transport = RelayTransport().apply { join = { HttpResult(200, bootstrap().toString()) } }
        val repository = DeviceRepository(store, transport, MutableClock())

        assertEquals(Feedback.Paired, repository.pairRelay("$relay/", "000042", "Phone"))
        val saved = StateCodec.decode(StateCodec.encode(store.value)).selected!!.profile
        assertEquals(DEVICE, saved.deviceId)
        assertEquals(CLIENT, saved.clientId)
        assertEquals("new-windows-device-token", saved.deviceToken)
        assertEquals("new-relay-controller-token", saved.relay!!.controllerToken)
        assertEquals(relay, saved.relay.endpoint)
        assertEquals("https://100.64.0.2:8443", saved.tailscaleEndpoint)
        assertEquals("000042", transport.submittedCode)
        assertEquals(relay, transport.joinedEndpoint)
        assertEquals(1, transport.joins)
        assertEquals(0, transport.base.pairCalls)
        assertTrue(transport.base.attempts.isEmpty())
        assertFalse(StateCodec.encode(store.value).contains("000042"))
    }

    @Test fun invalidCodeOrAddressNeverReachesRelay() = runBlocking {
        val transport = RelayTransport()
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        for (code in listOf("12345", "1234567", "１２３４５６", "12345x")) {
            assertEquals(Feedback.InvalidRelayCode, repository.pairRelay(relay, code, "Phone"))
        }
        assertEquals(Feedback.InvalidRelayEndpoint, repository.pairRelay("http://relay.example", "000042", "Phone"))
        assertEquals(0, transport.joins)
    }

    @Test fun failedOrExpiredCodeIsNotRetriedOrSaved() = runBlocking {
        for ((status, feedback) in listOf(400 to Feedback.InvalidInput, 401 to Feedback.InvalidInput, 410 to Feedback.Expired,
            429 to Feedback.Busy, 503 to Feedback.Offline)) {
            val store = MemoryStore(AppState(loaded = true))
            val transport = RelayTransport().apply { join = { HttpResult(status, "") } }
            val repository = DeviceRepository(store, transport, MutableClock())
            assertEquals(feedback, repository.pairRelay(relay, "000042", "Phone"))
            assertTrue(store.value.devices.isEmpty())
            assertEquals(1, transport.joins)
        }
    }

    @Test fun firstPairingRejectsMissingIdentityOrCredentialRoleConfusion() = runBlocking {
        for (body in listOf(bootstrap().put("certificatePin", JSONObject.NULL),
            bootstrap().put("controllerToken", "new-windows-device-token"),
            bootstrap().put("endpoint", "http://192.0.2.1"))) {
            val store = MemoryStore(AppState(loaded = true))
            val transport = RelayTransport().apply { join = { HttpResult(200, body.toString()) } }
            assertEquals(Feedback.Protocol, DeviceRepository(store, transport, MutableClock()).pairRelay(relay, "000042", "Phone"))
            assertTrue(store.value.devices.isEmpty())
        }
    }

    @Test fun existingPhoneUsesOriginalAuthorizationWithoutNewPairCodeOrIdentity() = runBlocking {
        val store = MemoryStore()
        val original = store.value.selected!!.profile
        val transport = RelayTransport().apply { authorize = { HttpResult(200, authorization().toString()) } }
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Saved, repository.configureConnection(DEVICE, original.name, "$relay/", "", true, true))
        assertEquals(original.copy(relay = RelayConfig(relay, "new-relay-controller-token")), store.value.selected!!.profile)
        assertEquals(original, transport.authorizedDevice)
        assertEquals(1, transport.authorizations)
        assertEquals(0, transport.joins)
        assertEquals(0, transport.base.pairCalls)
        assertEquals(Feedback.Saved, repository.configureConnection(DEVICE, "Renamed", "HTTPS://RELAY.EXAMPLE:443", "", true, true))
        assertEquals(1, transport.authorizations)
        assertEquals(Feedback.Saved, repository.configureConnection(DEVICE, "Renamed", relay, "", true, true, refreshRelay = true))
        assertEquals(2, transport.authorizations)
    }

    @Test fun authorizationCannotReplaceDeviceClientOrWindowsIdentity() = runBlocking {
        for (body in listOf(authorization(deviceId = OTHER), authorization(clientId = OTHER),
            authorization().put("deviceToken", "replacement"), authorization().put("endpoint", relay),
            authorization().put("certificatePin", PIN), authorization().put("controllerToken", "fixture-device-token"))) {
            val store = MemoryStore(activeState(true))
            val original = store.value.selected!!.profile
            val transport = RelayTransport().apply { authorize = { HttpResult(200, body.toString()) } }
            val repository = DeviceRepository(store, transport, MutableClock())
            assertEquals(Feedback.Protocol, repository.configureConnection(DEVICE, "Changed", "https://new.example", "", true, true))
            assertEquals(original, store.value.selected!!.profile)
            assertEquals(1, transport.authorizations)
        }
    }

    @Test fun invalidUnrelatedSettingDoesNotSendWindowsCredentialsToRelay() = runBlocking {
        val transport = RelayTransport()
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        assertEquals(Feedback.InvalidTailscaleEndpoint,
            repository.configureConnection(DEVICE, "Phone", relay, "http://tailscale", true, true))
        assertEquals(0, transport.authorizations)
    }

    @Test fun authenticatedDirectReadLearnsAndDoesNotIssueAnotherCredentialForSameOrigin() = runBlocking {
        val store = MemoryStore()
        val transport = announcedTransport()
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertEquals(RelayConfig(relay, "new-relay-controller-token"), store.value.selected!!.profile.relay)
        assertEquals(1, transport.learning)
        assertEquals(Route.Direct, store.value.selected!!.lastSuccessfulRoute)
        assertEquals("fixture-device-token", transport.authorizedDevice!!.deviceToken)
        repository.refresh(DEVICE)
        assertEquals(1, transport.learning)
    }

    @Test fun tailscaleAndRelayRepliesCannotTriggerAutomaticRelayCredentialIssuance() = runBlocking {
        for (route in listOf(Route.Tailscale, Route.Relay)) {
            val initial = activeState(true).let { state -> state.copy(devices = listOf(state.selected!!.let {
                it.copy(profile = it.profile.copy(tailscaleEndpoint = if (route == Route.Tailscale) "https://100.64.0.2" else null))
            })) }
            val store = MemoryStore(initial)
            val transport = announcedTransport("https://other.example")
            transport.base.handler = {
                if (it.route != route) throw SocketTimeoutException()
                HttpResult(200, replyJson(it.body, status = status().copy(relayEndpoint = "https://other.example")))
            }
            assertEquals(Feedback.Updated, DeviceRepository(store, transport, MutableClock()).refresh(DEVICE))
            assertEquals(0, transport.learning)
            assertEquals(relay, store.value.selected!!.profile.relay!!.endpoint)
        }
    }

    @Test fun automaticLearningChecksExactDeviceClientAndAnnouncedRelayOrigin() = runBlocking {
        for (body in listOf(learning().put("deviceId", OTHER), learning().put("clientId", OTHER),
            learning("https://other.example"), learning().put("controllerToken", "fixture-device-token"))) {
            val store = MemoryStore()
            val transport = announcedTransport().apply { learn = { HttpResult(200, body.toString()) } }
            assertEquals(Feedback.Updated, DeviceRepository(store, transport, MutableClock()).refresh(DEVICE))
            assertNull(store.value.selected!!.profile.relay)
            assertEquals(1, transport.learning)
        }
    }

    @Test fun failedAutomaticLearningHasBoundedRetriesAndDoesNotHideSuccessfulRead() = runBlocking {
        val clock = MutableClock()
        val transport = announcedTransport().apply { learn = { throw SocketTimeoutException() } }
        val repository = DeviceRepository(MemoryStore(), transport, clock)
        repeat(3) { assertEquals(Feedback.Updated, repository.refresh(DEVICE)) }
        assertEquals(1, transport.learning)
        clock.now = clock.now.plusSeconds(61)
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertEquals(2, transport.learning)
    }

    @Test fun explicitRemovalSurvivesRestartAndUserCanReenableAutomaticLearning() = runBlocking {
        val store = MemoryStore(activeState(true))
        val transport = announcedTransport()
        var repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Saved, repository.configureConnection(DEVICE, "Phone", "", "", true, true))
        assertFalse(store.value.selected!!.profile.autoLearnRelay)
        store.value = StateCodec.decode(StateCodec.encode(store.value))
        repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertEquals(0, transport.learning)
        assertNull(store.value.selected!!.profile.relay)
        assertEquals(Feedback.Saved, repository.configureConnection(DEVICE, "Phone", "", "", true, true))
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertEquals(1, transport.learning)
    }

    @Test fun reparingWindowsDiscardsCredentialBoundToOldPhoneIdentity() = runBlocking {
        val store = MemoryStore(activeState(true))
        val repository = DeviceRepository(store, FakeTransport(), MutableClock())
        assertEquals(Feedback.Updated, repository.pair(bundleJson(), "Phone"))
        assertNull(store.value.selected!!.profile.relay)
        assertEquals("fixture-token", store.value.selected!!.profile.deviceToken)
    }

    @Test fun backgroundLearningIsCancelledBeforeForegroundReadAndCannotPublishLateCredential() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val cleanup = CompletableDeferred<Unit>()
        val transport = announcedTransport().apply { learn = {
            entered.complete(Unit)
            try { CompletableDeferred<HttpResult>().await() } finally { cleanup.complete(Unit) }
        } }
        val store = MemoryStore()
        val repository = DeviceRepository(store, transport, MutableClock())
        val background = async { repository.backgroundRefresh(DEVICE) }
        withTimeout(3_000) { entered.await() }
        assertEquals(Feedback.Updated, withTimeout(3_000) { repository.refresh(DEVICE) })
        assertEquals(Feedback.Busy, background.await())
        assertTrue(cleanup.isCompleted)
        assertNull(store.value.selected!!.profile.relay)
        assertEquals(1, transport.learning)
        assertFalse(repository.state.value.busy)
    }

    @Test fun relayCredentialStorageFailureStopsSubsequentNetworkRequests() = runBlocking {
        val store = MemoryStore()
        val transport = announcedTransport().apply { learn = {
            store.failWrite = true
            HttpResult(200, learning().toString())
        } }
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Storage, repository.refresh(DEVICE))
        assertTrue(repository.state.value.storageFailed)
        val attempts = transport.base.attempts.size
        assertEquals(Feedback.Storage, repository.refresh(DEVICE))
        assertEquals(attempts, transport.base.attempts.size)
        assertNull(store.value.selected!!.profile.relay)
    }

    @Test fun oldStoredProfilesDefaultToAutomaticRelayLearningAndRejectInvalidFlag() {
        val json = JSONObject(StateCodec.encode(activeState()))
        val profile = json.getJSONArray("devices").getJSONObject(0)
        profile.remove("autoLearnRelay")
        assertTrue(StateCodec.decode(json.toString()).selected!!.profile.autoLearnRelay)
        profile.put("autoLearnRelay", "false")
        assertThrows(IllegalArgumentException::class.java) { StateCodec.decode(json.toString()) }
    }
}
