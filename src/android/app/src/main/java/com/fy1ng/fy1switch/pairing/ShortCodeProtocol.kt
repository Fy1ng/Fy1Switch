package com.fy1ng.fy1switch.pairing

import org.bouncycastle.crypto.agreement.srp.*
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters
import org.json.JSONObject
import java.math.BigInteger
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import java.util.UUID

object ShortCodeProtocol {
    private val group = SRP6StandardGroups.rfc5054_3072
    private val random = SecureRandom()
    fun validCode(code: String) = code.length == 6 && code.all { it in '0'..'9' }
    fun newCode(): String = random.nextInt(1_000_000).toString().padStart(6, '0')
    fun session(): String = UUID.randomUUID().toString()
    fun validSession(value: String): Boolean = runCatching { UUID.fromString(value).toString() == value }.getOrDefault(false)
    fun padded(value: BigInteger, length: Int): ByteArray {
        require(value.signum() >= 0 && value.bitLength() <= length * 8)
        val bytes = value.toByteArray()
        return ByteArray(length).also { bytes.copyInto(it, maxOf(0, length - bytes.size), maxOf(0, bytes.size - length)) }
    }
    private fun encode(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)
    private fun decode(value: String, length: Int): ByteArray {
        val bytes = Base64.getDecoder().decode(value)
        require(encode(bytes) == value && bytes.size == length)
        return bytes
    }
    private fun integer(message: JSONObject, name: String, size: Int): BigInteger {
        val value = BigInteger(1, decode(message.getString(name), size))
        if (size == 384) require(value.signum() > 0 && value < group.n)
        return value
    }
    private fun frame(type: String, client: String, server: String) = JSONObject()
        .put("type", type).put("version", 1).put("clientSession", client).put("serverSession", server)
    private fun check(message: JSONObject, type: String, client: String, server: String) {
        require(message.get("version") == 1 && message.getString("type") == type)
        require(message.getString("clientSession") == client && message.getString("serverSession") == server)
    }
    private fun channel(key: BigInteger, values: List<String>): SecureChannel {
        val hash = MessageDigest.getInstance("SHA-256").digest(values.joinToString("\n").toByteArray(Charsets.UTF_8))
        val derived = ByteArray(32)
        HKDFBytesGenerator(SHA256Digest()).apply {
            init(HKDFParameters(padded(key, 32), hash, "Fy1Switch-Pair-v1/encryption".toByteArray(Charsets.UTF_8)))
            generateBytes(derived, 0, derived.size)
        }
        return SecureChannel(derived, hash)
    }
    fun client(wire: PairWire, serverSession: String, code: String, clientRole: String = "android", clientSession: String = session(), onProof: () -> Unit = {}): SecureChannel {
        require(validCode(code) && validSession(serverSession) && validSession(clientSession))
        require(clientRole == "android" || clientRole == "windows")
        val serverRole = if (clientRole == "android") "windows" else "android"
        val srp = Client().apply { init(group, SHA256Digest(), random) }
        val identity = "Fy1Switch-Pair-v1/$serverSession".toByteArray(Charsets.UTF_8)
        val password = code.toByteArray(Charsets.UTF_8)
        val encodedA = encode(padded(srp.generateClientCredentials(ByteArray(32), identity, password), 384))
        wire.write(frame("hello", clientSession, serverSession).put("clientRole", clientRole).put("a", encodedA))
        val challenge = wire.read()
        check(challenge, "challenge", clientSession, serverSession)
        require(challenge.getString("serverRole") == serverRole)
        val salt = decode(challenge.getString("salt"), 32)
        val publicB = integer(challenge, "b", 384)
        srp.applySalt(salt, identity, password)
        srp.calculateSecret(publicB)
        val m1 = encode(padded(srp.calculateClientEvidenceMessage(), 32))
        wire.write(frame("proof", clientSession, serverSession).put("m1", m1))
        val verified = wire.read()
        check(verified, "verified", clientSession, serverSession)
        require(srp.verifyServerEvidenceMessage(integer(verified, "m2", 32)))
        onProof()
        return channel(srp.calculateSessionKey(), listOf("Fy1Switch-Pair-v1", clientSession, serverSession, clientRole, serverRole,
            challenge.getString("salt"), encodedA, challenge.getString("b"), m1, verified.getString("m2")))
    }
    fun server(wire: PairWire, serverSession: String, code: String, serverRole: String = "android", onProof: () -> Unit = {}): SecureChannel {
        require(validCode(code) && validSession(serverSession))
        require(serverRole == "android" || serverRole == "windows")
        val clientRole = if (serverRole == "android") "windows" else "android"
        val hello = wire.read()
        val clientSession = hello.getString("clientSession")
        require(validSession(clientSession))
        check(hello, "hello", clientSession, serverSession)
        require(hello.getString("clientRole") == clientRole)
        val publicA = integer(hello, "a", 384)
        val salt = ByteArray(32).also(random::nextBytes)
        val verifier = SRP6VerifierGenerator().apply { init(group, SHA256Digest()) }
            .generateVerifier(salt, "Fy1Switch-Pair-v1/$serverSession".toByteArray(Charsets.UTF_8), code.toByteArray(Charsets.UTF_8))
        val srp = SRP6Server().apply { init(group, verifier, SHA256Digest(), random) }
        val publicB = encode(padded(srp.generateServerCredentials(), 384))
        wire.write(frame("challenge", clientSession, serverSession).put("serverRole", serverRole).put("salt", encode(salt)).put("b", publicB))
        srp.calculateSecret(publicA)
        val proof = wire.read()
        check(proof, "proof", clientSession, serverSession)
        require(srp.verifyClientEvidenceMessage(integer(proof, "m1", 32)))
        onProof()
        val m2 = encode(padded(srp.calculateServerEvidenceMessage(), 32))
        wire.write(frame("verified", clientSession, serverSession).put("m2", m2))
        return channel(srp.calculateSessionKey(), listOf("Fy1Switch-Pair-v1", clientSession, serverSession, clientRole, serverRole,
            encode(salt), hello.getString("a"), publicB, proof.getString("m1"), m2))
    }
    private class Client : SRP6Client() {
        fun applySalt(salt: ByteArray, identity: ByteArray, password: ByteArray) {
            x = SRP6Util.calculateX(SHA256Digest(), group.n, salt, identity, password)
        }
    }
}
