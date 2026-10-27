package com.fy1ng.fy1switch.core

import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException

class TailscaleTest {
    private val endpoint = "https://100.64.0.2:8443"
    private fun device() = profile(true).copy(tailscaleEndpoint = endpoint)

    @Test fun directRemainsFirstAndAllFailedPathsRecoverAfterCooldown() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport()
        val router = Router(transport, clock)
        assertEquals(Route.Direct, (router.send(device(), request()) as Delivery.Reply).route)
        assertEquals(1, transport.attempts.size)
        transport.handler = { throw SocketTimeoutException() }
        router.send(device(), request())
        transport.attempts.clear()
        router.send(device(), request())
        assertTrue(transport.attempts.isEmpty())
        clock.now = NOW.plusSeconds(5)
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        transport.attempts.clear()
        assertEquals(Route.Direct, (router.send(device(), request()) as Delivery.Reply).route)
        assertEquals(listOf(Route.Direct, Route.Direct), transport.attempts.map { it.route })
    }

    @Test fun threeRoutesShareOneBodyAndDeadlineWithBoundedAttempts() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            if (it.route != Route.Relay) {
                clock.now = clock.now.plusMillis(it.timeout)
                throw SocketTimeoutException()
            }
            HttpResult(200, replyJson(it.body, operation = operation()))
        } }
        val result = Router(transport, clock).send(device(), request()) as Delivery.Reply
        assertEquals(Route.Relay, result.route)
        assertEquals(listOf(Route.Direct, Route.Tailscale, Route.Relay), transport.attempts.map { it.route })
        assertEquals(listOf(6000L, 6000L, 12000L), transport.attempts.map { it.timeout })
        assertEquals(1, transport.attempts.map { it.body }.distinct().size)
        assertEquals(Protocol.command(request()), transport.attempts.first().body)
    }

    @Test fun tailscaleSuccessStopsFallbackAndCooldownRestoresDirect() = runBlocking {
        val clock = MutableClock()
        var directAvailable = false
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct && !directAvailable) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        assertEquals(Route.Tailscale, (router.send(device(), request()) as Delivery.Reply).route)
        directAvailable = true
        transport.attempts.clear()
        router.send(device(), request())
        assertEquals(listOf(Route.Tailscale), transport.attempts.map { it.route })
        clock.now = NOW.plusSeconds(5)
        transport.attempts.clear()
        assertEquals(Route.Direct, (router.send(device(), request()) as Delivery.Reply).route)
        assertEquals(listOf(Route.Direct, Route.Direct), transport.attempts.map { it.route })
    }

    @Test fun relayCooldownRecoversTailscaleAndChangedAddressIsTriedImmediately() = runBlocking {
        val clock = MutableClock()
        var tailscaleAvailable = false
        val transport = FakeTransport().apply { handler = {
            if (it.route == Route.Direct || (it.route == Route.Tailscale && !tailscaleAvailable)) throw SocketTimeoutException()
            HttpResult(200, replyJson(it.body, status = status()))
        } }
        val router = Router(transport, clock)
        assertEquals(Route.Relay, (router.send(device(), request()) as Delivery.Reply).route)
        tailscaleAvailable = true
        transport.attempts.clear()
        router.send(device(), request())
        assertEquals(listOf(Route.Relay), transport.attempts.map { it.route })
        clock.now = NOW.plusSeconds(5)
        assertEquals(Route.Tailscale, (router.send(device(), request()) as Delivery.Reply).route)
        transport.attempts.clear()
        router.send(device().copy(tailscaleEndpoint = "https://100.64.0.3:8443"), request())
        assertEquals(listOf(Route.Tailscale), transport.attempts.map { it.route })
    }

    @Test fun tailscaleSecurityAuthenticationIdentityAndHttpErrorsNeverReachRelay() = runBlocking {
        for (failure in listOf("tls", "identity", "request", "401", "403", "500")) {
            val transport = FakeTransport().apply { handler = {
                if (it.route == Route.Direct) throw SocketTimeoutException()
                when (failure) {
                    "tls" -> throw certificateFailure()
                    "identity" -> HttpResult(200, replyJson(it.body, status = status(deviceId = OTHER)))
                    "request" -> HttpResult(200, JSONObject(replyJson(it.body)).put("requestId", OTHER.toString()).toString())
                    else -> HttpResult(failure.toInt(), "untrusted")
                }
            } }
            assertTrue(Router(transport, MutableClock()).send(device(), request()) is Delivery.Failure)
            assertEquals(listOf(Route.Direct, Route.Tailscale), transport.attempts.map { it.route })
        }
    }

    @Test fun sharedDeadlineStopsBeforeRelay() = runBlocking {
        val clock = MutableClock()
        val transport = FakeTransport().apply { handler = {
            clock.now = clock.now.plusMillis(it.timeout)
            throw SocketTimeoutException()
        } }
        val result = Router(transport, clock).send(device(), request(expires = NOW.plusSeconds(8))) as Delivery.Failure
        assertEquals(Feedback.Expired, result.feedback)
        assertTrue(result.mayHaveExecuted)
        assertEquals(listOf(6000L, 2000L), transport.attempts.map { it.timeout })
    }

    @Test fun optionalWireFieldsAndOldStorageRemainCompatible() {
        assertNull(Protocol.pairBundle(bundleJson(), NOW).tailscaleEndpoint)
        val bundle = JSONObject(bundleJson()).put("tailscaleEndpoint", endpoint + "/")
        assertEquals(endpoint, Protocol.pairBundle(bundle.toString(), NOW).tailscaleEndpoint)
        val statusJson = Protocol.statusJson(status()).put("tailscaleEndpoint", endpoint)
        assertEquals(endpoint, Protocol.status(statusJson).tailscaleEndpoint)
        statusJson.remove("tailscaleEndpoint")
        assertNull(Protocol.status(statusJson).tailscaleEndpoint)
        val old = JSONObject(StateCodec.encode(activeState(true)))
        old.getJSONArray("devices").getJSONObject(0).remove("tailscaleEndpoint")
        old.getJSONArray("devices").getJSONObject(0).remove("autoLearnTailscale")
        assertEquals(profile(true), StateCodec.decode(old.toString()).selected!!.profile)
        val state = activeState(true).copy(devices = listOf(DeviceState(device(), status().copy(tailscaleEndpoint = endpoint))))
        val decoded = StateCodec.decode(StateCodec.encode(state)).selected!!
        assertEquals(device(), decoded.profile)
        assertEquals(endpoint, decoded.status!!.tailscaleEndpoint)
    }

    @Test fun invalidOptionalEndpointsAreRejected() {
        for (invalid in listOf("http://100.64.0.2", "https://user:pass@host", "https://host/path", "https://host?q=x", "")) {
            assertThrows(Exception::class.java) {
                Protocol.pairBundle(JSONObject(bundleJson()).put("tailscaleEndpoint", invalid).toString(), NOW)
            }
            assertThrows(Exception::class.java) { Protocol.status(Protocol.statusJson(status()).put("tailscaleEndpoint", invalid)) }
        }
    }

    @Test fun authenticatedStatusUpdatesExistingPairingWithoutChangingCredentials() = runBlocking {
        val store = MemoryStore(activeState(true))
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body, status = status().copy(tailscaleEndpoint = endpoint)))
        } }
        val repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Updated, repository.refresh(DEVICE))
        assertEquals(device(), store.value.selected!!.profile)
        assertEquals(Route.Direct, repository.state.value.selected!!.lastSuccessfulRoute)
        assertEquals(0, transport.pairCalls)
        transport.handler = { HttpResult(200, replyJson(it.body, status = status())) }
        repository.refresh(DEVICE)
        assertEquals(endpoint, store.value.selected!!.profile.tailscaleEndpoint)
        transport.handler = { HttpResult(200, replyJson(it.body, status = status(deviceId = OTHER).copy(tailscaleEndpoint = "https://attacker.example"))) }
        assertEquals(Feedback.Protocol, repository.refresh(DEVICE))
        assertEquals(endpoint, store.value.selected!!.profile.tailscaleEndpoint)
    }

    @Test fun pairingConfigurationAndClearPreserveLegacyArguments() = runBlocking {
        val store = MemoryStore(AppState(loaded = true))
        val repository = DeviceRepository(store, FakeTransport(), MutableClock())
        repository.pair(JSONObject(bundleJson()).put("tailscaleEndpoint", endpoint).toString(), "Phone")
        assertEquals(endpoint, store.value.selected!!.profile.tailscaleEndpoint)
        repository.configure(DEVICE, "Phone", "", "", false)
        assertEquals(endpoint, store.value.selected!!.profile.tailscaleEndpoint)
        repository.configure(DEVICE, "Phone", "", "", false, "")
        assertNull(store.value.selected!!.profile.tailscaleEndpoint)
        assertEquals(Feedback.InvalidTailscaleEndpoint, repository.configure(DEVICE, "Phone", "", "", false, "http://host"))
        assertNull(store.value.selected!!.profile.tailscaleEndpoint)
    }

    @Test fun explicitClearPersistsAndRejectsStatusLearningUntilUserReenablesIt() = runBlocking {
        val store = MemoryStore(activeState(true).copy(devices = listOf(DeviceState(device(), status(), connected = true))))
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body, status = status().copy(tailscaleEndpoint = endpoint)))
        } }
        var repository = DeviceRepository(store, transport, MutableClock())
        assertEquals(Feedback.Saved, repository.configure(DEVICE, "Phone", "https://relay.example", "", false, ""))
        assertFalse(store.value.selected!!.profile.autoLearnTailscale)
        assertNull(store.value.selected!!.profile.tailscaleEndpoint)
        repository.refresh(DEVICE)
        assertNull(store.value.selected!!.profile.tailscaleEndpoint)
        store.value = StateCodec.decode(StateCodec.encode(store.value))
        repository = DeviceRepository(store, transport, MutableClock())
        repository.refresh(DEVICE)
        assertNull(store.value.selected!!.profile.tailscaleEndpoint)
        assertFalse(store.value.selected!!.profile.autoLearnTailscale)
        repository.configure(DEVICE, "Renamed", "https://relay.example", "", false)
        assertFalse(store.value.selected!!.profile.autoLearnTailscale)
        repository.pair(JSONObject(bundleJson()).put("tailscaleEndpoint", endpoint).toString(), "Phone")
        assertFalse(store.value.selected!!.profile.autoLearnTailscale)
        assertNull(store.value.selected!!.profile.tailscaleEndpoint)
        repository.configure(DEVICE, "Phone", "", "", false, autoLearnTailscale = true)
        repository.refresh(DEVICE)
        assertEquals(endpoint, store.value.selected!!.profile.tailscaleEndpoint)
        assertTrue(store.value.selected!!.profile.autoLearnTailscale)
    }

    @Test fun migratedEmptyProfileStillLearnsAndInvalidFlagIsRejected() = runBlocking {
        val json = JSONObject(StateCodec.encode(activeState(true)))
        val profileJson = json.getJSONArray("devices").getJSONObject(0)
        profileJson.remove("tailscaleEndpoint")
        profileJson.remove("autoLearnTailscale")
        val store = MemoryStore(StateCodec.decode(json.toString()))
        assertTrue(store.value.selected!!.profile.autoLearnTailscale)
        val transport = FakeTransport().apply { handler = {
            HttpResult(200, replyJson(it.body, status = status().copy(tailscaleEndpoint = endpoint)))
        } }
        val repository = DeviceRepository(store, transport, MutableClock())
        repository.configure(DEVICE, "Phone", "https://relay.example", "", false)
        repository.refresh(DEVICE)
        assertEquals(endpoint, store.value.selected!!.profile.tailscaleEndpoint)
        profileJson.put("autoLearnTailscale", "false")
        assertThrows(IllegalArgumentException::class.java) { StateCodec.decode(json.toString()) }
        Unit
    }
}
