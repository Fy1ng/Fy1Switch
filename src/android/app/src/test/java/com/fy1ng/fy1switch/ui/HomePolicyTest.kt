package com.fy1ng.fy1switch.ui

import com.fy1ng.fy1switch.core.*
import org.junit.Assert.*
import org.junit.Test

class HomePolicyTest {
    @Test fun onlyRunningFreshBackgroundPowerSkipsForegroundRecovery() {
        val state = activeState()
        assertTrue(HomePolicy.canReuseBackgroundPower(state, running = true, fresh = true))
        assertFalse(HomePolicy.canReuseBackgroundPower(state, running = false, fresh = true))
        assertFalse(HomePolicy.canReuseBackgroundPower(state, running = true, fresh = false))
        assertFalse(HomePolicy.canReuseBackgroundPower(state.copy(loaded = false), running = true, fresh = true))
        assertFalse(HomePolicy.canReuseBackgroundPower(state.copy(busy = true), running = true, fresh = true))
        assertFalse(HomePolicy.canReuseBackgroundPower(state.copy(storageFailed = true), running = true, fresh = true))
        val pending = state.copy(devices = listOf(state.selected!!.copy(
            requiresRefresh = true, operation = operation(OperationOutcome.Unknown),
        )))
        assertFalse(HomePolicy.canReuseBackgroundPower(pending, running = true, fresh = true))
        val unknown = state.copy(devices = listOf(state.selected!!.copy(status = status(DisplayPower.Unknown))))
        assertFalse(HomePolicy.canReuseBackgroundPower(unknown, running = true, fresh = true))
    }

    @Test fun expiredPowerCannotLookConfirmedOrAuthorizeToggle() {
        val original = activeState()
        val visible = HomePolicy.visibleState(original, fresh = false)
        assertEquals(DisplayPower.Unknown, StatePolicy.confirmedPower(visible.selected))
        assertFalse(StatePolicy.canSet(visible, PowerIntent.Toggle))
        assertTrue(StatePolicy.tileRefreshOnly(visible))
        assertNull(HomePolicy.action(visible).intent)
        assertEquals("显示器状态待确认", HomePolicy.status(visible))
        assertTrue(original.selected!!.connected)
        assertFalse(original.selected!!.requiresRefresh)
        assertEquals(original, HomePolicy.visibleState(original, fresh = true))
    }

    @Test fun expirationKeepsOperationRecoveryFactsAndCannotReviveOldUncertainty() {
        val confirmedAfterUnknown = activeState().let { it.copy(devices = listOf(it.selected!!.copy(
            operation = operation(OperationOutcome.Unknown), requiresRefresh = false,
        ))) }
        val expired = HomePolicy.visibleState(confirmedAfterUnknown, fresh = false)
        assertFalse(HomePolicy.action(expired).queryOperation)
        assertEquals(confirmedAfterUnknown.selected!!.operation, expired.selected!!.operation)
        val unresolved = confirmedAfterUnknown.copy(devices = listOf(confirmedAfterUnknown.selected!!.copy(requiresRefresh = true)))
        val visibleUnresolved = HomePolicy.visibleState(unresolved, fresh = false)
        assertTrue(HomePolicy.action(visibleUnresolved).queryOperation)
        assertEquals(unresolved, visibleUnresolved)
    }

    @Test fun knownPowerAlwaysUsesExplicitOppositeIntent() {
        val state = activeState()
        assertEquals(PowerIntent.Off, HomePolicy.action(state).intent)
        assertTrue(HomePolicy.action(state).enabled)
        val off = state.copy(devices = listOf(state.selected!!.copy(status = status(DisplayPower.Off))))
        assertEquals(PowerIntent.On, HomePolicy.action(off).intent)
    }

