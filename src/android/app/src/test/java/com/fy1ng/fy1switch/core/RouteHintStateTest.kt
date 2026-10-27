package com.fy1ng.fy1switch.core

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class RouteHintStateTest {
    private fun configured() = profile(true).copy(tailscaleEndpoint = "https://100.64.0.2:8443")

    private fun state(route: Route) = AppState(
        devices = listOf(DeviceState(configured(), status(), connected = true,
            lastSuccessfulRoute = route, lastLiveReadAtMillis = 123_000L)),
        defaultDeviceId = DEVICE, loaded = true,
    )

    @Test fun configuredRoutesRoundTripWithoutRestoringOnlinePowerOrFreshness() {
        for (route in Route.entries) {
            val original = state(route)
            val restored = StateCodec.decode(StateCodec.encode(original))
            val device = restored.selected!!
            assertEquals(route, device.lastSuccessfulRoute)
            assertEquals(original.selected!!.profile, device.profile)
            assertEquals(original.selected!!.status, device.status)
            assertFalse(device.connected)
            assertNull(device.lastLiveReadAtMillis)
            assertEquals(TilePower.Unavailable, StatePolicy.tile(restored))
        }
    }

    @Test fun oldStateWithoutHintRemainsCompatible() {
        val json = JSONObject(StateCodec.encode(state(Route.Relay)))
        json.getJSONArray("devices").getJSONObject(0).remove("lastSuccessfulRoute")
        val restored = StateCodec.decode(json.toString()).selected!!
        assertNull(restored.lastSuccessfulRoute)
        assertEquals(configured(), restored.profile)
        assertFalse(restored.connected)
    }

    @Test fun malformedOptionalHintDoesNotInvalidateSavedAuthorization() {
        for (value in listOf(JSONObject.NULL, "", "relay", "UnknownRoute", 42, true, JSONObject(), JSONArray())) {
            val json = JSONObject(StateCodec.encode(state(Route.Relay)))
            json.getJSONArray("devices").getJSONObject(0).put("lastSuccessfulRoute", value)
            val restored = StateCodec.decode(json.toString()).selected!!
            assertNull(restored.lastSuccessfulRoute)
            assertEquals(configured(), restored.profile)
            assertFalse(restored.connected)
        }
    }

    @Test fun unconfiguredOptionalRoutesCannotBecomeRestoredHints() {
        for (route in Route.entries) {
            val original = state(route).let { it.copy(devices = listOf(it.selected!!.copy(profile = profile()))) }
            val restored = StateCodec.decode(StateCodec.encode(original)).selected!!
            assertEquals(if (route == Route.Direct) Route.Direct else null, restored.lastSuccessfulRoute)
            assertEquals(profile(), restored.profile)
        }
    }

    @Test fun eachDeviceKeepsItsOwnRouteHintAndCredentials() {
        val first = state(Route.Relay).selected!!
        val secondProfile = configured().copy(deviceId = OTHER, deviceToken = "other-device-token")
        val second = DeviceState(secondProfile, status(deviceId = OTHER), connected = true,
            lastSuccessfulRoute = Route.Tailscale, lastLiveReadAtMillis = 456_000L)
        val restored = StateCodec.decode(StateCodec.encode(AppState(listOf(first, second), OTHER, loaded = true)))
        assertEquals(OTHER, restored.defaultDeviceId)
        assertEquals(Route.Relay, restored.devices.single { it.profile.deviceId == DEVICE }.lastSuccessfulRoute)
        assertEquals(Route.Tailscale, restored.selected!!.lastSuccessfulRoute)
        assertEquals(secondProfile, restored.selected!!.profile)
        assertTrue(restored.devices.all { !it.connected && it.lastLiveReadAtMillis == null })
    }

    @Test fun rememberedRelayDoesNotConfirmAnInterruptedOperation() {
        val original = state(Route.Relay).let { it.copy(devices = listOf(it.selected!!.copy(
            operation = operation(OperationOutcome.Pending), requiresRefresh = true,
        ))) }
        val restored = StateCodec.decode(StateCodec.encode(original)).selected!!
        val interrupted = requireNotNull(restored.operation)
        assertEquals(Route.Relay, restored.lastSuccessfulRoute)
        assertEquals(OperationOutcome.Unknown, interrupted.outcome)
        assertEquals(OPERATION, interrupted.operationId)
        assertEquals(TARGET, interrupted.targetId)
        assertEquals(Feedback.Unknown, restored.feedback)
        assertTrue(restored.requiresRefresh)
        assertFalse(restored.connected)
        assertNull(restored.lastLiveReadAtMillis)
    }
}
