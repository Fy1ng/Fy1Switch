package com.fy1ng.fy1switch.pairing

import com.fy1ng.fy1switch.core.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset
import javax.net.ssl.SSLException

class ShortCodePairingTest {
    @Test fun freshWindowsBundleSurvivesSmallClockDifferenceLikeOlderQrBundle() = runBlocking {
        val phoneTime = Instant.now()
        val windowsTime = phoneTime.plusSeconds(2)
        val olderQrBundle = bundleJson(expiresAt = windowsTime.minusSeconds(10).plusSeconds(300))
        assertEquals(DEVICE, Protocol.pairBundle(olderQrBundle, phoneTime).deviceId)

        val transport = FakeTransport()
        val store = MemoryStore(AppState())
        val repository = DeviceRepository(store, transport, Clock.fixed(phoneTime, ZoneOffset.UTC))
        repository.initialize()
        val freshBundle = bundleJson(expiresAt = windowsTime.plusSeconds(300))
        assertTrue(ShortCodePairing.acceptBundle(repository, freshBundle, "Fixture Android"))
        assertEquals(1, transport.pairCalls)
        assertEquals(PIN, store.value.devices.single().profile.certificatePin)
    }

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

    @Test fun certificateConnectionAndHttpsRejectionHaveDistinctFailureMessages() = runBlocking {
        val cases = listOf(
            "证书" to FakeTransport().apply { pairHandler = { _, _ -> throw SSLException("fixture") } },
            "HTTPS 服务" to FakeTransport().apply { pairHandler = { _, _ -> throw IOException("fixture") } },
            "被电脑拒绝" to FakeTransport().apply { pairHandler = { _, _ -> HttpResult(401, "{}") } },
        )
        for ((reason, transport) in cases) {
            val store = MemoryStore(AppState())
            val repository = DeviceRepository(store, transport)
            repository.initialize()
            val messages = mutableListOf<String>()
            assertFalse(ShortCodePairing.acceptBundle(repository, bundleJson(expiresAt = Instant.now().plusSeconds(300)), "Android") {
                messages.add(it)
            })
            assertTrue(messages.single().contains(reason))
            assertTrue(store.value.devices.isEmpty())
        }
    }

    @Test fun initialStatusFailureStillAcknowledgesSavedPairing() = runBlocking {
        val transport = FakeTransport().apply { handler = { throw SSLException("fixture") } }
        val store = MemoryStore(AppState())
        val repository = DeviceRepository(store, transport)
        repository.initialize()
        val messages = mutableListOf<String>()
        assertTrue(ShortCodePairing.acceptBundle(repository, bundleJson(expiresAt = Instant.now().plusSeconds(300)), "Android") {
            messages.add(it)
        })
        assertEquals(PIN, store.value.devices.single().profile.certificatePin)
        assertTrue(messages.isEmpty())
    }
}
