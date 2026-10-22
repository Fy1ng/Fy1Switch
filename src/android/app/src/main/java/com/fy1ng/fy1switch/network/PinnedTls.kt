package com.fy1ng.fy1switch.network

import com.fy1ng.fy1switch.core.Protocol
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.X509TrustManager

class PinnedTrustManager(pin: String) : X509TrustManager {
    private val expected = Base64.getDecoder().decode(Protocol.pin(pin).substring(7))

    fun verify(certificate: X509Certificate) {
        certificate.checkValidity()
        val actual = MessageDigest.getInstance("SHA-256").digest(certificate.publicKey.encoded)
        if (!MessageDigest.isEqual(expected, actual)) throw CertificateException("Certificate identity mismatch")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("Missing certificate")
        if (authType.isNullOrBlank()) throw CertificateException("Missing authentication type")
        verify(leaf)
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("Client authentication is not supported")
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    fun hostnameVerifier(expectedHost: String): HostnameVerifier = HostnameVerifier { host, session ->
        if (!host.equals(expectedHost, ignoreCase = true)) return@HostnameVerifier false
        try {
            verify(session.peerCertificates.first() as X509Certificate)
            true
        } catch (_: Exception) {
            false
        }
    }
}
