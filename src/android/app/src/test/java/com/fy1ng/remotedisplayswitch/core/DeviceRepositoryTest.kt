package com.fy1ng.remotedisplayswitch.core

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
        assertEquals(Feedback.InvalidInput, repository.configure(DEVICE, "Display", "https://relay.example", "fixture-device-token", false))
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Display", "https://relay.example", "controller-only", false))
        assertEquals(Feedback.InvalidInput, repository.configure(DEVICE, "Display", "https://other.example", "", false))
        assertEquals("https://relay.example", repository.state.value.selected?.profile?.relay?.endpoint)
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Display", "https://relay.example", "", false))
        assertEquals("controller-only", repository.state.value.selected?.profile?.relay?.controllerToken)
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
