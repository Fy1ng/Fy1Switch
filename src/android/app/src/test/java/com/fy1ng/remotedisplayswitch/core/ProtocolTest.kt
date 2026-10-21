package com.fy1ng.remotedisplayswitch.core

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ProtocolTest {
    @Test fun serializesExactCamelCaseAndStringEnums() {
        val json = JSONObject(Protocol.command(request()))
        assertEquals("Toggle", json.get("intent"))
        assertEquals("set", json.getString("command"))
        assertEquals(DEVICE.toString(), json.getString("deviceId"))
        assertEquals(OPERATION.toString(), json.getString("operationId"))
        assertEquals("2026-10-05T05:00:30Z", json.getString("expiresAt"))
        assertEquals(1, json.getInt("version"))
        assertFalse(json.has("DeviceId"))
    }

    @Test fun statusOmitsOptionalFields() {
        val json = JSONObject(Protocol.command(RemoteCommand(REQUEST, DEVICE, "status", NOW)))
        assertFalse(json.has("targetId"))
        assertFalse(json.has("intent"))
        assertFalse(json.has("operationId"))
    }

    @Test fun operationCarriesOriginalTargetAndOperation() {
        val json = JSONObject(Protocol.command(request("operation")))
        assertEquals(TARGET.toString(), json.getString("targetId"))
        assertEquals(OPERATION.toString(), json.getString("operationId"))
        assertFalse(json.has("intent"))
    }

    @Test fun missingSetTargetIsRejected() {
        assertThrows(IllegalArgumentException::class.java) { Protocol.command(request().copy(targetId = null)) }
        assertThrows(IllegalArgumentException::class.java) { Protocol.command(request().copy(operationId = null)) }
    }

    @Test fun parsesSharedOperationRecord() {
        val reply = Protocol.reply(replyJson(Protocol.command(request()), operation = operation(OperationOutcome.AcceptedUnconfirmed)), request())
        assertEquals(OperationOutcome.AcceptedUnconfirmed, reply.operation?.outcome)
        assertEquals(PowerIntent.Toggle, reply.operation?.requested)
        assertEquals(PowerIntent.Off, reply.operation?.resolved)
        assertEquals(DisplayPower.Off, reply.operation?.observation?.state)
    }

    @Test fun rejectsAnotherDeviceInBody() {
        assertThrows(IllegalArgumentException::class.java) {
            Protocol.reply(replyJson(Protocol.command(request()), status = status(deviceId = OTHER)), request())
        }
    }

    @Test fun rejectsRequestAndOperationIdentityConfusion() {
        val body = Protocol.command(request())
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(replyJson(body), request().copy(requestId = OTHER)) }
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(replyJson(body, operation = operation().copy(operationId = OTHER)), request()) }
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(replyJson(body, operation = operation().copy(targetId = OTHER)), request()) }
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(replyJson(body, operation = operation().copy(requested = PowerIntent.On)), request()) }
    }

    @Test fun rejectsWrongVersionAndNonBooleanSuccess() {
        val json = JSONObject(replyJson(Protocol.command(request())))
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(json.put("version", 2).toString(), request()) }
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(json.put("version", 1).put("success", "true").toString(), request()) }
    }

    @Test fun rejectsLowercaseAndNumericEnums() {
        val json = Protocol.observationJson(observation())
        assertThrows(IllegalArgumentException::class.java) { Protocol.observation(json.put("state", "on")) }
        assertThrows(Exception::class.java) { Protocol.observation(json.put("state", 1)) }
    }

    @Test fun ignoresCapabilitiesWithoutTrustingThem() {
        val json = JSONObject(replyJson(Protocol.command(request()), status = status()))
            .put("capabilities", JSONObject().put("allowAnyDevice", true))
        assertEquals(DEVICE, Protocol.reply(json.toString(), request()).status?.deviceId)
        json.getJSONObject("status").put("deviceId", OTHER.toString())
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(json.toString(), request()) }
    }

    @Test fun validatesPairBundleLifetimeAndIdentity() {
        assertEquals(DEVICE, Protocol.pairBundle(bundleJson(), NOW).deviceId)
        assertThrows(IllegalArgumentException::class.java) { Protocol.pairBundle(bundleJson(expiresAt = NOW), NOW) }
        assertThrows(IllegalArgumentException::class.java) { Protocol.pairBundle(bundleJson(expiresAt = NOW.plusSeconds(301)), NOW) }
        val response = JSONObject().put("deviceId", OTHER).put("clientId", CLIENT).put("token", "fixture-token")
        assertThrows(IllegalArgumentException::class.java) { Protocol.pairReply(response.toString(), DEVICE) }
    }

    @Test fun acceptsOnlySafeHttpsEndpoints() {
        assertEquals("https://192.0.2.1:8443", Protocol.endpoint("https://192.0.2.1:8443/"))
        assertEquals("https://[2001:db8::1]:8443", Protocol.endpoint("https://[2001:db8::1]:8443"))
        listOf("http://192.0.2.1", "https://user:password@example.com", "https://example.com/path", "https://example.com?token=x", "https://example.com#fragment", "https://example.com:0").forEach {
            assertThrows(IllegalArgumentException::class.java) { Protocol.endpoint(it) }
        }
    }

    @Test fun validatesPinAndRejectsHeaderInjection() {
        assertEquals(PIN, Protocol.pin(PIN))
        assertThrows(IllegalArgumentException::class.java) { Protocol.pin("sha256/abc") }
        assertThrows(IllegalArgumentException::class.java) { Protocol.token("abc\r\nX-Evil: yes") }
    }

    @Test fun sameHttpsEndpointIgnoresCaseDefaultPortAndRootSlashButNotOriginChanges() {
        assertTrue(Protocol.sameEndpoint("https://screlay.fy1ng.cn", "HTTPS://SCRELAY.FY1NG.CN:443/"))
        assertFalse(Protocol.sameEndpoint("https://screlay.fy1ng.cn", "https://other.example"))
        assertFalse(Protocol.sameEndpoint("https://screlay.fy1ng.cn", "https://screlay.fy1ng.cn:8443"))
        for (unsafe in listOf("http://screlay.fy1ng.cn", "https://user@screlay.fy1ng.cn", "https://screlay.fy1ng.cn/path")) {
            assertThrows(IllegalArgumentException::class.java) { Protocol.sameEndpoint("https://screlay.fy1ng.cn", unsafe) }
        }
    }

    @Test fun secretBearingModelsHaveRedactedStringRepresentations() {
        assertFalse(profile(true).toString().contains("fixture-device-token"))
        assertFalse(profile(true).relay.toString().contains("fixture-controller-token"))
        assertFalse(Protocol.pairBundle(bundleJson(), NOW).toString().contains("fixture-pair-code"))
        assertFalse(HttpResult(200, "secret").toString().contains("secret"))
    }
}
