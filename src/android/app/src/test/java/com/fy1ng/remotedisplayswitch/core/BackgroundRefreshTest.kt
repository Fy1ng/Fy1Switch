package com.fy1ng.remotedisplayswitch.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicInteger

class BackgroundRefreshTest {
    @Test fun backgroundReadsOnlyAndKeepsUserRequestNoticeAndOperationHistory() = runBlocking {
        val original = operation(OperationOutcome.Unknown)
        val userRequest = RepositoryRequest(REQUEST, RepositoryAction.Set, DEVICE, Feedback.Unknown)
        val initial = activeState().copy(notice = Feedback.Unknown, request = userRequest,
            devices = listOf(activeState().selected!!.copy(operation = original, requiresRefresh = true)))
        val store = MemoryStore(initial)
        lateinit var repository: DeviceRepository
        val transport = FakeTransport().apply { handler = {
            assertFalse(repository.state.value.busy)
            // initialize intentionally clears restored request; no background action may create one.
            assertNull(repository.state.value.request)
            assertEquals(Feedback.Unknown, repository.state.value.notice)
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        repository = DeviceRepository(store, transport, MutableClock(), { 10_000 })

        assertEquals(Feedback.Updated, repository.backgroundRefresh(DEVICE))
        assertEquals(listOf("refresh"), transport.attempts.map { JSONObject(it.body).getString("command") })
        assertEquals(original, repository.state.value.selected!!.operation)
        assertEquals(Feedback.Unknown, repository.state.value.notice)
        assertNull(repository.state.value.request)
        assertFalse(repository.state.value.busy)
        assertTrue(repository.hasFreshPower(DEVICE))
    }

    @Test fun foregroundPreemptsReadWaitsForCleanupAndDoesNotCancelPollingCaller() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val cleanup = CompletableDeferred<Unit>()
        val count = AtomicInteger()
        val transport = FakeTransport().apply { handler = {
            if (count.incrementAndGet() == 1) {
                entered.complete(Unit)
                try { CompletableDeferred<Unit>().await() }
                finally { cleanup.complete(Unit) }
            }
            assertTrue(cleanup.isCompleted)
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val poller = async {
            val result = repository.backgroundRefresh(DEVICE)
            assertTrue(currentCoroutineContext().isActive)
            result
        }
        withTimeout(3_000) { entered.await() }
        assertEquals(Feedback.Updated, withTimeout(3_000) { repository.refresh(DEVICE) })
        assertEquals(Feedback.Busy, poller.await())
        assertEquals(2, transport.attempts.size)
        assertFalse(repository.state.value.busy)
        assertEquals(Feedback.Updated, repository.backgroundRefresh(DEVICE))
    }

    @Test fun foregroundSetPreemptsReadAndIsTheOnlyWrite() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val store = MemoryStore()
        val transport = FakeTransport().apply { handler = {
            if (JSONObject(it.body).getString("command") == "refresh") {
                entered.complete(Unit)
                CompletableDeferred<Unit>().await()
            }
            val pending = requireNotNull(store.value.selected?.operation)
            HttpResult(200, replyJson(it.body, operation = pending.copy(outcome = OperationOutcome.Confirmed,
                observation = observation(DisplayPower.Off, NOW))))
        } }
        val repository = DeviceRepository(store, transport, MutableClock(), { 5_000 })
        val poller = async { repository.backgroundRefresh(DEVICE) }
        withTimeout(3_000) { entered.await() }
        assertEquals(Feedback.Confirmed, withTimeout(3_000) { repository.set(DEVICE, PowerIntent.Off) })
        assertEquals(Feedback.Busy, poller.await())
        assertEquals(listOf("refresh", "set"), transport.attempts.map { JSONObject(it.body).getString("command") })
        assertTrue(repository.hasFreshPower(DEVICE))
    }

    @Test fun cancellingBackgroundReleasesMutexAndDoesNotPublishUserFeedback() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = {
            entered.complete(Unit)
            CompletableDeferred<HttpResult>().await()
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val background = launch { repository.backgroundRefresh(DEVICE) }
        withTimeout(3_000) { entered.await() }
        withTimeout(3_000) { background.cancelAndJoin() }
        assertFalse(repository.state.value.busy)
        assertNull(repository.state.value.notice)
        assertNull(repository.state.value.request)
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
    }

    @Test fun busyMismatchedAndConcurrentBackgroundReadsSkipNetwork() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = {
            entered.complete(Unit)
            release.await()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        assertEquals(Feedback.Busy, repository.backgroundRefresh(OTHER))
        assertTrue(transport.attempts.isEmpty())
        val foreground = launch { repository.refresh(DEVICE) }
        withTimeout(3_000) { entered.await() }
        val busyRequest = repository.state.value.request
        assertEquals(Feedback.Busy, repository.backgroundRefresh(DEVICE))
        assertEquals(busyRequest, repository.state.value.request)
        assertEquals(1, transport.attempts.size)
        release.complete(Unit)
        foreground.join()

        val enteredAgain = CompletableDeferred<Unit>()
        val releaseAgain = CompletableDeferred<Unit>()
        transport.handler = {
            enteredAgain.complete(Unit)
            releaseAgain.await()
            HttpResult(200, replyJson(it.body, status = status()))
        }
        val first = launch { repository.backgroundRefresh(DEVICE) }
        withTimeout(3_000) { enteredAgain.await() }
        assertEquals(Feedback.Busy, repository.backgroundRefresh(DEVICE))
        assertEquals(2, transport.attempts.size)
        releaseAgain.complete(Unit)
        first.join()
        assertEquals(busyRequest?.id, repository.state.value.request?.id)
    }

    @Test fun freshnessUsesPhoneMonotonicTtlAndIsNeverRestored() = runBlocking {
        var elapsed = 1_000L
        val clock = MutableClock()
        val store = MemoryStore()
        val repository = DeviceRepository(store, FakeTransport(), clock, { elapsed })
        repository.initialize()
        assertFalse(repository.hasFreshPower(DEVICE))
        assertEquals(Feedback.Updated, repository.backgroundRefresh(DEVICE))
        assertTrue(repository.hasFreshPower(DEVICE))
        clock.now = NOW.minusSeconds(3_600)
        elapsed = 26_000
        assertTrue(repository.hasFreshPower(DEVICE))
        elapsed = 26_001
        assertFalse(repository.hasFreshPower(DEVICE))
        assertTrue(repository.hasFreshPower(DEVICE, 30_000))
        assertFalse(repository.hasFreshPower(DEVICE, -1))
        elapsed = 999
        assertFalse(repository.hasFreshPower(DEVICE))
        elapsed = 1_100
        assertFalse(repository.hasFreshPower(OTHER))
        val restarted = DeviceRepository(store, FakeTransport(), MutableClock(), { elapsed })
        restarted.initialize()
        assertNull(restarted.state.value.selected!!.lastLiveReadAtMillis)
        assertFalse(restarted.hasFreshPower(DEVICE))
        val encoded = StateCodec.encode(store.value)
        assertFalse(encoded.contains("lastLiveReadAtMillis"))
        assertNull(StateCodec.decode(encoded).selected!!.lastLiveReadAtMillis)
    }

    @Test fun failureUnknownAndChangedCachedTargetInvalidateFreshness() = runBlocking {
        val transport = FakeTransport()
        val clock = MutableClock()
        val repository = DeviceRepository(MemoryStore(), transport, clock, { 1_000 })
        assertEquals(Feedback.Updated, repository.backgroundRefresh(DEVICE))
        transport.handler = { throw SocketTimeoutException() }
        assertEquals(Feedback.Offline, repository.backgroundRefresh(DEVICE))
        assertFalse(repository.hasFreshPower(DEVICE))
        assertNull(repository.state.value.selected!!.lastLiveReadAtMillis)
        clock.now = NOW.plusSeconds(5)
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        assertEquals(Feedback.Updated, repository.backgroundRefresh(DEVICE))
        assertTrue(repository.hasFreshPower(DEVICE))
        transport.handler = { HttpResult(200, replyJson(it.body, status = status().copy(targetId = OTHER))) }
        assertEquals(Feedback.Updated, repository.refresh(DEVICE, live = false))
        assertFalse(repository.hasFreshPower(DEVICE))
        transport.handler = { HttpResult(200, replyJson(it.body, status = status(DisplayPower.Unknown))) }
        assertEquals(Feedback.Updated, repository.backgroundRefresh(DEVICE))
        assertFalse(repository.hasFreshPower(DEVICE))
    }

    @Test fun oldConfirmedOperationQueryCannotRenewReadFreshness() = runBlocking {
        var elapsed = 1_000L
        val original = operation()
        val initial = activeState().copy(devices = listOf(activeState().selected!!.copy(operation = original)))
        val transport = FakeTransport()
        val repository = DeviceRepository(MemoryStore(initial), transport, MutableClock(), { elapsed })
        repository.backgroundRefresh(DEVICE)
        elapsed = 30_000
        transport.handler = { HttpResult(200, replyJson(it.body, operation = original)) }
        assertEquals(Feedback.Confirmed, repository.queryOperation(DEVICE))
        assertFalse(repository.hasFreshPower(DEVICE))
        assertNull(repository.state.value.selected!!.lastLiveReadAtMillis)
        assertEquals(original, repository.state.value.selected!!.operation)
    }

    @Test fun foregroundRefreshKeepsRecentPowerFreshWhileItsReadIsBusy() = runBlocking {
        val transport = FakeTransport()
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock(), { 1_000 })
        repository.backgroundRefresh(DEVICE)
        transport.handler = {
            assertTrue(repository.state.value.busy)
            assertTrue(repository.hasFreshPower(DEVICE))
            assertFalse(StatePolicy.canSet(repository.state.value))
            HttpResult(200, replyJson(it.body, status = status()))
        }
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
    }

    @Test fun selectionChangeDropsPreviouslyReadFreshness() = runBlocking {
        val initial = activeState().copy(devices = activeState().devices + DeviceState(profile(deviceId = OTHER)))
        val repository = DeviceRepository(MemoryStore(initial), FakeTransport(), MutableClock(), { 1_000 })
        repository.backgroundRefresh(DEVICE)
        assertTrue(repository.hasFreshPower(DEVICE))
        assertEquals(Feedback.Saved, repository.select(OTHER))
        assertFalse(repository.hasFreshPower(DEVICE))
        assertEquals(Feedback.Busy, repository.backgroundRefresh(DEVICE))
        repository.select(DEVICE)
        assertFalse(repository.hasFreshPower(DEVICE))
        assertNull(repository.state.value.selected!!.lastLiveReadAtMillis)
    }

    @Test fun backgroundStorageFailureStopsFurtherReadsWithoutGlobalNotice() = runBlocking {
        val store = MemoryStore()
        val transport = FakeTransport()
        val repository = DeviceRepository(store, transport, MutableClock(), { 1_000 })
        repository.backgroundRefresh(DEVICE)
        store.failWrite = true
        assertEquals(Feedback.Storage, repository.backgroundRefresh(DEVICE))
        assertTrue(repository.state.value.storageFailed)
        assertNull(repository.state.value.notice)
        assertNull(repository.state.value.request)
        assertFalse(repository.state.value.busy)
        assertFalse(repository.hasFreshPower(DEVICE))
        assertEquals(Feedback.Storage, repository.backgroundRefresh(DEVICE))
        assertEquals(2, transport.attempts.size)
    }

    @Test fun unreadableStoreSkipsBackgroundNetworkWithoutPublishingNotice() = runBlocking {
        val transport = FakeTransport()
        val repository = DeviceRepository(MemoryStore().apply { failRead = true }, transport, MutableClock())
        assertEquals(Feedback.Storage, repository.backgroundRefresh(DEVICE))
        assertTrue(repository.state.value.storageFailed)
        assertNull(repository.state.value.notice)
        assertNull(repository.state.value.request)
        assertFalse(repository.state.value.busy)
        assertTrue(transport.attempts.isEmpty())
    }
}
