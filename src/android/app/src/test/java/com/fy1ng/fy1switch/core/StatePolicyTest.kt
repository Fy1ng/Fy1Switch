package com.fy1ng.fy1switch.core

import org.junit.Assert.*
import org.junit.Test

class StatePolicyTest {
    @Test fun onlyConfirmedOnAndOffEnableTile() {
        assertEquals(TilePower.Active, StatePolicy.tile(activeState()))
        val off = activeState().copy(devices = listOf(activeState().selected!!.copy(status = status(DisplayPower.Off))))
        assertEquals(TilePower.Inactive, StatePolicy.tile(off))
    }

    @Test fun unknownOfflineAndBusyAreUnavailable() {
        val state = activeState()
        val device = state.selected!!
        listOf(
            state.copy(busy = true), state.copy(storageFailed = true), state.copy(loaded = false), AppState(loaded = true),
            state.copy(devices = listOf(device.copy(connected = false))),
            state.copy(devices = listOf(device.copy(status = status(DisplayPower.Unknown)))),
            state.copy(devices = listOf(device.copy(status = status().copy(available = false)))),
            state.copy(devices = listOf(device.copy(status = status().copy(busy = true)))),
            state.copy(devices = listOf(device.copy(status = status().copy(targetId = null)))),
            state.copy(devices = listOf(device.copy(status = status(deviceId = OTHER)))),
        ).forEach { assertEquals(TilePower.Unavailable, StatePolicy.tile(it)) }
    }

    @Test fun mismatchedDeviceIdentityBlocksAllPowerIntents() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(status = status(deviceId = OTHER)))) }
        for (intent in PowerIntent.entries) assertFalse(StatePolicy.canSet(state, intent))
    }

    @Test fun unknownPowerAllowsExplicitCommandsButNeverToggle() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(status = status(DisplayPower.Unknown)))) }
        assertTrue(StatePolicy.canSet(state))
        assertTrue(StatePolicy.canSet(state, PowerIntent.On))
        assertTrue(StatePolicy.canSet(state, PowerIntent.Off))
        assertFalse(StatePolicy.canSet(state, PowerIntent.Toggle))
        assertEquals(TilePower.Unavailable, StatePolicy.tile(state))
    }

    @Test fun onlySelectedLiveRefreshRemainsClickableWhileBusy() {
        val state = activeState().let { it.copy(
            devices = listOf(it.selected!!.copy(connected = false, status = status(DisplayPower.Unknown))),
            busy = true, request = RepositoryRequest(REQUEST, RepositoryAction.Refresh, DEVICE),
        ) }
        assertEquals(TilePower.Refreshing, StatePolicy.tile(state))
        assertFalse(StatePolicy.canSet(state, PowerIntent.Toggle))
        for (request in listOf(null, RepositoryRequest(REQUEST, RepositoryAction.Set, DEVICE),
            RepositoryRequest(REQUEST, RepositoryAction.Other, DEVICE), RepositoryRequest(REQUEST, RepositoryAction.Refresh, OTHER))) {
            assertEquals(TilePower.Unavailable, StatePolicy.tile(state.copy(request = request)))
        }
        assertEquals(TilePower.Unavailable, StatePolicy.tile(state.copy(storageFailed = true)))
        assertEquals(TilePower.Unavailable, StatePolicy.tile(state.copy(loaded = false)))
    }

    @Test fun uncertainOperationsDoNotReusePreviousOnObservation() {
        for (outcome in listOf(OperationOutcome.Pending, OperationOutcome.Unknown, OperationOutcome.AcceptedUnconfirmed)) {
            val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(operation = operation(outcome), requiresRefresh = true))) }
            assertEquals(TilePower.Unavailable, StatePolicy.tile(state))
            assertFalse(StatePolicy.canSet(state))
        }
    }

    @Test fun remoteClockInFutureCannotClearUncertainty() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(
            status = status().copy(observation = observation(at = NOW.plusSeconds(3600))),
            operation = operation(OperationOutcome.Unknown), requiresRefresh = true,
        ))) }
        assertEquals(TilePower.Unavailable, StatePolicy.tile(state))
    }

    @Test fun storageRoundTripPreservesDevicesCredentialsAndDefaultButNotLiveState() {
        val state = activeState(true).copy(devices = listOf(activeState(true).selected!!, DeviceState(profile(deviceId = OTHER))),
            defaultDeviceId = OTHER, busy = true, request = RepositoryRequest(REQUEST, RepositoryAction.Refresh, OTHER))
        val restored = StateCodec.decode(StateCodec.encode(state))
        assertEquals(2, restored.devices.size)
        assertEquals(OTHER, restored.defaultDeviceId)
        assertEquals("fixture-device-token", restored.devices.first().profile.deviceToken)
        assertEquals("fixture-controller-token", restored.devices.first().profile.relay?.controllerToken)
        assertFalse(restored.busy)
        assertNull(restored.request)
        assertFalse(restored.devices.first().connected)
        assertEquals(TilePower.Unavailable, StatePolicy.tile(restored))
    }

    @Test fun restartTurnsPendingIntoUnknownAndKeepsQueryIdentity() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(operation = operation(OperationOutcome.Pending), requiresRefresh = true))) }
        val restored = StateCodec.decode(StateCodec.encode(state)).selected!!
        assertEquals(OperationOutcome.Unknown, restored.operation?.outcome)
        assertEquals(OPERATION, restored.operation?.operationId)
        assertEquals(TARGET, restored.operation?.targetId)
        assertEquals(Feedback.Unknown, restored.feedback)
        assertTrue(restored.requiresRefresh)
    }

    @Test fun persistedWrongDeviceStatusFailsClosed() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(status = status(deviceId = OTHER)))) }
        assertThrows(IllegalArgumentException::class.java) { StateCodec.decode(StateCodec.encode(state)) }
    }
}
