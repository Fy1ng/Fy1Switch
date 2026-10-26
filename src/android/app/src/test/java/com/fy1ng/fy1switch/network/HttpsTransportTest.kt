package com.fy1ng.fy1switch.network

import com.fy1ng.fy1switch.core.DEVICE
import com.fy1ng.fy1switch.core.CLIENT
import com.fy1ng.fy1switch.core.PIN
import com.fy1ng.fy1switch.core.Protocol
import com.fy1ng.fy1switch.core.Route
import com.fy1ng.fy1switch.core.profile
import com.fy1ng.fy1switch.core.request
import okio.Buffer
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.*
import org.junit.Test
import org.json.JSONObject

class HttpsTransportTest {
    @Test fun relayFirstPairingSendsOnlyOneTimeCodeAndPhoneName() {
        val transport = HttpsTransport()
        val request = transport.relayPairRequest("https://relay.example/", "000042", "Phone")
        assertEquals("https://relay.example/v1/relay/pair", request.url.toString())
        assertNull(request.header("Authorization"))
        assertNull(request.header("X-Device-Token"))
        val buffer = Buffer()
        request.body!!.writeTo(buffer)
        val body = JSONObject(buffer.readUtf8())
        assertEquals("000042", body.getString("code"))
        assertEquals("Phone", body.getString("clientName"))
        assertEquals(2, body.length())
    }

    @Test fun existingPhoneAuthorizationNeverSendsOldRelayCredentialToNewOrigin() {
        val transport = HttpsTransport()
        val request = transport.relayAuthorizationRequest(profile(true), "https://new.example")
        assertEquals("https://new.example/v1/devices/$DEVICE/authorize", request.url.toString())
        assertNull(request.header("Authorization"))
        assertEquals("fixture-device-token", request.header("X-Device-Token"))
        val buffer = Buffer()
        request.body!!.writeTo(buffer)
        val body = JSONObject(buffer.readUtf8())
        assertEquals(CLIENT.toString(), body.getString("clientId"))
        assertEquals(1, body.length())
    }

    @Test fun automaticRelayLearningUsesWindowsPinnedOriginAndOnlyDeviceBearer() {
        val transport = HttpsTransport()
        val device = profile(true)
        val request = transport.relayLearningRequest(device)
        assertEquals(device.endpoint + "/v1/relay", request.url.toString())
        assertEquals("Bearer fixture-device-token", request.header("Authorization"))
        assertNull(request.header("X-Device-Token"))
        val buffer = Buffer()
        request.body!!.writeTo(buffer)
        assertEquals(CLIENT.toString(), JSONObject(buffer.readUtf8()).getString("clientId"))
        val pinned = transport.controlClient(device, Route.Direct)
        val relay = transport.controlClient(device, Route.Relay)
        assertNotSame(pinned.sslSocketFactory, relay.sslSocketFactory)
    }

    @Test fun sameTlsOriginReusesClientAcrossRefreshesAndCredentialChanges() {
        val transport = HttpsTransport()
        val device = profile()
        val first = transport.controlClient(device, Route.Direct)
        assertSame(first, transport.controlClient(device, Route.Direct))
        val changedToken = device.copy(deviceToken = "replacement-fixture-token", endpoint = device.endpoint + "/")
        assertSame(first, transport.controlClient(changedToken, Route.Direct))
        assertEquals("Bearer replacement-fixture-token", transport.controlRequest(changedToken, Route.Direct, "{}").header("Authorization"))
        assertSame(first, transport.controlClient(device.copy(tailscaleEndpoint = device.endpoint), Route.Tailscale))
    }

    @Test fun originOrPinChangesRequireSeparateTlsClients() {
        val transport = HttpsTransport()
        val device = profile()
        val first = transport.controlClient(device, Route.Direct)
        val otherOrigin = transport.controlClient(device.copy(endpoint = "https://192.0.2.2:8443"), Route.Direct)
        val otherPin = transport.controlClient(device.copy(certificatePin = "sha256/AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="), Route.Direct)
        assertNotSame(first, otherOrigin)
        assertNotSame(first.sslSocketFactory, otherOrigin.sslSocketFactory)
        assertNotSame(first.sslSocketFactory, otherPin.sslSocketFactory)
        assertSame(first, transport.controlClient(device.copy(certificatePin = PIN), Route.Direct))
    }

