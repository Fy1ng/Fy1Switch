package com.fy1ng.fy1switch.core

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

    private fun simulateLatency(clock: MutableClock, attempt: Attempt, latencyMillis: Long = 1_500L) {
        clock.now = clock.now.plusMillis(minOf(latencyMillis, attempt.timeout))
        if (attempt.timeout < latencyMillis) throw SocketTimeoutException()
    }

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

    @Test fun tlsConnectionFailureDuringRecoveryProbeStillReachesRelay() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            if (it.route != Route.Relay) throw SSLHandshakeException("Peer closed the handshake")
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        assertEquals(Route.Relay, (router.send(device, request(command = "status")) as Delivery.Reply).route)
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        val write = request(expires = clock.now.plusSeconds(30))
        assertEquals(Route.Relay, (router.send(device, write) as Delivery.Reply).route)
        assertEquals(listOf(Route.Direct, Route.Tailscale, Route.Relay), transport.attempts.map { it.route })
        assertEquals(listOf("status", "status", "set"), transport.attempts.map(::command))
        assertEquals(listOf(1000L, 1000L, 12000L), transport.attempts.map { it.timeout })
        assertEquals(Protocol.command(write), transport.attempts.last().body)
    }

    @Test fun singleRouteRecoversOverSlowLinkWithinOriginalDeadline() = runBlocking {
        for ((commandName, lifetimeMillis) in listOf("status" to 3_200L, "set" to 3_200L, "set" to 30_000L)) {
            val clock = MutableClock()
            val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
            val router = Router(transport, clock)
            val directOnly = profile()
            router.send(directOnly, request(command = "status"))
            clock.now = NOW.plusSeconds(5)
            transport.attempts.clear()
            val startedAt = clock.now
            val original = request(command = commandName, expires = startedAt.plusMillis(lifetimeMillis))
            transport.handler = { attempt ->
                assertFalse(clock.now.plusMillis(attempt.timeout).isAfter(original.expiresAt))
                simulateLatency(clock, attempt)
                HttpResult(200, replyJson(attempt.body, status = status()))
            }
            assertEquals(Route.Direct, (router.send(directOnly, original) as Delivery.Reply).route)
            assertEquals(startedAt.plusMillis(if (commandName == "status") 1_500 else 3_000), clock.now)
            assertEquals(if (commandName == "status") listOf("status") else listOf("status", commandName), transport.attempts.map(::command))
            assertEquals(Protocol.command(original), transport.attempts.last().body)
            val probe = JSONObject(transport.attempts.first().body)
            assertFalse(probe.has("operationId"))
            assertFalse(probe.has("intent"))
            if (commandName == "status") assertEquals(original.targetId.toString(), probe.getString("targetId"))
            else assertFalse(probe.has("targetId"))
            if (commandName == "status") assertEquals(original.requestId.toString(), probe.getString("requestId"))
            else assertNotEquals(original.requestId.toString(), probe.getString("requestId"))
            assertEquals(original.expiresAt.toString(), probe.getString("expiresAt"))
            transport.attempts.clear()
            transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
            router.send(directOnly, request(expires = clock.now.plusSeconds(30)))
            assertEquals(listOf("set"), transport.attempts.map(::command))
        }
    }

    @Test fun recoveringStatusUsesOriginalRequestOnceAndRestoresRoutePriority() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        router.send(device, request(command = "status"))
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        val original = request(command = "status", expires = clock.now.plusSeconds(30))
        assertEquals(Route.Direct, (router.send(device, original) as Delivery.Reply).route)
        assertEquals(Protocol.command(original), transport.attempts.single().body)
        assertEquals(1000L, transport.attempts.single().timeout)
        transport.attempts.clear()
        router.send(device, request(expires = clock.now.plusSeconds(30)))
        assertEquals(listOf("set"), transport.attempts.map(::command))
    }

    @Test fun liveRefreshRecoveryStillRequiresHardwareReadAfterStatusProbe() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        router.send(device, request(command = "refresh"))
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        val original = request(command = "refresh", expires = clock.now.plusSeconds(30))
        assertEquals(Route.Direct, (router.send(device, original) as Delivery.Reply).route)
        assertEquals(listOf("status", "refresh"), transport.attempts.map(::command))
        assertEquals(Protocol.command(original), transport.attempts.last().body)
        assertEquals(listOf(1000L, 6000L), transport.attempts.map { it.timeout })
    }

    @Test fun recoveringStatusCanUseWholeShortDeadlineWithoutReservingAnotherRequest() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val router = Router(transport, clock)
        router.send(profile(), request(command = "status"))
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        val original = request(command = "status", expires = clock.now.plusMillis(500))
        assertEquals(Route.Direct, (router.send(profile(), original) as Delivery.Reply).route)
        assertEquals(500L, transport.attempts.single().timeout)
        assertEquals(Protocol.command(original), transport.attempts.single().body)
    }

    @Test fun recoveringStatusValidationFailuresDoNotFallBack() = runBlocking {
        for (failure in listOf("tls", "identity", "missing-status", "unsuccessful")) {
            val clock = MutableClock()
            val transport = FakeTransport().apply { handler = {
                if (it.route == Route.Direct) throw SocketTimeoutException()
                HttpResult(200, replyJson(it.body, status = status()))
            } }
            val router = Router(transport, clock)
            router.send(device, request(command = "status"))
            clock.now = NOW.plusSeconds(5)
            transport.attempts.clear()
            transport.handler = {
                when (failure) {
                    "tls" -> throw certificateFailure()
                    "identity" -> HttpResult(200, replyJson(it.body, status = status(deviceId = OTHER)))
                    "missing-status" -> HttpResult(200, replyJson(it.body))
                    else -> HttpResult(200, replyJson(it.body, status = status(), success = false))
                }
            }
            val result = router.send(device, request(command = "status", expires = clock.now.plusSeconds(30))) as Delivery.Failure
            assertEquals(when (failure) {
                "tls" -> Feedback.Security
                "unsuccessful" -> Feedback.Failed
                else -> Feedback.Protocol
            }, result.feedback)
            assertEquals(listOf(Route.Direct), transport.attempts.map { it.route })
        }
    }

    @Test fun allUnhealthyRoutesCanRecoverThroughSlowReadOnlyProbeAfterCooldown() = runBlocking {
        val routes = listOf(Route.Direct, Route.Tailscale, Route.Relay)
        for (recoveredRoute in routes) {
            val clock = MutableClock()
            val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
            val router = Router(transport, clock)
            router.send(device, request(command = "status"))
            transport.attempts.clear()
            val cooled = router.send(device, request()) as Delivery.Failure
            assertEquals(Feedback.Offline, cooled.feedback)
            assertFalse(cooled.mayHaveExecuted)
            assertTrue(transport.attempts.isEmpty())
            clock.now = NOW.plusSeconds(5)
            val startedAt = clock.now
            val write = request(expires = startedAt.plusSeconds(30))
            transport.handler = { attempt ->
                assertFalse(clock.now.plusMillis(attempt.timeout).isAfter(write.expiresAt))
                if (attempt.route != recoveredRoute) {
                    clock.now = clock.now.plusMillis(attempt.timeout)
                    throw SocketTimeoutException()
                }
                simulateLatency(clock, attempt)
                HttpResult(200, replyJson(attempt.body, status = status()))
            }
            assertEquals(recoveredRoute, (router.send(device, write) as Delivery.Reply).route)
            val precedingRoutes = routes.takeWhile { it != recoveredRoute }
            assertEquals(precedingRoutes + listOf(recoveredRoute, recoveredRoute), transport.attempts.map { it.route })
            assertEquals(List(precedingRoutes.size + 1) { "status" } + "set", transport.attempts.map(::command))
            assertEquals(startedAt.plusMillis(precedingRoutes.size * 6_000L + 3_000L), clock.now)
            assertEquals(Protocol.command(write), transport.attempts.last().body)
            assertTrue(clock.now.isBefore(write.expiresAt))
        }
    }

    @Test fun slowRecoveredPathsStillYieldToHealthyFallbackAfterShortProbes() = runBlocking {
        val routes = listOf(Route.Direct, Route.Tailscale, Route.Relay)
        for (healthyRoute in listOf(Route.Tailscale, Route.Relay)) {
            val clock = MutableClock()
            val transport = FakeTransport().apply { handler = {
                if (it.route != healthyRoute) throw SocketTimeoutException()
                HttpResult(200, replyJson(it.body, status = status()))
            } }
            val router = Router(transport, clock)
            router.send(device, request(command = "status"))
            clock.now = NOW.plusSeconds(5)
            transport.attempts.clear()
            val startedAt = clock.now
            val write = request(expires = startedAt.plusSeconds(30))
            transport.handler = { attempt ->
                simulateLatency(clock, attempt, if (attempt.route == healthyRoute) 100L else 1_500L)
                HttpResult(200, replyJson(attempt.body, status = status()))
            }
            assertEquals(healthyRoute, (router.send(device, write) as Delivery.Reply).route)
            val precedingRoutes = routes.takeWhile { it != healthyRoute }
            assertEquals(precedingRoutes + healthyRoute, transport.attempts.map { it.route })
            assertEquals(List(precedingRoutes.size) { "status" } + "set", transport.attempts.map(::command))
            assertEquals(startedAt.plusMillis(precedingRoutes.size * 1_000L + 100L), clock.now)
            assertEquals(Protocol.command(write), transport.attempts.last().body)
        }
    }

    @Test fun allUnhealthyProbeTimeoutsStayBoundedWithoutWritesOrImmediateRetries() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val router = Router(transport, clock)
        router.send(device, request(command = "status"))
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        val startedAt = clock.now
        val write = request(expires = startedAt.plusSeconds(30))
        transport.handler = { attempt ->
            assertFalse(clock.now.plusMillis(attempt.timeout).isAfter(write.expiresAt))
            clock.now = clock.now.plusMillis(attempt.timeout)
            throw SocketTimeoutException()
        }
        val result = router.send(device, write) as Delivery.Failure
        assertEquals(Feedback.Offline, result.feedback)
        assertFalse(result.mayHaveExecuted)
        assertEquals(listOf(Route.Direct, Route.Tailscale, Route.Relay), transport.attempts.map { it.route })
        assertEquals(listOf("status", "status", "status"), transport.attempts.map(::command))
        assertEquals(startedAt.plusSeconds(24), clock.now)
        transport.attempts.clear()
        router.send(device, request(expires = clock.now.plusSeconds(30)))
        assertTrue(transport.attempts.isEmpty())
    }

    @Test fun shortDeadlineWithoutHealthyFallbackDoesNotSendWriteAfterSlowProbeTimeout() = runBlocking {
        for (lifetimeMillis in listOf(500L, 1_000L, 2_000L)) {
            val clock = MutableClock()
            val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
            val router = Router(transport, clock)
            val directOnly = profile()
            router.send(directOnly, request(command = "status"))
            clock.now = NOW.plusSeconds(5)
            transport.attempts.clear()
            val startedAt = clock.now
            val write = request(expires = startedAt.plusMillis(lifetimeMillis))
            transport.handler = { attempt ->
                assertFalse(clock.now.plusMillis(attempt.timeout).isAfter(write.expiresAt))
                simulateLatency(clock, attempt)
                HttpResult(200, replyJson(attempt.body, status = status()))
            }
            val result = router.send(directOnly, write) as Delivery.Failure
            assertEquals(Feedback.Offline, result.feedback)
            assertFalse(result.mayHaveExecuted)
            val expectedCommands = if (lifetimeMillis > 1_000L) listOf("status") else emptyList()
            assertEquals(expectedCommands, transport.attempts.map(::command))
            assertTrue(clock.now.isBefore(write.expiresAt))
        }
    }

    @Test fun expiryDuringRecoveryProbePreventsOriginalWrite() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val router = Router(transport, clock)
        val directOnly = profile()
        router.send(directOnly, request(command = "status"))
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        val write = request(expires = clock.now.plusSeconds(3))
        transport.handler = {
            clock.now = write.expiresAt
            HttpResult(200, replyJson(it.body, status = status()))
        }
        val result = router.send(directOnly, write) as Delivery.Failure
        assertEquals(Feedback.Expired, result.feedback)
        assertFalse(result.mayHaveExecuted)
        assertEquals(listOf("status"), transport.attempts.map(::command))
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
        for (allUnhealthy in listOf(false, true)) {
            for (failure in listOf("tls", "identity", "request", "missing-status", "unsuccessful", "401", "403", "500")) {
                val clock = MutableClock()
                val transport = FakeTransport().apply { handler = {
                    if (allUnhealthy || it.route == Route.Direct) throw SocketTimeoutException()
                    HttpResult(200, replyJson(it.body, status = status()))
                } }
                val router = Router(transport, clock)
                router.send(device, request(command = "status"))
                clock.now = NOW.plusSeconds(5)
                transport.attempts.clear()
                transport.handler = {
                    if (allUnhealthy) simulateLatency(clock, it)
                    when (failure) {
                        "tls" -> throw certificateFailure()
                        "identity" -> HttpResult(200, replyJson(it.body, status = status(deviceId = OTHER)))
                        "request" -> HttpResult(200, JSONObject(replyJson(it.body, status = status())).put("requestId", OTHER.toString()).toString())
                        "missing-status" -> HttpResult(200, replyJson(it.body))
                        "unsuccessful" -> HttpResult(200, replyJson(it.body, status = status(), success = false))
                        else -> HttpResult(failure.toInt(), "untrusted")
                    }
                }
                val result = router.send(device, request()) as Delivery.Failure
                val expectedFeedback = when (failure) {
                    "tls" -> Feedback.Security
                    "identity", "request", "missing-status" -> Feedback.Protocol
                    "401", "403" -> Feedback.Unauthorized
                    else -> Feedback.Failed
                }
                assertEquals(expectedFeedback, result.feedback)
                assertFalse(result.mayHaveExecuted)
                assertEquals(listOf("status"), transport.attempts.map(::command))
                assertEquals(listOf(Route.Direct), transport.attempts.map { it.route })
            }
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
