package com.fy1ng.fy1switch.network

import org.junit.Assert.*
import org.junit.Test

class WolHttpsTransportTest {
    @Test fun credentialsAreOnlyInAuthorizationHeaderAndCheckHasNoMacOrShell() {
        val token = "A".repeat(43)
        val transport = WolHttpsTransport()
        val request = transport.request("https://WOL.EXAMPLE:443/", "/v1/wol/devices", "GET", token, null)
        assertEquals("https://wol.example/v1/wol/devices", request.url.toString())
        assertEquals("Bearer $token", request.header("Authorization"))
        assertNull(request.header("X-Device-Token")); assertFalse(request.url.toString().contains(token))
        assertThrows(IllegalArgumentException::class.java) { transport.request("http://wol.example", "/v1/wol/devices", "GET", token, null) }
        assertThrows(IllegalArgumentException::class.java) { transport.request("https://wol.example", "/v1/wol/devices?token=x", "GET", token, null) }
    }
}