    @Test fun tlsClientCacheIsBoundedAndKeepsRecentlyUsedOrigins() {
        val transport = HttpsTransport()
        val device = profile()
        val first = transport.controlClient(device, Route.Direct)
        val oldest = device.copy(endpoint = "https://192.0.2.2:8443")
        val evicted = transport.controlClient(oldest, Route.Direct)
        for (index in 3..64) transport.controlClient(device.copy(endpoint = "https://192.0.2.$index:8443"), Route.Direct)
        assertSame(first, transport.controlClient(device, Route.Direct))
        transport.controlClient(device.copy(endpoint = "https://192.0.2.65:8443"), Route.Direct)
        assertSame(first, transport.controlClient(device, Route.Direct))
        assertNotSame(evicted, transport.controlClient(oldest, Route.Direct))
    }

    @Test fun errorHeadersArePreservedEvenWithOversizedBody() {
        val transport = HttpsTransport()
        val request = transport.controlRequest(profile(), Route.Direct, "{}")
        for (code in listOf(401, 403, 500, 302)) {
            val response = Response.Builder().request(request).protocol(okhttp3.Protocol.HTTP_1_1)
                .code(code).message("fixture").body("x".repeat(Protocol.MAX_BODY + 1).toResponseBody()).build()
            assertEquals(code, transport.readResponse(response).status)
        }
        val oversized = Response.Builder().request(request).protocol(okhttp3.Protocol.HTTP_1_1)
            .code(200).message("fixture").body("x".repeat(Protocol.MAX_BODY + 1).toResponseBody()).build()
        assertThrows(IllegalArgumentException::class.java) { transport.readResponse(oversized) }
    }

    @Test fun tailscaleUsesPinnedDeviceTlsAndBearerWithoutRelayCredentials() {
        val transport = HttpsTransport()
        val device = profile(true).copy(tailscaleEndpoint = "https://100.64.0.2:8443")
        val body = Protocol.command(request())
        val http = transport.controlRequest(device, Route.Tailscale, body)
        assertEquals("https://100.64.0.2:8443/v1/control", http.url.toString())
        assertEquals("Bearer fixture-device-token", http.header("Authorization"))
        assertNull(http.header("X-Device-Token"))
        val buffer = Buffer()
        http.body!!.writeTo(buffer)
        assertEquals(body, buffer.readUtf8())
        val client = transport.controlClient(device, Route.Tailscale)
        assertNotSame(client.sslSocketFactory, transport.controlClient(device, Route.Relay).sslSocketFactory)
        assertFalse(client.followRedirects)
        assertFalse(client.followSslRedirects)
        assertFalse(client.retryOnConnectionFailure)
    }

    @Test fun directSendsOnlyDeviceBearer() {
        val transport = HttpsTransport()
        val http = transport.controlRequest(profile(true), Route.Direct, Protocol.command(request()))
        assertEquals("https://192.0.2.1:8443/v1/control", http.url.toString())
        assertEquals("Bearer fixture-device-token", http.header("Authorization"))
        assertNull(http.header("X-Device-Token"))
        assertEquals("POST", http.method)
    }

    @Test fun relaySendsSeparateControllerBearerAndDeviceHeaderWithoutChangingBody() {
        val transport = HttpsTransport()
        val body = Protocol.command(request())
        val http = transport.controlRequest(profile(true), Route.Relay, body)
        assertEquals("https://relay.example/v1/devices/" + DEVICE + "/control", http.url.toString())
        assertEquals("Bearer fixture-controller-token", http.header("Authorization"))
        assertEquals("fixture-device-token", http.header("X-Device-Token"))
        val buffer = Buffer()
        http.body!!.writeTo(buffer)
        assertEquals(body, buffer.readUtf8())
    }

    @Test fun relayUsesSystemTlsAndNeitherRouteFollowsRedirectsOrAutomaticallyRetries() {
        val transport = HttpsTransport()
        val direct = transport.controlClient(profile(true), Route.Direct)
        val relay = transport.controlClient(profile(true), Route.Relay)
        assertNotSame(direct.sslSocketFactory, relay.sslSocketFactory)
        assertNotSame(direct.hostnameVerifier, relay.hostnameVerifier)
        assertSame(relay.sslSocketFactory, transport.controlClient(profile(), Route.Relay).sslSocketFactory)
        listOf(direct, relay).forEach {
            assertFalse(it.followRedirects)
            assertFalse(it.followSslRedirects)
            assertFalse(it.retryOnConnectionFailure)
        }
    }
}
