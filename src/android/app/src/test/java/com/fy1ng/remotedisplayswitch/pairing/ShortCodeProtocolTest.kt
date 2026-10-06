package com.fy1ng.remotedisplayswitch.pairing

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.math.BigInteger
import java.util.Base64
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.security.MessageDigest
import java.net.ServerSocket
import java.net.Socket
import org.bouncycastle.crypto.agreement.srp.SRP6StandardGroups

class ShortCodeProtocolTest {
    private fun runPair(role: String = "android", code: String = "000042", mutate: (JSONObject) -> Unit = {}): Pair<Result<SecureChannel>, Result<SecureChannel>> {
        val toServer = LinkedBlockingQueue<JSONObject>()
        val toClient = LinkedBlockingQueue<JSONObject>()
        fun wire(incoming: LinkedBlockingQueue<JSONObject>, outgoing: LinkedBlockingQueue<JSONObject>) = object : PairWire {
            override fun read() = incoming.poll(3, TimeUnit.SECONDS) ?: error("Peer closed")
            override fun write(message: JSONObject) { mutate(message); outgoing.put(message) }
        }
        val serverWire = wire(toServer, toClient)
        val clientWire = wire(toClient, toServer)
        val session = ShortCodeProtocol.session()
        val executor = Executors.newSingleThreadExecutor()
        try {
            val server = executor.submit<Result<SecureChannel>> {
                try { runCatching { ShortCodeProtocol.server(serverWire, session, "000042", if (role == "android") "windows" else "android") } }
                finally { toClient.offer(JSONObject().put("type", "closed")) }
            }
            val client = try { runCatching { ShortCodeProtocol.client(clientWire, session, code, role) } }
                finally { toServer.offer(JSONObject().put("type", "closed")) }
            return client to server.get(5, TimeUnit.SECONDS)
        } finally { executor.shutdownNow() }
    }
    @Test fun androidClientAndServerRolesRoundTrip() {
        for (role in listOf("android", "windows")) {
            val (client, server) = runPair(role)
            val clientChannel = client.getOrThrow()
            val serverChannel = server.getOrThrow()
            assertArrayEquals(clientChannel.transcriptHash, serverChannel.transcriptHash)
            val windows = if (role == "windows") clientChannel else serverChannel
            val android = if (role == "android") clientChannel else serverChannel
            assertEquals("fixture bundle", android.decrypt("windows-bundle", windows.encrypt("windows-bundle", "fixture bundle")))
            assertEquals("{\"success\":true}", windows.decrypt("android-result", android.encrypt("android-result", "{\"success\":true}")))
        }
    }
    @Test fun wrongCodeAndNonAsciiInputsRejected() {
        val (client, server) = runPair(code = "000043")
        assertTrue(client.isFailure && server.isFailure)
        for (code in listOf("42", "00042", "0000042", "１２３４５６", "12345 ", "-12345")) assertFalse(ShortCodeProtocol.validCode(code))
        assertTrue(ShortCodeProtocol.validCode("000000"))
        repeat(100) { assertTrue(ShortCodeProtocol.validCode(ShortCodeProtocol.newCode())) }
    }
    @Test fun publicValuesAndProofsRejectTampering() {
        for ((type, field, size) in listOf(Triple("hello", "a", 384), Triple("challenge", "b", 384), Triple("proof", "m1", 32), Triple("verified", "m2", 32))) {
            val (client, server) = runPair { if (it.getString("type") == type) it.put(field, Base64.getEncoder().encodeToString(ByteArray(size))) }
            assertTrue("Tampered $field accepted", client.isFailure || server.isFailure)
            if (field == "m2") assertTrue(client.isFailure)
        }
    }
    @Test fun rolesSessionsVersionsTypesAndCanonicalBase64FailClosed() {
        for ((field, value) in listOf("clientRole" to "android", "serverSession" to ShortCodeProtocol.session(), "version" to 2, "type" to "other", "a" to "AA==")) {
            val (client, server) = runPair(role = "windows") { if (it.getString("type") == "hello") it.put(field, value) }
            assertTrue(client.isFailure && server.isFailure)
        }
    }
    @Test fun gcmTagContextPurposeAndReplayRejected() {
        val key = ByteArray(32) { 4 }
        val hash = ByteArray(32) { 5 }
        for (field in listOf("tag", "ciphertext", "nonce")) {
            val message = SecureChannel(key, hash).encrypt("windows-bundle", "payload")
            val value = Base64.getDecoder().decode(message.getString(field))
            value[0] = (value[0].toInt() xor 1).toByte()
            message.put(field, Base64.getEncoder().encodeToString(value))
            assertTrue(runCatching { SecureChannel(key, hash).decrypt("windows-bundle", message) }.isFailure)
        }
        val message = SecureChannel(key, hash).encrypt("windows-bundle", "payload")
        assertTrue(runCatching { SecureChannel(key, ByteArray(32)).decrypt("windows-bundle", message) }.isFailure)
        assertTrue(runCatching { SecureChannel(key, hash).decrypt("android-result", message) }.isFailure)
        val receiver = SecureChannel(key, hash)
        receiver.decrypt("windows-bundle", message)
        assertTrue(runCatching { receiver.decrypt("windows-bundle", message) }.isFailure)
        val sender = SecureChannel(key, hash)
        sender.encrypt("android-result", "true")
        assertTrue(runCatching { sender.encrypt("android-result", "true") }.isFailure)
    }
    @Test fun unsignedPaddingAndSessionCanonicalization() {
        assertEquals(384, ShortCodeProtocol.padded(BigInteger.ONE, 384).size)
        assertEquals(1.toByte(), ShortCodeProtocol.padded(BigInteger.ONE, 384).last())
        assertEquals(0.toByte(), ShortCodeProtocol.padded(BigInteger("80", 16), 32).first())
        assertTrue(runCatching { ShortCodeProtocol.padded(BigInteger.ONE.shiftLeft(256), 32) }.isFailure)
        assertFalse(ShortCodeProtocol.validSession("1-1-1-1-1"))
    }
    @Test fun transcriptHasExactOrderEncodingAndNoTrailingNewline() {
        val messages = java.util.Collections.synchronizedList(mutableListOf<JSONObject>())
        val (client, _) = runPair { messages.add(JSONObject(it.toString())) }
        val hello = messages.single { it.getString("type") == "hello" }
        val challenge = messages.single { it.getString("type") == "challenge" }
        val proof = messages.single { it.getString("type") == "proof" }
        val verified = messages.single { it.getString("type") == "verified" }
        val transcript = listOf("RDS-Pair-v1", hello.getString("clientSession"), hello.getString("serverSession"),
            "android", "windows", challenge.getString("salt"), hello.getString("a"), challenge.getString("b"),
            proof.getString("m1"), verified.getString("m2")).joinToString("\n")
        assertArrayEquals(MessageDigest.getInstance("SHA-256").digest(transcript.toByteArray(Charsets.UTF_8)), client.getOrThrow().transcriptHash)
        assertFalse(messages.any { it.toString().contains("000042") })
    }
    @Test fun groupModulusAndNonCanonicalEvidenceRejected() {
        for (type in listOf("hello", "challenge")) {
            val (client, _) = runPair { if (it.getString("type") == type) it.put(if (type == "hello") "a" else "b",
                Base64.getEncoder().encodeToString(ShortCodeProtocol.padded(SRP6StandardGroups.rfc5054_3072.n, 384))) }
            assertTrue(client.isFailure)
        }
        val (client, _) = runPair { if (it.getString("type") == "verified") it.put("m2", it.getString("m2").trimEnd('=')) }
        assertTrue(client.isFailure)
    }
    @Test fun tcpFramesRejectOversizeMalformedUtf8AndDisconnect() {
        for (bytes in listOf(ByteArray(16385) { 32 } + byteArrayOf(10), byteArrayOf(0xc3.toByte(), 0x28, 10), "{\"type\":".toByteArray())) {
            ServerSocket(0).use { listener ->
                Socket("127.0.0.1", listener.localPort).use { sender ->
                    listener.accept().use { receiver ->
                        receiver.soTimeout = 2000
                        sender.getOutputStream().write(bytes)
                        sender.shutdownOutput()
                        assertTrue(runCatching { SocketPairWire(receiver).read() }.isFailure)
                    }
                }
            }
        }
    }
    @Test fun limitsSurviveRefreshAndEnforceExpiryOneShotAndExclusion() {
        var now = 0L
        val limits = PairingLimits { now }
        val first = CodeSession { now }
        assertTrue(first.admit(limits))
        assertFalse(CodeSession { now }.admit(limits))
        limits.release()
        repeat(4) { assertTrue(first.admit(limits)); limits.release() }
        assertFalse(first.admit(limits))
        repeat(5) { assertTrue(CodeSession { now }.admit(limits)); limits.release() }
        assertFalse(CodeSession { now }.admit(limits))
        now = 600_000
        val fresh = CodeSession { now }
        assertTrue(fresh.admit(limits)); limits.release()
        fresh.consume()
        assertFalse(fresh.admit(limits))
        assertTrue(runCatching { fresh.consume() }.isFailure)
        val expired = CodeSession { now }
        now += 300_000
        assertFalse(expired.admit(limits))
        assertTrue(runCatching { expired.consume() }.isFailure)
    }
}
