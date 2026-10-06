package com.fy1ng.remotedisplayswitch.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException
import javax.net.ssl.SSLHandshakeException

class RecoveryProbeTest {
    private val device = profile(true).copy(tailscaleEndpoint = "https://100.64.0.2:8443")
    private fun command(attempt: Attempt) = JSONObject(attempt.body).getString("command")

    @Test fun failuresStayCooledUntilAfterWholeRoundAndOnlyReadProbesPrecedeRelayWrite() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            clock.now = clock.now.plusMillis(it.timeout)
            if (it.route != Route.Relay) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, operation = operation()))
        } }
        val router = Router(transport, clock)
        router.send(device, request())
        assertEquals(NOW.plusSeconds(24), clock.now)
        transport.attempts.clear()
        transport.handler = { HttpResult(200, replyJson(it.body, operation = operation())) }
        router.send(device, request(expires = clock.now.plusSeconds(30)))
        assertEquals(listOf(Route.Relay), transport.attempts.map { it.route })
        clock.now = clock.now.plusSeconds(5)
        transport.attempts.clear()
        transport.handler = {
            if (it.route != Route.Relay) {
                assertEquals("status", command(it))
                clock.now = clock.now.plusMillis(it.timeout)
                throw SocketTimeoutException()
            }
            HttpResult(200, replyJson(it.body, operation = operation()))
        }
        val write = request(expires = clock.now.plusSeconds(30))
        assertEquals(Route.Relay, (router.send(device, write) as Delivery.Reply).route)
        assertEquals(listOf("status", "status", "set"), transport.attempts.map(::command))
        assertEquals(listOf(1000L, 1000L, 12000L), transport.attempts.map { it.timeout })
        assertEquals(Protocol.command(write), transport.attempts.last().body)
        for (attempt in transport.attempts.take(2)) {
            val probe = JSONObject(attempt.body)
            assertFalse(probe.has("operationId"))
            assertFalse(probe.has("intent"))
            assertFalse(probe.has("targetId"))
            assertNotEquals(write.requestId.toString(), probe.getString("requestId"))
            assertEquals(write.expiresAt.toString(), probe.getString("expiresAt"))
        }
        transport.attempts.clear()
        router.send(device, request(expires = clock.now.plusSeconds(30)))
        assertEquals(listOf(Route.Relay), transport.attempts.map { it.route })
    }

    @Test fun successfulProbeRestoresPriorityAndUsesOriginalWriteOnlyAfterStatus() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        router.send(device, request())
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        assertEquals(Route.Direct, (router.send(device, request()) as Delivery.Reply).route)
        assertEquals(listOf("status", "set"), transport.attempts.map(::command))
        assertEquals(Protocol.command(request()), transport.attempts.last().body)
        transport.attempts.clear()
        router.send(device, request())
        assertEquals(listOf("set"), transport.attempts.map(::command))
    }

    @Test fun shortDeadlineSkipsProbeAndReservesAllTimeForHealthyRelay() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            if (it.route != Route.Relay) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        router.send(device, request())
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        router.send(device, request(expires = clock.now.plusMillis(500)))
        assertEquals(listOf(Route.Relay), transport.attempts.map { it.route })
        assertEquals(500L, transport.attempts.single().timeout)
    }

    @Test fun recoveredPathWriteTimeoutStillLeavesHealthyRelayBudget() = runBlocking {
        val clock = MutableClock()
        val directRelay = profile(true)
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        router.send(directRelay, request())
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        transport.handler = {
            clock.now = clock.now.plusMillis(it.timeout)
            if (it.route == Route.Direct && command(it) == "set") throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        }
        assertEquals(Route.Relay, (router.send(directRelay, request(expires = clock.now.plusSeconds(14))) as Delivery.Reply).route)
        assertEquals(listOf(1000L, 1000L, 12000L), transport.attempts.map { it.timeout })
        assertEquals(listOf("status", "set", "set"), transport.attempts.map(::command))
        assertEquals(transport.attempts[1].body, transport.attempts[2].body)
    }

    @Test fun probeSecurityIdentityAndProtocolFailuresStopWithoutAnyWrite() = runBlocking {
        for (failure in listOf("tls", "identity", "request", "missing-status", "unsuccessful", "401", "403", "500")) {
            val clock = MutableClock()
            val transport = FakeTransport().apply { handler = {
                if (it.route == Route.Direct) throw SocketTimeoutException()
                HttpResult(200, replyJson(it.body, status = status()))
            } }
            val router = Router(transport, clock)
            router.send(device, request())
            clock.now = NOW.plusSeconds(5)
            transport.attempts.clear()
            transport.handler = {
                when (failure) {
                    "tls" -> throw SSLHandshakeException("fixture")
                    "identity" -> HttpResult(200, replyJson(it.body, status = status(deviceId = OTHER)))
                    "request" -> HttpResult(200, JSONObject(replyJson(it.body, status = status())).put("requestId", OTHER.toString()).toString())
                    "missing-status" -> HttpResult(200, replyJson(it.body))
                    "unsuccessful" -> HttpResult(200, replyJson(it.body, status = status(), success = false))
                    else -> HttpResult(failure.toInt(), "untrusted")
                }
            }
            val result = router.send(device, request()) as Delivery.Failure
            assertFalse(result.mayHaveExecuted)
            assertEquals(listOf("status"), transport.attempts.map(::command))
            assertEquals(listOf(Route.Direct), transport.attempts.map { it.route })
        }
    }

    @Test fun cancellationDuringProbeDoesNotSendWriteOrFallback() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        router.send(device, request())
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        transport.handler = { throw CancellationException() }
        try {
            router.send(device, request())
            fail("Cancellation must propagate")
        } catch (_: CancellationException) {
            assertEquals(listOf("status"), transport.attempts.map(::command))
        }
    }
}
