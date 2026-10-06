package com.fy1ng.remotedisplayswitch.network

import com.fy1ng.remotedisplayswitch.core.DEVICE
import com.fy1ng.remotedisplayswitch.core.Protocol
import com.fy1ng.remotedisplayswitch.core.Route
import com.fy1ng.remotedisplayswitch.core.profile
import com.fy1ng.remotedisplayswitch.core.request
import okio.Buffer
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.*
import org.junit.Test

class HttpsTransportTest {
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
