package com.fy1ng.fy1switch.core

import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException

class DisplayControlRepositoryTest {
    @Test fun readsCapabilitiesBeforeWritingPersistsPendingAndKeepsPowerOperationSeparate() = runBlocking {
        val store = MemoryStore(activeState().let { it.copy(devices = it.devices.map { device -> device.copy(operation = operation()) }) })
        val transport = parameterTransport(store); val normal = transport.handler
        transport.handler = { attempt ->
            if (JSONObject(attempt.body).getString("command") == "set-control") {
                val pending = store.value.selected!!.controlOperation!!
                assertEquals(OperationOutcome.Pending, pending.outcome)
                assertEquals(pending.operationId.toString(), JSONObject(attempt.body).getString("operationId"))
            }
            normal(attempt)
        }
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Confirmed, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70))
        assertEquals(listOf("controls", "set-control"), transport.attempts.map { JSONObject(it.body).getString("command") })
        assertEquals(operation(), store.value.selected!!.operation)
        assertEquals(70, store.value.selected!!.controls!!.controls.first().value)
        assertEquals(ControlsFeedback.Confirmed, store.value.selected!!.controlsFeedback)
    }
    @Test fun storeFailureAfterCapabilityReadPreventsAnyWrite() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store)
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Updated, repo.readDisplayControls(DEVICE))
        store.failWrite = true
        assertEquals(Feedback.Storage, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70))
        assertEquals(1, transport.attempts.size)
    }
    @Test fun supportedValuesWhitelistAndUnsupportedControlsBlockWrites() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store)
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.InvalidInput, repo.setDisplayControl(DEVICE, DisplayControl.Volume, 70))
        assertEquals(Feedback.InvalidInput, repo.setDisplayControl(DEVICE, DisplayControl.InputSource, 3))
        assertEquals(Feedback.InvalidInput, repo.setDisplayControl(DEVICE, DisplayControl.Rotation, 90))
        assertEquals(1, transport.attempts.size)
    }
    @Test fun crossRouteRetryPreservesSameParameterOperation() = runBlocking {
        val store = MemoryStore(activeState(true)); val transport = parameterTransport(store); val normal = transport.handler
        transport.handler = { if (JSONObject(it.body).getString("command") == "set-control" && it.route == Route.Direct) throw SocketTimeoutException() else normal(it) }
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Confirmed, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70))
        val writes = transport.attempts.filter { JSONObject(it.body).getString("command") == "set-control" }
        assertEquals(2, writes.size); assertEquals(writes[0].body, writes[1].body)
    }
    @Test fun oldRelayRejectionAfterAmbiguousDirectWriteRemainsUnknown() = runBlocking {
        val store = MemoryStore(activeState(true)); val transport = parameterTransport(store); val normal = transport.handler
        transport.handler = { if (JSONObject(it.body).getString("command") == "set-control") {
            if (it.route == Route.Direct) throw SocketTimeoutException()
            HttpResult(400, "{\"errorCode\":\"invalid_command\"}")
        } else normal(it) }
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Unknown, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70))
        assertEquals(OperationOutcome.Unknown, store.value.selected!!.controlOperation!!.outcome)
    }
    @Test fun oldHostUnsupportedDoesNotMarkDeviceOfflineOrBreakPower() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store); val normal = transport.handler
        transport.handler = { if (JSONObject(it.body).getString("command") == "controls") HttpResult(400, "{\"errorCode\":\"invalid_command\"}") else normal(it) }
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Failed, repo.readDisplayControls(DEVICE))
        assertEquals(ControlsFeedback.Unsupported, repo.state.value.selected!!.controlsFeedback)
        assertTrue(repo.state.value.selected!!.connected)
        assertEquals(Feedback.Ready, repo.state.value.selected!!.feedback)
        assertTrue(StatePolicy.canSet(repo.state.value))
        assertEquals(Feedback.Updated, repo.refresh(DEVICE))
    }
    @Test fun timeoutsRestoreUnknownAndRecoveryQueriesOriginalOperationInsteadOfReapplying() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store); val normal = transport.handler
        transport.handler = { if (JSONObject(it.body).getString("command") == "set-control") throw SocketTimeoutException() else normal(it) }
        val clock = MutableClock(); val repo = DeviceRepository(store, transport, clock)
        assertEquals(Feedback.Unknown, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70))
        val original = store.value.selected!!.controlOperation!!
        val restored = MemoryStore(StateCodec.decode(StateCodec.encode(store.value)))
        val next = parameterTransport(restored); val restarted = DeviceRepository(restored, next, clock)
        assertEquals(Feedback.Updated, restarted.recoverDisplayControls(DEVICE))
        assertTrue(next.attempts.none { JSONObject(it.body).getString("command") == "set-control" })
        val query = next.attempts.single { JSONObject(it.body).getString("command") == "control-operation" }
        assertEquals(original.operationId.toString(), JSONObject(query.body).getString("operationId"))
        assertEquals(OperationOutcome.Confirmed, restored.value.selected!!.controlOperation!!.outcome)
    }
    @Test fun mismatchedQueryCannotConfirmAnotherControlOrRequestedValue() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store); val normal = transport.handler
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Confirmed, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70))
        transport.handler = { if (JSONObject(it.body).getString("command") == "control-operation") HttpResult(200,
            controlReply(it.body, operation = store.value.selected!!.controlOperation!!.copy(control = DisplayControl.Contrast))) else normal(it) }
        assertEquals(Feedback.Protocol, repo.queryDisplayControl(DEVICE))
        assertEquals(DisplayControl.Brightness, store.value.selected!!.controlOperation!!.control)
        assertEquals(ControlsFeedback.Security, repo.state.value.selected!!.controlsFeedback)
    }
    @Test fun cancellationAndConcurrentRequestsNeverRepeatWrite() = runBlocking {
        val store = MemoryStore(); val entered = CompletableDeferred<Unit>()
        val transport = parameterTransport(store); val normal = transport.handler
        transport.handler = { if (JSONObject(it.body).getString("command") == "set-control") { entered.complete(Unit); awaitCancellation() } else normal(it) }
        val repo = DeviceRepository(store, transport, MutableClock())
        val job = launch { repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70) }; entered.await()
        assertEquals(Feedback.Busy, repo.setDisplayControl(DEVICE, DisplayControl.Contrast, 40))
        assertEquals(Feedback.Busy, repo.select(OTHER))
        job.cancelAndJoin()
        assertEquals(OperationOutcome.Unknown, store.value.selected!!.controlOperation!!.outcome)
        assertTrue(store.value.selected!!.controlsRequireRefresh)
        assertEquals(1, transport.attempts.count { JSONObject(it.body).getString("command") == "set-control" })
    }
    @Test fun newDisplayBindingClearsOldCapabilitiesAndOperation() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store)
        val repo = DeviceRepository(store, transport, MutableClock())
        repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70)
        transport.handler = { HttpResult(200, replyJson(it.body, status = status().copy(targetId = OTHER))) }
        assertEquals(Feedback.Updated, repo.refresh(DEVICE))
        assertNull(store.value.selected!!.controls); assertNull(store.value.selected!!.controlOperation)
        assertFalse(repo.hasFreshControls(DEVICE))
    }
    @Test fun staleUiTargetCannotWriteToNewBoundTargetEvenWithSameCachedReadback() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store); val normal = transport.handler
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Updated, repo.readDisplayControls(DEVICE))
        val renderedTarget = repo.state.value.selected!!.controls!!.targetId
        transport.handler = { if (JSONObject(it.body).getString("command") == "refresh")
            HttpResult(200, replyJson(it.body, status = status().copy(targetId = OTHER))) else normal(it) }
        assertEquals(Feedback.Updated, repo.refresh(DEVICE))
        assertEquals(Feedback.Updated, repo.readDisplayControls(DEVICE))
        assertEquals(65, repo.state.value.selected!!.controls!!.controls.first().value)
        val callsBeforeClick = transport.attempts.size
        assertEquals(Feedback.Busy, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70, expectedTargetId = renderedTarget))
        assertEquals(callsBeforeClick, transport.attempts.size)
        assertTrue(transport.attempts.none { JSONObject(it.body).getString("command") == "set-control" })
        assertNull(repo.state.value.selected!!.controlOperation)
        assertEquals(OTHER, repo.state.value.selected!!.controls!!.targetId)
        assertEquals(ControlsFeedback.TargetChanged, repo.state.value.selected!!.controlsFeedback)
    }
    @Test fun switchingComputerKeepsEachParameterResultWithItsOwnDevice() = runBlocking {
        val initial = activeState().let { it.copy(devices = it.devices + DeviceState(profile(deviceId = OTHER), status(deviceId = OTHER), connected = true)) }
        val store = MemoryStore(initial); val repo = DeviceRepository(store, parameterTransport(store), MutableClock())
        repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70)
        val first = store.value.selected!!.controlOperation
        assertEquals(Feedback.Saved, repo.select(OTHER))
        assertNull(repo.state.value.selected!!.controlOperation)
        assertEquals(Feedback.Confirmed, repo.setDisplayControl(OTHER, DisplayControl.Contrast, 40))
        assertEquals(first, repo.state.value.devices.first { it.profile.deviceId == DEVICE }.controlOperation)
        assertEquals(DisplayControl.Contrast, repo.state.value.selected!!.controlOperation!!.control)
    }
    @Test fun unconfirmedWriteStaysUnconfirmedAfterFreshCapabilityRead() = runBlocking {
        val store = MemoryStore(); val transport = parameterTransport(store); val normal = transport.handler
        transport.handler = { if (JSONObject(it.body).getString("command") == "set-control") HttpResult(200, controlReply(it.body,
            operation = store.value.selected!!.controlOperation!!.copy(outcome = OperationOutcome.AcceptedUnconfirmed,
                result = ControlWriteResult(true, null, false, "readback_failed")))) else normal(it) }
        val repo = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.AcceptedUnconfirmed, repo.setDisplayControl(DEVICE, DisplayControl.Brightness, 70))
        assertTrue(repo.state.value.selected!!.controlsRequireRefresh)
        assertEquals(Feedback.Updated, repo.readDisplayControls(DEVICE))
        assertEquals(OperationOutcome.AcceptedUnconfirmed, repo.state.value.selected!!.controlOperation!!.outcome)
        assertFalse(repo.state.value.selected!!.controlsRequireRefresh)
    }
}
