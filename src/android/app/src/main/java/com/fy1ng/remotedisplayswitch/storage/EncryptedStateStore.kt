package com.fy1ng.remotedisplayswitch.storage

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import com.fy1ng.remotedisplayswitch.core.AppState
import com.fy1ng.remotedisplayswitch.core.StateCodec
import com.fy1ng.remotedisplayswitch.core.StateStore
import java.io.File
import java.io.ByteArrayOutputStream
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class EncryptedStateStore(context: Context) : StateStore {
    private val file = AtomicFile(File(context.noBackupFilesDir, "devices.aesgcm"))
    private val aad = "com.fy1ng.remotedisplayswitch:devices:v1".toByteArray(Charsets.UTF_8)

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256).setRandomizedEncryptionRequired(true).build())
        return generator.generateKey()
    }

    override fun read(): AppState {
        if (!file.baseFile.exists() && !File(file.baseFile.path + ".bak").exists()) return AppState(loaded = true)
        val bytes = file.openRead().use { input ->
            val output = ByteArrayOutputStream()
            val buffer = ByteArray(8192)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                require(output.size() + count <= MAX_BYTES)
                output.write(buffer, 0, count)
            }
            val content = output.toByteArray()
            require(content.size in 30..MAX_BYTES)
            content
        }
        require(bytes[0] == 1.toByte())
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(1, 13)))
        cipher.updateAAD(aad)
        val plaintext = cipher.doFinal(bytes, 13, bytes.size - 13)
        return try { StateCodec.decode(plaintext.toString(Charsets.UTF_8)) } finally { plaintext.fill(0) }
    }

    override fun write(state: AppState) {
        val plaintext = StateCodec.encode(state).toByteArray(Charsets.UTF_8)
        require(plaintext.size <= MAX_BYTES - 64)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        cipher.updateAAD(aad)
        val encrypted = try { cipher.doFinal(plaintext) } finally { plaintext.fill(0) }
        require(cipher.iv.size == 12)
        val output = file.startWrite()
        try {
            output.write(byteArrayOf(1))
            output.write(cipher.iv)
            output.write(encrypted)
            file.finishWrite(output)
        } catch (exception: Exception) {
            file.failWrite(output)
            throw exception
        }
    }

    private companion object {
        const val ALIAS = "remote-display-state-v1"
        const val MAX_BYTES = 1_048_576
    }
}
