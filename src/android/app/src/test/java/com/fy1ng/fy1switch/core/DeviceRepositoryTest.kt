package com.fy1ng.fy1switch.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException

class DeviceRepositoryTest {
    @Test fun persistsPendingBeforeSendingAndUsesSingleOperationAcrossFallback() = runBlocking {
        val store = MemoryStore(activeState(true))
        val transport = FakeTransport().apply { handler = {
            val json = JSONObject(it.body)
            assertEquals(OperationOutcome.Pending, store.value.selected?.operation?.outcome)
            assertEquals(store.value.selected?.operation?.operationId.toString(), json.getString("operationId"))
            if (it.route == Route.Direct) throw SocketTimeoutException()
            val record = operation().copy(operationId = Protocol.uuid(json.getString("operationId")), requested = PowerIntent.On)
            HttpResult(200, replyJson(it.body, operation = record))
        } }
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Confirmed, repository.set(DEVICE, PowerIntent.On))
        assertEquals(transport.attempts[0].body, transport.attempts[1].body)
        assertEquals(OperationOutcome.Confirmed, store.value.selected?.operation?.outcome)
    }

    @Test fun timeoutsPersistUnknownAndQueryUsesOriginalOperationWithoutReplayingSet() = runBlocking {
        val store = MemoryStore(activeState(true))
        val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val clock = MutableClock()
        val repository = DeviceRepository(store, transport, clock)
        assertEquals(Feedback.Unknown, repository.set(DEVICE, PowerIntent.Toggle))
        val original = store.value.selected!!.operation!!
        assertEquals(OperationOutcome.Unknown, original.outcome)
        assertFalse(repository.state.value.busy)
        transport.handler = {
            if (JSONObject(it.body).getString("command") == "status") HttpResult(200, replyJson(it.body, status = status()))
            else HttpResult(200, replyJson(it.body, operation = original.copy(outcome = OperationOutcome.Confirmed)))
        }
        assertEquals(Feedback.Offline, repository.queryOperation(DEVICE))
        clock.now = NOW.plusSeconds(5)
        repository.queryOperation(DEVICE)
        val query = JSONObject(transport.attempts.last().body)
        assertEquals("operation", query.getString("command"))
        assertEquals(original.operationId.toString(), query.getString("operationId"))
        assertEquals(original.targetId.toString(), query.getString("targetId"))
        assertNotEquals(JSONObject(transport.attempts.first().body).getString("requestId"), query.getString("requestId"))
    }

    @Test fun acceptedUnconfirmedIsNeverDisplayedAsSuccessfulState() = runBlocking {
        val transport = FakeTransport().apply { handler = {
            val json = JSONObject(it.body)
            val record = operation(OperationOutcome.AcceptedUnconfirmed).copy(operationId = Protocol.uuid(json.getString("operationId")))
            HttpResult(200, replyJson(it.body, status = status(), operation = record))
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        assertEquals(Feedback.AcceptedUnconfirmed, repository.set(DEVICE, PowerIntent.Toggle))
        assertEquals(TilePower.Unavailable, StatePolicy.tile(repository.state.value))
        assertFalse(StatePolicy.canSet(repository.state.value))
    }

    @Test fun refreshCanRecoverUncertainStateButCachedStatusCannot() = runBlocking {
        val store = MemoryStore()
        val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val clock = MutableClock()
        val repository = DeviceRepository(store, transport, clock)
        repository.set(DEVICE, PowerIntent.Toggle)
        clock.now = NOW.plusSeconds(5)
        transport.handler = { HttpResult(200, replyJson(it.body, status = status().copy(observation = observation(at = NOW.plusSeconds(3600))))) }
        repository.refresh(DEVICE, live = false)
        assertEquals(TilePower.Unavailable, StatePolicy.tile(repository.state.value))
        repository.refresh(DEVICE, live = true)
        assertEquals(TilePower.Active, StatePolicy.tile(repository.state.value))
        assertEquals(OperationOutcome.Unknown, repository.state.value.selected?.operation?.outcome)
    }

    @Test fun definitelyUnsentFailureClearsRefreshRequirementAndExplicitPowerRecovers() = runBlocking {
        val clock = MutableClock()
        val memory = MemoryStore()
        var expireWrite = true
        val store = object : StateStore {
            override fun read() = memory.read()
            override fun write(state: AppState) {
                memory.write(state)
                if (expireWrite && state.selected?.operation?.outcome == OperationOutcome.Pending) {
                    expireWrite = false
                    clock.now = clock.now.plusSeconds(31)
                }
            }
        }
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body, status = status(DisplayPower.Unknown)))
        } }
        val repository = DeviceRepository(store, transport, clock)
        assertEquals(Feedback.Expired, repository.set(DEVICE, PowerIntent.On))
        assertTrue(transport.attempts.isEmpty())
        assertEquals(OperationOutcome.Failed, repository.state.value.selected?.operation?.outcome)
        assertFalse(repository.state.value.selected!!.requiresRefresh)
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertTrue(StatePolicy.canSet(repository.state.value, PowerIntent.On))
        assertTrue(StatePolicy.canSet(repository.state.value, PowerIntent.Off))
        assertEquals(Feedback.Busy, repository.set(DEVICE, PowerIntent.Toggle))
        assertEquals(1, transport.attempts.size)
        transport.handler = {
            val pending = requireNotNull(memory.value.selected?.operation)
            HttpResult(200, replyJson(it.body, operation = pending.copy(outcome = OperationOutcome.Failed)))
        }
        for (intent in listOf(PowerIntent.On, PowerIntent.Off)) {
            assertEquals(Feedback.Failed, repository.set(DEVICE, intent))
            assertFalse(repository.state.value.selected!!.requiresRefresh)
        }
        assertEquals(listOf("refresh", "set", "set"), transport.attempts.map { JSONObject(it.body).getString("command") })
    }

    @Test fun trustedFailedOperationQueryReleasesUncertaintyWithoutKnownPower() = runBlocking {
        val clock = MutableClock()
        val store = MemoryStore()
        val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val repository = DeviceRepository(store, transport, clock)
        assertEquals(Feedback.Unknown, repository.set(DEVICE, PowerIntent.On))
        val original = requireNotNull(store.value.selected?.operation)
        clock.now = clock.now.plusSeconds(5)
        transport.handler = {
            if (JSONObject(it.body).getString("command") == "operation") {
                HttpResult(200, replyJson(it.body, operation = original.copy(outcome = OperationOutcome.Failed)))
            } else HttpResult(200, replyJson(it.body, status = status(DisplayPower.Unknown)))
        }
        assertEquals(Feedback.Failed, repository.queryOperation(DEVICE))
        assertFalse(repository.state.value.selected!!.requiresRefresh)
        assertEquals(original.operationId, repository.state.value.selected?.operation?.operationId)
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertEquals(DisplayPower.Unknown, repository.state.value.selected?.status?.observation?.state)
        assertTrue(StatePolicy.canSet(repository.state.value, PowerIntent.On))
        assertTrue(StatePolicy.canSet(repository.state.value, PowerIntent.Off))
        assertFalse(StatePolicy.canSet(repository.state.value, PowerIntent.Toggle))
        assertEquals(1, transport.attempts.count { JSONObject(it.body).getString("command") == "set" })
    }

    @Test fun unresolvedRepliesAndUnknownRefreshStillBlockEveryPowerIntent() = runBlocking {
        for (outcome in listOf(OperationOutcome.Pending, OperationOutcome.Unknown, OperationOutcome.AcceptedUnconfirmed)) {
            val store = MemoryStore()
            val transport = FakeTransport().apply { handler = {
                if (JSONObject(it.body).getString("command") == "refresh") {
                    HttpResult(200, replyJson(it.body, status = status(DisplayPower.Unknown)))
                } else {
                    val pending = requireNotNull(store.value.selected?.operation)
                    HttpResult(200, replyJson(it.body, operation = pending.copy(outcome = outcome)))
                }
            } }
            val repository = DeviceRepository(store, transport, MutableClock())
            repository.set(DEVICE, PowerIntent.Off)
            repository.queryOperation(DEVICE)
            assertEquals(Feedback.Updated, repository.refresh(DEVICE))
            assertEquals(outcome, repository.state.value.selected?.operation?.outcome)
            assertTrue(repository.state.value.selected!!.requiresRefresh)
            val attempts = transport.attempts.size
            for (intent in PowerIntent.entries) assertEquals(Feedback.Busy, repository.set(DEVICE, intent))
            assertEquals(attempts, transport.attempts.size)
        }
    }

    @Test fun restoredLegacyFailedFlagDoesNotRequireKnownPowerOrChangePairing() = runBlocking {
        val device = activeState().selected!!.copy(
            status = status(DisplayPower.Unknown), operation = operation(OperationOutcome.Failed), requiresRefresh = true,
        )
        val stored = StateCodec.decode(StateCodec.encode(activeState().copy(devices = listOf(device))))
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body, status = status(DisplayPower.Unknown)))
        } }
        val repository = DeviceRepository(MemoryStore(stored), transport, MutableClock())
        repository.initialize()
        assertFalse(repository.state.value.selected!!.requiresRefresh)
        assertEquals(device.profile, repository.state.value.selected?.profile)
        assertTrue(transport.attempts.isEmpty())
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertTrue(StatePolicy.canSet(repository.state.value, PowerIntent.On))
        assertFalse(StatePolicy.canSet(repository.state.value, PowerIntent.Toggle))
    }

    @Test fun uiAndTileConcurrentRequestsHaveOneWriterAndBusyClears() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = {
            entered.complete(Unit)
            release.await()
            HttpResult(200, replyJson(it.body))
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val first = launch { repository.set(DEVICE, PowerIntent.Toggle) }
        withTimeout(3000) { entered.await() }
        assertEquals(RepositoryAction.Set, repository.state.value.request?.action)
        assertEquals(TilePower.Unavailable, StatePolicy.tile(repository.state.value))
        assertEquals(Feedback.Busy, repository.set(DEVICE, PowerIntent.Toggle))
        assertEquals(Feedback.Busy, repository.select(DEVICE))
        assertEquals(1, transport.attempts.size)
        release.complete(Unit)
        first.join()
        assertFalse(repository.state.value.busy)
    }

    @Test fun cancelledNetworkCallKeepsQueryIdentityAndDoesNotLeaveBusy() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = { entered.complete(Unit); CompletableDeferred<HttpResult>().await() } }
        val store = MemoryStore()
        val repository = DeviceRepository(store, transport, MutableClock())
        val job = launch { repository.set(DEVICE, PowerIntent.Toggle) }
        withTimeout(3000) { entered.await() }
        job.cancelAndJoin()
        assertFalse(repository.state.value.busy)
        assertEquals(OperationOutcome.Unknown, store.value.selected?.operation?.outcome)
        assertNotNull(store.value.selected?.operation?.operationId)
        assertEquals(RepositoryAction.Set, repository.state.value.request?.action)
        assertNull(repository.state.value.request?.result)
    }

    @Test fun failedStorageStopsNetworkBeforeSending() = runBlocking {
        val store = MemoryStore().apply { failWrite = true }
        val transport = FakeTransport()
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Storage, repository.set(DEVICE, PowerIntent.On))
        assertTrue(transport.attempts.isEmpty())
        assertTrue(repository.state.value.storageFailed)
    }

    @Test fun unreadableEncryptedStoreFailsClosed() = runBlocking {
        val transport = FakeTransport()
        val repository = DeviceRepository(MemoryStore().apply { failRead = true }, transport, MutableClock())
        assertEquals(Feedback.Storage, repository.refresh(DEVICE))
        assertTrue(transport.attempts.isEmpty())
    }

    @Test fun restoredProcessDoesNotReplayPendingOperation() = runBlocking {
        val snapshot = activeState().let { it.copy(devices = listOf(it.selected!!.copy(operation = operation(OperationOutcome.Pending)))) }
        val store = MemoryStore(StateCodec.decode(StateCodec.encode(snapshot)))
        val transport = FakeTransport()
        val repository = DeviceRepository(store, transport, MutableClock())
        repository.initialize()
        assertTrue(transport.attempts.isEmpty())
        assertEquals(OperationOutcome.Unknown, repository.state.value.selected?.operation?.outcome)
    }

    @Test fun pairingSavesOnlyExpectedDeviceAndAutomaticallyConnects() = runBlocking {
        val store = MemoryStore(AppState(loaded = true))
        val transport = FakeTransport()
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Updated, repository.pair(bundleJson(), "Test phone"))
        assertEquals(DEVICE, repository.state.value.defaultDeviceId)
        assertTrue(repository.state.value.selected!!.connected)
        assertEquals("status", JSONObject(transport.attempts.single().body).getString("command"))
        assertFalse(StateCodec.encode(store.value).contains("fixture-pair-code"))
    }

    @Test fun expiredPairBundleNeverCallsNetwork() = runBlocking {
        val transport = FakeTransport()
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        assertEquals(Feedback.InvalidInput, repository.pair(bundleJson(expiresAt = NOW), "Phone"))
        assertEquals(0, transport.pairCalls)
    }

    @Test fun pairResponseCannotReplaceAnotherDevice() = runBlocking {
        val transport = FakeTransport().apply { pairHandler = { _, _ ->
            HttpResult(200, JSONObject().put("deviceId", OTHER).put("clientId", CLIENT).put("token", "fixture-token").toString())
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        assertEquals(Feedback.Protocol, repository.pair(bundleJson(), "Phone"))
        assertEquals(listOf(DEVICE), repository.state.value.devices.map { it.profile.deviceId })
    }

    @Test fun relayTokenIsSeparateAndNotReusedForChangedEndpoint() = runBlocking {
        val repository = DeviceRepository(MemoryStore(), FakeTransport(), MutableClock())
        assertEquals(Feedback.RelayDeviceTokenConflict, repository.configure(DEVICE, "Display", "https://relay.example", "fixture-device-token", false))
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Display", "https://relay.example", "controller-only", false))
        assertEquals(Feedback.RelayTokenRequired, repository.configure(DEVICE, "Display", "https://other.example", "", false))
        assertEquals("https://relay.example", repository.state.value.selected?.profile?.relay?.endpoint)
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Display", "https://relay.example", "", false))
        assertEquals("controller-only", repository.state.value.selected?.profile?.relay?.controllerToken)
    }

    @Test fun relayConfigurationSavesWithoutLanOrNetworkAndPreservesPairing() = runBlocking {
        val initial = StateCodec.decode(StateCodec.encode(activeState()))
        val store = MemoryStore(initial)
        val transport = FakeTransport().apply { handler = { throw AssertionError("Saving settings must not access the network") } }
        val repository = DeviceRepository(store, transport, MutableClock())
        val controllerToken = java.util.Base64.getEncoder().encodeToString(ByteArray(32) { (it + 1).toByte() })

        assertFalse(initial.selected!!.connected)
        assertEquals(Feedback.Saved, repository.configure(DEVICE, initial.selected!!.profile.name,
            "https://screlay.fy1ng.cn/", controllerToken, false, tailscaleEndpoint = "", autoLearnTailscale = true))

        val restored = StateCodec.decode(StateCodec.encode(store.value)).selected!!.profile
        assertEquals(initial.selected!!.profile.copy(relay = RelayConfig("https://screlay.fy1ng.cn", controllerToken)), restored)
        assertTrue(transport.attempts.isEmpty())
        assertEquals(0, transport.pairCalls)
    }

    @Test fun relayConfigurationReusesCredentialForEquivalentHttpsOrigin() = runBlocking {
        val repository = DeviceRepository(MemoryStore(), FakeTransport(), MutableClock())
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Display", "https://screlay.fy1ng.cn", "controller-only", false))
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Display", "HTTPS://SCRELAY.FY1NG.CN:443/", "", false))
        assertEquals("controller-only", repository.state.value.selected!!.profile.relay!!.controllerToken)
        assertEquals(Feedback.RelayTokenRequired, repository.configure(DEVICE, "Display", "https://screlay.fy1ng.cn:8443", "", false))
        assertEquals(Feedback.RelayTokenRequired, repository.configure(DEVICE, "Display", "https://other.example", "", false))
        assertEquals("controller-only", repository.state.value.selected!!.profile.relay!!.controllerToken)
    }

    @Test fun configurationErrorsIdentifyTheFieldWithoutChangingSavedDataOrUsingNetwork() = runBlocking {
        data class Case(
            val name: String = "Display", val endpoint: String = "https://relay.example", val token: String = "controller-only",
            val tailscale: String? = null, val deviceId: java.util.UUID = DEVICE, val expected: Feedback,
        )
        val cases = listOf(
            Case(name = "bad\nname", expected = Feedback.InvalidName),
            Case(endpoint = "https://relay.example/path", expected = Feedback.InvalidRelayEndpoint),
            Case(endpoint = "https://relay .example", expected = Feedback.InvalidRelayEndpoint),
            Case(token = "", expected = Feedback.RelayTokenRequired),
            Case(token = "controller\nvalue", expected = Feedback.InvalidRelayToken),
            Case(token = "fixture-device-token", expected = Feedback.RelayDeviceTokenConflict),
            Case(tailscale = "http://100.64.0.1:51000", expected = Feedback.InvalidTailscaleEndpoint),
            Case(deviceId = OTHER, expected = Feedback.DeviceMissing),
        )
        for (case in cases) {
            val initial = activeState()
            val store = MemoryStore(initial)
            val transport = FakeTransport()
            val repository = DeviceRepository(store, transport, MutableClock())
            assertEquals(case.expected, repository.configure(case.deviceId, case.name, case.endpoint, case.token,
                clearRelay = false, tailscaleEndpoint = case.tailscale))
            assertEquals(case.expected, repository.state.value.notice)
            assertEquals(initial, store.value)
            assertFalse(repository.state.value.storageFailed)
            assertFalse(repository.state.value.busy)
            assertTrue(transport.attempts.isEmpty())
            assertEquals(0, transport.pairCalls)
            assertFalse(case.expected.text.contains("配对码"))
        }
    }

    @Test fun selectedDeviceIsStableAcrossStaleUiActions() = runBlocking {
        val store = MemoryStore(activeState().copy(devices = listOf(activeState().selected!!, DeviceState(profile(deviceId = OTHER), status(deviceId = OTHER), connected = true))))
        val transport = FakeTransport()
        val repository = DeviceRepository(store, transport, MutableClock())
        repository.select(OTHER)
        assertEquals(Feedback.InvalidInput, repository.set(DEVICE, PowerIntent.On))
        assertTrue(transport.attempts.isEmpty())
        repository.remove(OTHER)
        assertEquals(DEVICE, repository.state.value.defaultDeviceId)
    }
}
