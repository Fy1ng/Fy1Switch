package com.fy1ng.fy1switch.core

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class WolStorageAndTileTest {
    @Test fun legacyStateLoadsWithWolDisabledAndOldIdentityUnchanged() {
        val legacy = JSONObject(StateCodec.encode(activeState()))
        legacy.remove("wolAuthorizations"); legacy.remove("wolTileDeviceId")
        legacy.getJSONArray("devices").getJSONObject(0).remove("wol")
        legacy.getJSONArray("devices").getJSONObject(0).remove("wolOperation")
        val restored = StateCodec.decode(legacy.toString())
        assertEquals(profile(), restored.selected!!.profile)
        assertTrue(restored.wolAuthorizations.isEmpty()); assertNull(restored.wolTileDeviceId)
        assertFalse(WolPolicy.configured(restored, restored.selected))
    }
    @Test fun restartPreservesCredentialsAndIdsButNeverRestoresPendingOrHostOnline() {
        val initial = wolState().copy(wolTileDeviceId = DEVICE, devices = wolState().devices.map {
            it.copy(wolOperation = wolOperation(WolStatus.Pending), connected = true, lastHostResponseAtMillis = 55)
        })
        val restored = StateCodec.decode(StateCodec.encode(initial))
        assertEquals(initial.wolAuthorizations, restored.wolAuthorizations)
        assertEquals(WolStatus.Unknown, restored.selected!!.wolOperation!!.status)
        assertEquals(OPERATION, restored.selected!!.wolOperation!!.operationId)
        assertFalse(restored.selected!!.connected); assertNull(restored.selected!!.lastHostResponseAtMillis)
        assertEquals(DEVICE, restored.wolTileDeviceId)
    }
    @Test fun fixedDeletedTargetNeverFallsBackToDefault() {
        val state = wolState().copy(wolTileDeviceId = OTHER)
        assertNull(WolPolicy.target(state))
        assertNull(WolPolicy.target(StateCodec.decode(StateCodec.encode(state))))
        assertEquals(DEVICE, WolPolicy.target(state.copy(wolTileDeviceId = null))!!.profile.deviceId)
    }
    @Test fun unlockSnapshotRejectsTargetSwitchConfigurationChangeRevocationAndExpiry() {
        val state = wolState()
        val click = WakeTileClick(DEVICE, wolBinding(), CLIENT, 1_000)
        assertTrue(click.valid(state, 1_001))
        assertFalse(click.valid(state.copy(wolTileDeviceId = OTHER), 1_001))
        assertFalse(click.valid(state.copy(wolAuthorizations = emptyList()), 1_001))
        assertFalse(click.valid(state.copy(devices = state.devices.map { it.copy(profile = it.profile.copy(wol = wolBinding().copy(enabled = false))) }), 1_001))
        assertFalse(click.valid(state, 31_000)); assertFalse(click.valid(state, 999))
    }
    @Test fun configuredStateRequiresExplicitTargetWhitelist() {
        val state = wolState().copy(wolAuthorizations = listOf(wolAuth().copy(devices = listOf(WolTarget(OTHER, "Other")))))
        assertFalse(WolPolicy.configured(state, state.selected))
    }
}
