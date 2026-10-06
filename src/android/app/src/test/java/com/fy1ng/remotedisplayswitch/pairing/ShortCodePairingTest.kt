package com.fy1ng.remotedisplayswitch.pairing

import com.fy1ng.remotedisplayswitch.core.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import javax.net.ssl.SSLException

class ShortCodePairingTest {
    @Test fun authenticatedBundleUsesRepositoryAndDoesNotRePairExistingIdentity() = runBlocking {
        val transport = FakeTransport()
        val store = MemoryStore(AppState())
        val repository = DeviceRepository(store, transport)
        repository.initialize()
        val bundle = bundleJson(expiresAt = Instant.now().plusSeconds(300))
        assertTrue(ShortCodePairing.acceptBundle(repository, bundle, "Fixture Android"))
        assertEquals(PIN, store.value.devices.single().profile.certificatePin)
        assertFalse(ShortCodePairing.acceptBundle(repository, bundle, "Fixture Android"))
        assertEquals(1, transport.pairCalls)
    }
    @Test fun tlsFailureDoesNotSaveAndMalformedOrExpiredBundleNeverPairs() = runBlocking {
        val transport = FakeTransport().apply { pairHandler = { _, _ -> throw SSLException("fixture") } }
        val store = MemoryStore(AppState())
        val repository = DeviceRepository(store, transport)
        repository.initialize()
        assertFalse(ShortCodePairing.acceptBundle(repository, bundleJson(expiresAt = Instant.now().plusSeconds(300)), "Android"))
        assertTrue(store.value.devices.isEmpty())
        assertFalse(ShortCodePairing.acceptBundle(repository, "{}", "Android"))
        assertFalse(ShortCodePairing.acceptBundle(repository, bundleJson(expiresAt = Instant.now().minusSeconds(1)), "Android"))
        assertEquals(1, transport.pairCalls)
    }
}
