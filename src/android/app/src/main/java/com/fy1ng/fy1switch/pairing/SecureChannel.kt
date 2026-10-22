package com.fy1ng.fy1switch.pairing

import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class SecureChannel internal constructor(private val key: ByteArray, val transcriptHash: ByteArray) {
    private val sent = mutableSetOf<String>()
    private val received = mutableSetOf<String>()
    private fun purpose(value: String) = require(value == "windows-bundle" || value == "android-result")
    private fun encode(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)
    private fun decode(value: String, length: Int? = null): ByteArray {
        val bytes = Base64.getDecoder().decode(value)
        require(encode(bytes) == value && (length == null || bytes.size == length))
        return bytes
    }
    fun encrypt(purpose: String, plaintext: String): JSONObject {
        purpose(purpose)
        require(sent.add(purpose))
        val nonce = ByteArray(12).also(SecureRandom()::nextBytes)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        cipher.updateAAD(transcriptHash + purpose.toByteArray(Charsets.UTF_8))
        val encrypted = cipher.doFinal(plaintext.toByteArray(Charsets.UTF_8))
        return JSONObject().put("type", "encrypted").put("version", 1).put("purpose", purpose)
            .put("nonce", encode(nonce)).put("ciphertext", encode(encrypted.copyOfRange(0, encrypted.size - 16)))
            .put("tag", encode(encrypted.copyOfRange(encrypted.size - 16, encrypted.size)))
    }
    fun decrypt(purpose: String, message: JSONObject): String {
        purpose(purpose)
        require(received.add(purpose))
        require(message.getString("type") == "encrypted" && message.get("version") == 1 && message.getString("purpose") == purpose)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, decode(message.getString("nonce"), 12)))
        cipher.updateAAD(transcriptHash + purpose.toByteArray(Charsets.UTF_8))
        val bytes = cipher.doFinal(decode(message.getString("ciphertext")) + decode(message.getString("tag"), 16))
        return Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
    }
}
