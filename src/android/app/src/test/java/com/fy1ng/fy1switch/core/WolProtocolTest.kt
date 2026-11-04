package com.fy1ng.fy1switch.core

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class WolProtocolTest {
    @Test fun validatesHttpsOriginWithoutCredentialsPathsOrQuery() {
        assertEquals(WOL_ENDPOINT, WolProtocol.endpoint("https://WOL.EXAMPLE:443/"))
        for (address in listOf("http://wol.example", "https://token@wol.example", "https://wol.example/path", "https://wol.example/?token=x", "https://wol.example/#x")) {
            assertThrows(IllegalArgumentException::class.java) { WolProtocol.endpoint(address) }
        }
    }
    @Test fun strictVersionAndOperationIdentityAreRequired() {
        for (json in listOf(JSONObject(wolReply()).put("version", "1"), JSONObject(wolReply()).put("operationId", OTHER), JSONObject(wolReply()).put("deviceId", OTHER), JSONObject(wolReply()).put("status", "computer_on"))) {
            assertThrows(IllegalArgumentException::class.java) { WolProtocol.operation(json.toString(), wolOperation()) }
        }
    }
    @Test fun wakeEnvelopeHasIndependentIdsAndBoundedLifetime() {
        val request = JSONObject(WolProtocol.request(DEVICE, NOW, NOW.plusSeconds(30), OPERATION))
        assertEquals(DEVICE.toString(), request.getString("deviceId"))
        assertEquals(OPERATION.toString(), request.getString("operationId"))
        assertNotEquals(OPERATION.toString(), request.getString("requestId"))
        assertFalse(request.has("mac")); assertFalse(request.has("command")); assertFalse(request.has("accessToken"))
        assertThrows(IllegalArgumentException::class.java) { WolProtocol.request(DEVICE, NOW, NOW.plusSeconds(121)) }
    }
    @Test fun registrationTokensAreStrictAndNeverPrinted() {
        assertEquals(WOL_TOKEN, WolProtocol.token(WOL_TOKEN))
        for (token in listOf("short", "A".repeat(42), "A".repeat(42) + "=", "A".repeat(42) + "\n")) {
            assertThrows(IllegalArgumentException::class.java) { WolProtocol.token(token) }
        }
        assertFalse(wolAuth().toString().contains(WOL_TOKEN))
    }
    @Test fun sentPacketIsNotHostOnlineOrMonitorOn() {
        val text = WolPolicy.result(wolOperation(), false, NOW)
        assertTrue(text.contains("唤醒包已发送")); assertFalse(text.contains("已上线")); assertFalse(text.contains("显示器已开启"))
        val timeout = WolPolicy.result(wolOperation(), false, NOW.plusSeconds(91))
        assertTrue(timeout.contains("尚未确认"))
    }
    @Test fun deviceListMustMatchRegisteredClient() {
        val response = JSONObject().put("version", 1).put("clientId", OTHER).put("devices", wolDevices()).toString()
        assertThrows(IllegalArgumentException::class.java) { WolProtocol.devices(response, wolAuth()) }
    }
}
