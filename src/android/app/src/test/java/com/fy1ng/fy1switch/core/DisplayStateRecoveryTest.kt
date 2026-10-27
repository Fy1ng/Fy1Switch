package com.fy1ng.fy1switch.core

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class DisplayStateRecoveryTest {
    @Test fun queriesOriginalOperationThenReadsCurrentPowerWithoutRewritingItsOutcome() = runBlocking {
        for (outcome in listOf(OperationOutcome.Unknown, OperationOutcome.Pending, OperationOutcome.AcceptedUnconfirmed)) {
            val original = operation(outcome)
            val store = uncertainStore(original)
            val transport = FakeTransport().apply { handler = {
                if (command(it) == "operation") {
                    assertEquals(original.operationId.toString(), JSONObject(it.body).getString("operationId"))
                    HttpResult(200, replyJson(it.body, operation = original))
                } else HttpResult(200, replyJson(it.body, status = status(DisplayPower.Off)))
            } }
            val repository = DeviceRepository(store, transport, MutableClock())
            assertEquals(Feedback.Updated, repository.recover(DEVICE))
            assertEquals(listOf("operation", "refresh"), transport.attempts.map(::command))
            assertEquals(original, repository.state.value.selected!!.operation)
            assertFalse(repository.state.value.selected!!.requiresRefresh)
            assertEquals(DisplayPower.Off, StatePolicy.confirmedPower(repository.state.value.selected))
        }
    }

    @Test fun missingOperationRecordStillRefreshesWithoutReplayingWrite() = runBlocking {
        val original = operation(OperationOutcome.Unknown)
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body, status = if (command(it) == "refresh") status() else null))
        } }
        val repository = DeviceRepository(uncertainStore(original), transport, MutableClock())
        assertEquals(Feedback.Updated, repository.recover(DEVICE))
        assertEquals(listOf("operation", "refresh"), transport.attempts.map(::command))
        assertEquals(original, repository.state.value.selected!!.operation)
        assertEquals(TilePower.Active, StatePolicy.tile(repository.state.value))
    }

    @Test fun transientUnknownOrBusyReadGetsOneBoundedRetry() = runBlocking {
        for (busy in listOf(false, true)) {
            val transport = FakeTransport().apply { handler = {
                val observed = if (attempts.size == 1) status(DisplayPower.Unknown).copy(busy = busy) else status()
                HttpResult(200, replyJson(it.body, status = observed))
            } }
            val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
            assertEquals(Feedback.Updated, repository.recover(DEVICE))
            assertEquals(listOf("refresh", "refresh"), transport.attempts.map(::command))
            assertEquals(TilePower.Active, StatePolicy.tile(repository.state.value))
        }
    }

    @Test fun persistentUnknownStopsAfterTwoReadsAndKeepsEveryPowerIntentBlocked() = runBlocking {
        val original = operation(OperationOutcome.Unknown)
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body,
                status = if (command(it) == "refresh") status(DisplayPower.Unknown) else null,
                operation = if (command(it) == "operation") original else null))
        } }
        val repository = DeviceRepository(uncertainStore(original), transport, MutableClock())
        assertEquals(Feedback.Updated, repository.recover(DEVICE))
        assertEquals(listOf("operation", "refresh", "refresh"), transport.attempts.map(::command))
        assertEquals(original, repository.state.value.selected!!.operation)
        assertTrue(repository.state.value.selected!!.requiresRefresh)
        for (intent in PowerIntent.entries) assertFalse(StatePolicy.canSet(repository.state.value, intent))
        assertFalse(repository.state.value.busy)
    }

    @Test fun safetyFailuresAndUnavailableBindingNeverLoop() = runBlocking {
        for (feedback in listOf(Feedback.Security, Feedback.Unauthorized, Feedback.Protocol)) {
            val transport = FakeTransport().apply { handler = {
                when (feedback) {
                    Feedback.Security -> throw certificateFailure()
                    Feedback.Unauthorized -> HttpResult(401, "")
                    else -> HttpResult(200, "invalid fixture JSON")
                }
            } }
            val repository = DeviceRepository(uncertainStore(operation(OperationOutcome.Unknown)), transport, MutableClock())
            assertEquals(feedback, repository.recover(DEVICE))
            assertEquals(listOf("operation"), transport.attempts.map(::command))
        }
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body, status = status(DisplayPower.Unknown).copy(available = false)))
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        repository.recover(DEVICE)
        assertEquals(listOf("refresh"), transport.attempts.map(::command))
    }

    @Test fun automaticReadWaitsForBusyRequestAndThenRefreshes() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = {
            if (attempts.size == 1) { entered.complete(Unit); release.await() }
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val first = launch { repository.refresh(DEVICE) }
        withTimeout(3_000) { entered.await() }
        val recovery = launch { assertEquals(Feedback.Updated, repository.recover(DEVICE)) }
        yield()
        assertEquals(1, transport.attempts.size)
        release.complete(Unit)
        first.join()
        recovery.join()
        assertEquals(listOf("refresh", "refresh"), transport.attempts.map(::command))
        assertFalse(repository.state.value.busy)
    }

    @Test fun changedDefaultWhileWaitingDoesNotReadPreviousDevice() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { pairHandler = { bundle, _ ->
            entered.complete(Unit)
            release.await()
            HttpResult(200, JSONObject().put("deviceId", bundle.deviceId.toString()).put("clientId", CLIENT.toString())
                .put("token", "fixture-token").toString())
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val pairing = launch { repository.pair(bundleJson(deviceId = OTHER), "Other computer") }
        withTimeout(3_000) { entered.await() }
        val recovery = launch { assertEquals(Feedback.Busy, repository.recover(DEVICE)) }
        yield()
        release.complete(Unit)
        pairing.join()
        recovery.join()
        assertEquals(OTHER, repository.state.value.defaultDeviceId)
        assertEquals(listOf(OTHER), transport.attempts.map { it.device.deviceId })
    }

    @Test fun cancellingQueuedRecoveryNeverStartsAnotherRequest() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { handler = {
            entered.complete(Unit)
            release.await()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val repository = DeviceRepository(MemoryStore(), transport, MutableClock())
        val first = launch { repository.refresh(DEVICE) }
        withTimeout(3_000) { entered.await() }
        val recovery = launch { repository.recover(DEVICE) }
        yield()
        recovery.cancelAndJoin()
        release.complete(Unit)
        first.join()
        assertEquals(listOf("refresh"), transport.attempts.map(::command))
        assertFalse(repository.state.value.busy)
        assertEquals(Feedback.Updated, withTimeout(3_000) { repository.refresh(DEVICE) })
        assertEquals(listOf("refresh", "refresh"), transport.attempts.map(::command))
    }

    @Test fun cancellingUnknownReadBackoffNeverStartsTheRetryOrChangesOperationHistory() = runBlocking {
        for (outcome in listOf(OperationOutcome.Unknown, OperationOutcome.Pending, OperationOutcome.AcceptedUnconfirmed)) {
            val original = operation(outcome)
            val transport = FakeTransport().apply { handler = {
                HttpResult(200, replyJson(it.body,
                    status = if (command(it) == "refresh") status(DisplayPower.Unknown) else null,
                    operation = if (command(it) == "operation") original else null))
            } }
            val repository = DeviceRepository(uncertainStore(original), transport, MutableClock())
            val recovery = launch { repository.recover(DEVICE) }
            withTimeout(3_000) { repository.state.first { it.selected?.feedback == Feedback.Updated } }
            recovery.cancelAndJoin()
            assertEquals(listOf("operation", "refresh"), transport.attempts.map(::command))
            assertEquals(original, repository.state.value.selected!!.operation)
            assertTrue(repository.state.value.selected!!.requiresRefresh)
            assertFalse(repository.state.value.busy)
        }
    }

    private fun uncertainStore(original: OperationRecord) = MemoryStore(activeState().let {
        it.copy(devices = listOf(it.selected!!.copy(connected = false, operation = original, requiresRefresh = true)))
    })

    private fun command(attempt: Attempt) = JSONObject(attempt.body).getString("command")
}