    @Test fun unknownPrioritizesRefreshButPreservesSafeExplicitActions() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(status = status(DisplayPower.Unknown)))) }
        assertNull(HomePolicy.action(state).intent)
        assertEquals("刷新状态", HomePolicy.action(state).label)
        assertTrue(HomePolicy.action(state).enabled)
        assertTrue(StatePolicy.canSet(state))
        assertEquals("显示器状态待确认", HomePolicy.status(state))
    }

    @Test fun offlineNeverLooksOffOrSendsPowerCommand() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(connected = false, status = status(DisplayPower.Off)))) }
        assertNull(HomePolicy.action(state).intent)
        assertEquals("重新连接", HomePolicy.action(state).label)
        assertEquals("显示器状态待确认", HomePolicy.status(state))
        assertFalse(StatePolicy.canSet(state))
    }

    @Test fun pendingUnknownAndAcceptedAreNotSuccess() {
        for (outcome in listOf(OperationOutcome.Pending, OperationOutcome.Unknown, OperationOutcome.AcceptedUnconfirmed)) {
            val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(operation = operation(outcome), requiresRefresh = true))) }
            assertEquals("显示器状态待确认", HomePolicy.status(state))
            assertNull(HomePolicy.action(state).intent)
            assertTrue(HomePolicy.action(state).queryOperation)
            assertEquals("确认上次结果", HomePolicy.action(state).label)
            assertTrue(HomePolicy.action(state).enabled)
            assertFalse(StatePolicy.canSet(state))
        }
    }

    @Test fun offlineUncertainOperationStillQueriesInsteadOfSendingAnotherPowerCommand() {
        val state = activeState().let { it.copy(devices = listOf(it.selected!!.copy(
            operation = operation(OperationOutcome.Unknown), requiresRefresh = true, connected = false,
        ))) }
        val action = HomePolicy.action(state)
        assertTrue(action.queryOperation)
        assertNull(action.intent)
        assertTrue(action.enabled)
        assertFalse(HomePolicy.action(state.copy(busy = true)).enabled)
    }

    @Test fun freshReadAllowsExplicitPowerWithoutRewritingPreviousUncertainOperation() {
        val uncertain = activeState().let { it.copy(devices = listOf(it.selected!!.copy(
            operation = operation(OperationOutcome.Unknown), requiresRefresh = true,
        ))) }
        assertTrue(HomePolicy.action(uncertain).queryOperation)
        val refreshed = uncertain.copy(devices = listOf(uncertain.selected!!.copy(requiresRefresh = false)))
        assertFalse(HomePolicy.action(refreshed).queryOperation)
        assertEquals(PowerIntent.Off, HomePolicy.action(refreshed).intent)
        assertTrue(HomePolicy.action(refreshed).enabled)
        assertEquals(OperationOutcome.Unknown, refreshed.selected!!.operation!!.outcome)
    }

    @Test fun busyStorageFailureAndUnloadedDisablePrimaryAction() {
        for (state in listOf(activeState().copy(busy = true), activeState().copy(storageFailed = true),
            activeState().copy(loaded = false), AppState(loaded = true))) {
            assertFalse(HomePolicy.action(state).enabled)
        }
    }

    @Test fun hostBusyAndUnavailableDoNotExposeStaleOff() {
        val state = activeState()
        for (remote in listOf(status(DisplayPower.Off).copy(busy = true), status(DisplayPower.Off).copy(available = false))) {
            val next = state.copy(devices = listOf(state.selected!!.copy(status = remote)))
            assertNull(HomePolicy.action(next).intent)
            assertEquals("显示器状态待确认", HomePolicy.status(next))
            assertFalse(StatePolicy.canSet(next))
        }
    }

    @Test fun chooserConnectionLabelsOnlyClaimObservedConnection() {
        val device = activeState().selected!!
        assertEquals("最近连接成功", HomePolicy.connectionStatus(device))
        assertEquals("连接状态待确认", HomePolicy.connectionStatus(device.copy(connected = false)))
        assertEquals("最近连接失败", HomePolicy.connectionStatus(device.copy(connected = false, feedback = Feedback.Offline)))
    }

    @Test fun liveRecoveryShowsProgressInsteadOfUncertainty() {
        val uncertain = activeState().let { it.copy(devices = listOf(it.selected!!.copy(
            connected = false, operation = operation(OperationOutcome.Unknown), requiresRefresh = true,
        ))) }
        val refreshing = uncertain.copy(busy = true, request = RepositoryRequest(REQUEST, RepositoryAction.Refresh, DEVICE))
        assertEquals("正在刷新显示器状态", HomePolicy.status(refreshing))
        assertEquals("正在刷新", HomePolicy.action(refreshing).label)
        assertFalse(HomePolicy.action(refreshing).enabled)
        val querying = refreshing.copy(request = RepositoryRequest(REQUEST, RepositoryAction.Other, DEVICE))
        assertEquals("正在确认显示器状态", HomePolicy.status(querying))
        assertEquals("正在查询", HomePolicy.action(querying).label)
        assertEquals("显示器状态待确认", HomePolicy.status(uncertain))
    }

    @Test fun onlyCompletedSelectedUncertainWritesTriggerAutomaticRecovery() {
        val completed = RepositoryRequest(REQUEST, RepositoryAction.Set, DEVICE, Feedback.Unknown)
        val uncertain = activeState().let { it.copy(devices = listOf(it.selected!!.copy(
            operation = operation(OperationOutcome.Unknown), requiresRefresh = true,
        )), request = completed) }
        assertEquals(REQUEST, HomePolicy.completedUncertainSet(uncertain, DEVICE))
        assertEquals(REQUEST, HomePolicy.completedUncertainSet(uncertain.copy(request = completed.copy(result = null)), DEVICE))
        assertNull(HomePolicy.completedUncertainSet(uncertain.copy(busy = true), DEVICE))
        assertNull(HomePolicy.completedUncertainSet(uncertain, OTHER))
        assertNull(HomePolicy.completedUncertainSet(uncertain.copy(request = completed.copy(action = RepositoryAction.Refresh)), DEVICE))
        assertNull(HomePolicy.completedUncertainSet(uncertain.copy(devices = listOf(uncertain.selected!!.copy(requiresRefresh = false))), DEVICE))
        assertNull(HomePolicy.completedUncertainSet(uncertain.copy(devices = listOf(uncertain.selected!!.copy(operation = operation(OperationOutcome.Confirmed)))), DEVICE))
    }
}
