package com.fy1ng.fy1switch.core

import com.fy1ng.fy1switch.ui.HomePolicy
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException

class ColdStartRoutingTest {
    private fun command(attempt: Attempt) = JSONObject(attempt.body).getString("command")

    @Test fun tileRecoveryAfterProcessRestartUsesAShortLanProbeBeforeRelay() = runBlocking {
        val clock = MutableClock()
        val saved = activeState(true).let { it.copy(devices = listOf(it.selected!!.copy(lastSuccessfulRoute = Route.Relay))) }
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct) {
                clock.now = clock.now.plusMillis(it.timeout)
                throw SocketTimeoutException()
            }
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val repository = DeviceRepository(MemoryStore(StateCodec.decode(StateCodec.encode(saved))), transport, clock)
        repository.initialize()
        assertFalse(repository.hasFreshPower(DEVICE))
        assertEquals(TilePower.Unavailable, StatePolicy.tile(HomePolicy.visibleState(repository.state.value, fresh = false)))
        assertEquals(Feedback.Updated, repository.recover(DEVICE))
        assertEquals(NOW.plusSeconds(1), clock.now)
        assertEquals(listOf(Route.Direct, Route.Relay), transport.attempts.map { it.route })
        assertEquals(listOf("status", "refresh"), transport.attempts.map(::command))
        assertEquals(listOf(1_000L, 12_000L), transport.attempts.map { it.timeout })
        assertTrue(repository.hasFreshPower(DEVICE))
        assertEquals(TilePower.Active, StatePolicy.tile(repository.state.value))
    }

    @Test fun returningToLanStillProbesAndRestoresDirectPriority() = runBlocking {
        val saved = activeState(true).let { it.copy(devices = listOf(it.selected!!.copy(lastSuccessfulRoute = Route.Relay))) }
        val store = MemoryStore(StateCodec.decode(StateCodec.encode(saved)))
        val transport = FakeTransport()
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Updated, repository.recover(DEVICE))
        assertEquals(listOf(Route.Direct, Route.Direct), transport.attempts.map { it.route })
        assertEquals(listOf("status", "refresh"), transport.attempts.map(::command))
        assertEquals(1_000L, transport.attempts.first().timeout)
        assertEquals(Route.Direct, store.value.selected!!.lastSuccessfulRoute)
        val restored = StateCodec.decode(StateCodec.encode(store.value))
        transport.attempts.clear()
        assertEquals(Feedback.Updated, DeviceRepository(MemoryStore(restored), transport, MutableClock()).recover(DEVICE))
        assertEquals(listOf("refresh"), transport.attempts.map(::command))
        assertEquals(Route.Direct, transport.attempts.single().route)
    }

    @Test fun savedRelayWithTailscaleBoundsBothEarlierPathsToReadOnlyProbes() = runBlocking {
        val clock = MutableClock()
        val saved = activeState(true).let { state -> state.copy(devices = listOf(state.selected!!.let {
            it.copy(profile = it.profile.copy(tailscaleEndpoint = "https://100.64.0.2:8443"), lastSuccessfulRoute = Route.Relay)
        })) }
        val transport = FakeTransport().apply { handler = {
            if (it.route != Route.Relay) {
                clock.now = clock.now.plusMillis(it.timeout)
                throw SocketTimeoutException()
            }
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val repository = DeviceRepository(MemoryStore(StateCodec.decode(StateCodec.encode(saved))), transport, clock)
        assertEquals(Feedback.Updated, repository.recover(DEVICE))
        assertEquals(NOW.plusSeconds(2), clock.now)
        assertEquals(listOf(Route.Direct, Route.Tailscale, Route.Relay), transport.attempts.map { it.route })
        assertEquals(listOf("status", "status", "refresh"), transport.attempts.map(::command))
        assertEquals(listOf(1_000L, 1_000L, 12_000L), transport.attempts.map { it.timeout })
    }

    @Test fun coldStartHintCannotBypassACertificateFailureOrWritePower() = runBlocking {
        val saved = activeState(true).let { it.copy(devices = listOf(it.selected!!.copy(lastSuccessfulRoute = Route.Relay))) }
        val transport = FakeTransport().apply { handler = { throw certificateFailure() } }
        val repository = DeviceRepository(MemoryStore(StateCodec.decode(StateCodec.encode(saved))), transport, MutableClock())
        assertEquals(Feedback.Security, repository.recover(DEVICE))
        assertEquals(listOf("status"), transport.attempts.map(::command))
        assertEquals(listOf(Route.Direct), transport.attempts.map { it.route })
        assertFalse(repository.hasFreshPower(DEVICE))
    }

    @Test fun routeHintDoesNotPermanentlySuppressASlowLanIfRelayIsOffline() = runBlocking {
        val clock = MutableClock()
        val saved = activeState(true).let { it.copy(devices = listOf(it.selected!!.copy(lastSuccessfulRoute = Route.Relay))) }
        val transport = FakeTransport().apply { handler = {
            val latency = if (it.route == Route.Direct) 1_500L else 20_000L
            clock.now = clock.now.plusMillis(minOf(it.timeout, latency))
            if (it.timeout < latency) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val repository = DeviceRepository(MemoryStore(StateCodec.decode(StateCodec.encode(saved))), transport, clock)
        assertEquals(Feedback.Offline, repository.recover(DEVICE))
        assertEquals(listOf("status", "refresh"), transport.attempts.map(::command))
        clock.now = clock.now.plusSeconds(5)
        transport.attempts.clear()
        assertEquals(Feedback.Updated, repository.recover(DEVICE))
        assertEquals(listOf(Route.Direct, Route.Direct), transport.attempts.map { it.route })
        assertEquals(listOf("status", "refresh"), transport.attempts.map(::command))
        assertEquals(Route.Direct, repository.state.value.selected!!.lastSuccessfulRoute)
    }

    @Test fun changingConnectionSettingsClearsHintButRenamingKeepsIt() = runBlocking {
        val saved = activeState(true).let { it.copy(devices = listOf(it.selected!!.copy(lastSuccessfulRoute = Route.Relay))) }
        val store = MemoryStore(StateCodec.decode(StateCodec.encode(saved)))
        val repository = DeviceRepository(store, FakeTransport(), MutableClock())
        assertEquals(Feedback.Saved, repository.configureConnection(DEVICE, "Renamed", "https://relay.example", "", true, true))
        assertEquals(Route.Relay, store.value.selected!!.lastSuccessfulRoute)
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Renamed", "https://replacement.example", "new-fixture-token", false))
        assertNull(store.value.selected!!.lastSuccessfulRoute)
    }
}
