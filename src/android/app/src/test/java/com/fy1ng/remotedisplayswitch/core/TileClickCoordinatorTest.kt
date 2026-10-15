package com.fy1ng.remotedisplayswitch.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class TileClickCoordinatorTest {
    private fun refreshing() = activeState().copy(
        busy = true, request = RepositoryRequest(REQUEST, RepositoryAction.Refresh, DEVICE),
    )

    private fun completed(state: AppState = refreshing(), result: Feedback? = Feedback.Updated) = state.copy(
        busy = false, request = state.request!!.copy(result = result),
    )

    private fun coordinator() = TileClickCoordinator().also { it.startListening() }

    private fun queue(coordinator: TileClickCoordinator, state: AppState = refreshing(), now: Long = 100) =
        requireNotNull(coordinator.queue(state, now)).also { assertTrue(coordinator.authorize(it)) }

    @Test fun repeatedClicksMergeAndSuccessfulRefreshDeliversOnlyOnce() {
        val coordinator = coordinator()
        val click = queue(coordinator)
        assertEquals(click, coordinator.queue(refreshing(), 500))
        assertNull(coordinator.takeReady(refreshing(), 600, locked = false))
        assertTrue(coordinator.hasPending)
        assertEquals(DEVICE, coordinator.takeReady(completed(), 800, locked = false))
        assertFalse(coordinator.hasPending)
        assertNull(coordinator.takeReady(completed(), 900, locked = false))
        assertFalse(coordinator.authorize(click))
    }

    @Test fun failedOrCancelledRefreshDiscardsQueuedClick() {
        for (result in listOf(null, Feedback.Failed, Feedback.Offline, Feedback.Expired, Feedback.Security,
            Feedback.Protocol, Feedback.Storage, Feedback.Busy)) {
            val coordinator = coordinator()
            val click = queue(coordinator)
            assertNull(coordinator.takeReady(completed(result = result), 200, locked = false))
            assertFalse(coordinator.hasPending)
            assertFalse(coordinator.authorize(click))
            assertNull(coordinator.takeReady(completed(), 300, locked = false))
        }
    }

    @Test fun changedDeviceRequestOrBindingCannotReceiveQueuedClick() {
        val state = completed()
        val device = requireNotNull(state.selected)
        val request = requireNotNull(state.request)
        val other = DeviceState(profile(deviceId = OTHER), status(deviceId = OTHER), connected = true)
        val changed = listOf(
            state.copy(defaultDeviceId = OTHER, devices = listOf(device, other)),
            state.copy(request = request.copy(id = OTHER)),
            state.copy(request = request.copy(action = RepositoryAction.Set)),
            state.copy(request = request.copy(deviceId = OTHER)),
            state.copy(devices = listOf(device.copy(status = status().copy(targetId = OTHER)))),
            state.copy(devices = listOf(device.copy(status = status(deviceId = OTHER)))),
        )
        for (next in changed) {
            val coordinator = coordinator()
            queue(coordinator)
            assertNull(coordinator.takeReady(next, 200, locked = false))
            assertFalse(coordinator.hasPending)
        }
    }

    @Test fun repeatedClickDoesNotExtendTimeoutAndClockReversalCancels() {
        for (now in listOf(99L, 30_100L)) {
            val coordinator = coordinator()
            val first = queue(coordinator)
            assertEquals(first, coordinator.queue(refreshing(), 30_000))
            assertNull(coordinator.takeReady(completed(), now, locked = false))
            assertFalse(coordinator.hasPending)
        }
    }

    @Test fun stoppingListeningInvalidatesPendingClickAndUnlockCallback() {
        val coordinator = coordinator()
        val click = requireNotNull(coordinator.queue(refreshing(), 100))
        coordinator.stopListening()
        assertFalse(coordinator.hasPending)
        assertNull(coordinator.queue(refreshing(), 200))
        coordinator.startListening()
        assertFalse(coordinator.authorize(click))
        assertNull(coordinator.takeReady(completed(), 300, locked = false))
    }

    @Test fun lockBeforeDeliveryCancelsInsteadOfWaitingForLaterUnlock() {
        val coordinator = coordinator()
        val click = queue(coordinator)
        assertNull(coordinator.takeReady(completed(), 200, locked = true))
        assertFalse(coordinator.authorize(click))
        assertNull(coordinator.takeReady(completed(), 300, locked = false))
    }

    @Test fun completionBeforeInitialUnlockWaitsAndDeliversOnlyAfterAuthorization() {
        val coordinator = coordinator()
        val click = requireNotNull(coordinator.queue(refreshing(), 100))
        assertNull(coordinator.takeReady(refreshing(), 150, locked = true))
        assertNull(coordinator.takeReady(completed(), 200, locked = true))
        assertTrue(coordinator.hasPending)
        assertNull(coordinator.takeReady(completed(), 250, locked = false))
        assertTrue(coordinator.authorize(click))
        assertEquals(DEVICE, coordinator.takeReady(completed(), 300, locked = false))
        assertNull(coordinator.takeReady(completed(), 400, locked = false))
    }

    @Test fun stoppedLifecycleAndFailedRefreshNeverResumeAfterUnlock() {
        for (stop in listOf(false, true)) {
            val coordinator = coordinator()
            val click = requireNotNull(coordinator.queue(refreshing(), 100))
            if (stop) {
                coordinator.stopListening()
                coordinator.startListening()
            } else {
                assertNull(coordinator.takeReady(completed(result = Feedback.Offline), 200, locked = true))
            }
            assertFalse(coordinator.authorize(click))
            assertNull(coordinator.takeReady(completed(), 300, locked = false))
        }
    }

    @Test fun writesOtherReadsAndUncertainOperationsNeverQueue() {
        val state = refreshing()
        val request = requireNotNull(state.request)
        val requests = listOf(
            state.copy(busy = false),
            state.copy(request = request.copy(action = RepositoryAction.Set)),
            state.copy(request = request.copy(action = RepositoryAction.Other)),
            state.copy(request = request.copy(deviceId = OTHER)),
        ) + listOf(OperationOutcome.Pending, OperationOutcome.Unknown, OperationOutcome.AcceptedUnconfirmed).map { outcome ->
            state.copy(devices = listOf(state.selected!!.copy(operation = operation(outcome), requiresRefresh = true)))
        }
        for (next in requests) {
            val coordinator = coordinator()
            assertNull(coordinator.queue(next, 100))
            assertFalse(coordinator.hasPending)
        }
    }

    @Test fun unknownUnavailableOrUncertainCompletionNeverDelivers() {
        val state = completed()
        val device = requireNotNull(state.selected)
        val invalid = listOf(
            state.copy(devices = listOf(device.copy(status = status(DisplayPower.Unknown)))),
            state.copy(devices = listOf(device.copy(status = status().copy(available = false)))),
            state.copy(devices = listOf(device.copy(status = status().copy(busy = true)))),
            state.copy(devices = listOf(device.copy(status = status().copy(targetId = null)))),
            state.copy(devices = listOf(device.copy(connected = false))),
            state.copy(devices = listOf(device.copy(requiresRefresh = true))),
            state.copy(storageFailed = true),
            state.copy(loaded = false),
        ) + listOf(OperationOutcome.Pending, OperationOutcome.Unknown, OperationOutcome.AcceptedUnconfirmed).map { outcome ->
            state.copy(devices = listOf(device.copy(operation = operation(outcome))))
        }
        for (next in invalid) {
            val coordinator = coordinator()
            queue(coordinator)
            assertNull(coordinator.takeReady(next, 200, locked = false))
            assertFalse(coordinator.hasPending)
        }
    }

    @Test fun actualRefreshCompletionDeliversOneToggleThroughRepository() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = {
            val json = JSONObject(it.body)
            if (json.getString("command") == "refresh") {
                entered.complete(Unit)
                release.await()
                HttpResult(200, replyJson(it.body, status = status()))
            } else {
                val record = operation().copy(operationId = Protocol.uuid(json.getString("operationId")))
                HttpResult(200, replyJson(it.body, operation = record))
            }
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val coordinator = coordinator()
        val refresh = launch { repository.refresh(DEVICE) }
        withTimeout(3000) { entered.await() }
        assertEquals(RepositoryAction.Refresh, repository.state.value.request?.action)
        queue(coordinator, repository.state.value)
        assertNull(coordinator.takeReady(repository.state.value, 150, locked = false))
        release.complete(Unit)
        refresh.join()
        assertEquals(Feedback.Updated, repository.state.value.request?.result)
        val deviceId = requireNotNull(coordinator.takeReady(repository.state.value, 200, locked = false))
        assertEquals(Feedback.Confirmed, repository.set(deviceId, PowerIntent.Toggle))
        assertNull(coordinator.takeReady(repository.state.value, 300, locked = false))
        assertEquals(listOf("refresh", "set"), transport.attempts.map { JSONObject(it.body).getString("command") })
    }

    @Test fun actualCancelledRefreshCannotDeliverCachedKnownPower() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = {
            entered.complete(Unit)
            CompletableDeferred<HttpResult>().await()
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val coordinator = coordinator()
        val refresh = launch { repository.refresh(DEVICE) }
        withTimeout(3000) { entered.await() }
        val click = queue(coordinator, repository.state.value)
        refresh.cancelAndJoin()
        assertFalse(repository.state.value.busy)
        assertEquals(click.requestId, repository.state.value.request?.id)
        assertNull(repository.state.value.request?.result)
        assertNull(coordinator.takeReady(repository.state.value, 200, locked = false))
        assertFalse(coordinator.authorize(click))
        assertEquals(listOf("refresh"), transport.attempts.map { JSONObject(it.body).getString("command") })
    }
}
