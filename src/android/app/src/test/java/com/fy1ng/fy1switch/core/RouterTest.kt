package com.fy1ng.fy1switch.core

import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException
import javax.net.ssl.SSLHandshakeException

class RouterTest {
    @Test fun prefersDirectWithoutDuplicateSend() = runBlocking {
        val transport = FakeTransport()
        assertTrue(Router(transport, MutableClock()).send(profile(true), request()) is Delivery.Reply)
        assertEquals(listOf(Route.Direct), transport.attempts.map { it.route })
    }

    @Test fun timeoutFallbackRetainsExactBodyRequestAndOperationIds() = runBlocking {
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, operation = operation()))
        } }
        val result = Router(transport, MutableClock()).send(profile(true), request())
        assertEquals(Route.Relay, (result as Delivery.Reply).route)
        assertEquals(transport.attempts[0].body, transport.attempts[1].body)
        assertEquals(listOf(15000L, 12000L), transport.attempts.map { it.timeout })
    }

    @Test fun allTimeoutsAreUncertainNotSuccess() = runBlocking {
        val transport = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val result = Router(transport, MutableClock()).send(profile(true), request()) as Delivery.Failure
        assertTrue(result.mayHaveExecuted)
        assertEquals(Feedback.Offline, result.feedback)
        assertEquals(2, transport.attempts.size)
    }

    @Test fun expiredRequestIsNeverSent() = runBlocking {
        val transport = FakeTransport()
        val result = Router(transport, MutableClock()).send(profile(true), request(expires = NOW)) as Delivery.Failure
        assertFalse(result.mayHaveExecuted)
        assertEquals(Feedback.Expired, result.feedback)
        assertTrue(transport.attempts.isEmpty())
    }

    @Test fun noFallbackAfterExpiryEvenWhenFirstAttemptMayHaveExecuted() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            clock.now = NOW.plusSeconds(31)
            throw SocketTimeoutException()
        } }
        val result = Router(transport, clock).send(profile(true), request()) as Delivery.Failure
        assertTrue(result.mayHaveExecuted)
        assertEquals(Feedback.Expired, result.feedback)
        assertEquals(1, transport.attempts.size)
    }

    @Test fun rejectsExpiryBeyondProtocolLimit() = runBlocking {
        val transport = FakeTransport()
        assertEquals(Feedback.Expired, (Router(transport, MutableClock()).send(profile(), request(expires = NOW.plusSeconds(121))) as Delivery.Failure).feedback)
        assertTrue(transport.attempts.isEmpty())
    }

    @Test fun boundsTimeoutByRemainingLifetime() = runBlocking {
        val transport = FakeTransport()
        Router(transport, MutableClock()).send(profile(), request(expires = NOW.plusMillis(500)))
        assertEquals(500L, transport.attempts.single().timeout)
    }

    @Test fun certificateFailureNeverDowngradesToRelay() = runBlocking {
        val transport = FakeTransport().apply { handler = { throw SSLHandshakeException("fixture") } }
        assertEquals(Feedback.Security, (Router(transport, MutableClock()).send(profile(true), request()) as Delivery.Failure).feedback)
        assertEquals(1, transport.attempts.size)
    }

    @Test fun authenticationAndHttpErrorsAreNotRetried() = runBlocking {
        for (statusCode in listOf(401, 403, 400, 500, 302)) {
            val transport = FakeTransport().apply { handler = { HttpResult(statusCode, "untrusted") } }
            assertTrue(Router(transport, MutableClock()).send(profile(true), request()) is Delivery.Failure)
            assertEquals(1, transport.attempts.size)
        }
    }

    @Test fun cannotRouteUsingUnselectedDeviceId() = runBlocking {
        val transport = FakeTransport()
        assertEquals(Feedback.Protocol, (Router(transport, MutableClock()).send(profile(), request().copy(deviceId = OTHER)) as Delivery.Failure).feedback)
        assertTrue(transport.attempts.isEmpty())
    }

    @Test fun malformedReplyDoesNotFallBackOrConfirm() = runBlocking {
        val transport = FakeTransport().apply { handler = { HttpResult(200, "not json") } }
        assertEquals(Feedback.Protocol, (Router(transport, MutableClock()).send(profile(true), request()) as Delivery.Failure).feedback)
        assertEquals(1, transport.attempts.size)
    }
}
