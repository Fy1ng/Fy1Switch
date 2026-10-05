package com.fy1ng.remotedisplayswitch.network

import com.fy1ng.remotedisplayswitch.core.PIN
import org.junit.Assert.*
import org.junit.Test
import java.lang.reflect.Proxy
import java.math.BigInteger
import java.security.KeyStore
import java.security.Principal
import java.security.PublicKey
import java.security.cert.CertificateException
import java.security.cert.CertificateExpiredException
import java.security.cert.X509Certificate
import java.security.MessageDigest
import java.util.Base64
import java.util.Date
import javax.net.ssl.SSLSession
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

class PinnedTlsTest {
    private fun certificate(): X509Certificate {
        val factory = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
        factory.init(null as KeyStore?)
        return factory.trustManagers.filterIsInstance<X509TrustManager>().first().acceptedIssuers.first {
            runCatching { it.checkValidity() }.isSuccess
        }
    }

    private fun pin(certificate: X509Certificate) = "sha256/" + Base64.getEncoder().encodeToString(
        MessageDigest.getInstance("SHA-256").digest(certificate.publicKey.encoded),
    )

    private fun session(certificate: X509Certificate): SSLSession = Proxy.newProxyInstance(
        SSLSession::class.java.classLoader, arrayOf(SSLSession::class.java),
    ) { _, method, _ ->
        when (method.name) {
            "getPeerCertificates" -> arrayOf(certificate)
            "getCipherSuite" -> "TLS_AES_128_GCM_SHA256"
            "getPeerHost" -> "unrelated.example"
            else -> null
        }
    } as SSLSession

    @Test fun acceptsOnlyThePinnedPublicKey() {
        val certificate = certificate()
        PinnedTrustManager(pin(certificate)).checkServerTrusted(arrayOf(certificate), "RSA")
        assertThrows(CertificateException::class.java) { PinnedTrustManager(PIN).checkServerTrusted(arrayOf(certificate), "RSA") }
    }

    @Test fun emptyChainsAndClientAuthenticationAreRejected() {
        val trust = PinnedTrustManager(PIN)
        assertThrows(CertificateException::class.java) { trust.checkServerTrusted(emptyArray(), "RSA") }
        assertThrows(CertificateException::class.java) { trust.checkServerTrusted(null, "RSA") }
        assertThrows(CertificateException::class.java) { trust.checkClientTrusted(emptyArray(), "RSA") }
        assertTrue(trust.acceptedIssuers.isEmpty())
    }

    @Test fun matchingPinDoesNotBypassCertificateExpiry() {
        val certificate = certificate()
        val expired = ExpiredCertificate(certificate)
        assertThrows(CertificateExpiredException::class.java) { PinnedTrustManager(pin(certificate)).verify(expired) }
    }

    @Test fun ipSanExceptionRequiresSamePinnedIdentityAndExpectedHost() {
        val certificate = certificate()
        val trust = PinnedTrustManager(pin(certificate))
        assertTrue(trust.hostnameVerifier("192.0.2.1").verify("192.0.2.1", session(certificate)))
        assertFalse(trust.hostnameVerifier("192.0.2.1").verify("192.0.2.2", session(certificate)))
        assertFalse(PinnedTrustManager(PIN).hostnameVerifier("192.0.2.1").verify("192.0.2.1", session(certificate)))
        assertFalse(trust.hostnameVerifier("192.0.2.1").verify("192.0.2.1", session(ExpiredCertificate(certificate))))
    }

    @Test fun dnsSanDifferenceRequiresExpectedEndpointHostValidCertificateAndSamePin() {
        val certificate = certificate()
        val host = "fixture.tailnet.ts.net"
        val verifier = PinnedTrustManager(pin(certificate)).hostnameVerifier(host)
        assertTrue(verifier.verify(host, session(certificate)))
        assertTrue(verifier.verify("FIXTURE.TAILNET.TS.NET", session(certificate)))
        assertFalse(verifier.verify("other.tailnet.ts.net", session(certificate)))
        assertFalse(verifier.verify("127.0.0.1", session(certificate)))
        assertFalse(PinnedTrustManager(PIN).hostnameVerifier(host).verify(host, session(certificate)))
        assertFalse(verifier.verify(host, session(ExpiredCertificate(certificate))))
    }

    @Suppress("DEPRECATION", "OVERRIDE_DEPRECATION")
    private class ExpiredCertificate(private val original: X509Certificate) : X509Certificate() {
        override fun checkValidity() { throw CertificateExpiredException("Expired fixture") }
        override fun checkValidity(date: Date) { throw CertificateExpiredException("Expired fixture") }
        override fun getPublicKey(): PublicKey = original.publicKey
        override fun getVersion(): Int = original.version
        override fun getSerialNumber(): BigInteger = original.serialNumber
        override fun getIssuerDN(): Principal = original.issuerDN
        override fun getSubjectDN(): Principal = original.subjectDN
        override fun getNotBefore(): Date = original.notBefore
        override fun getNotAfter(): Date = Date(0)
        override fun getTBSCertificate(): ByteArray = original.tbsCertificate
        override fun getSignature(): ByteArray = original.signature
        override fun getSigAlgName(): String = original.sigAlgName
        override fun getSigAlgOID(): String = original.sigAlgOID
        override fun getSigAlgParams(): ByteArray? = original.sigAlgParams
        override fun getIssuerUniqueID(): BooleanArray? = original.issuerUniqueID
        override fun getSubjectUniqueID(): BooleanArray? = original.subjectUniqueID
        override fun getKeyUsage(): BooleanArray? = original.keyUsage
        override fun getBasicConstraints(): Int = original.basicConstraints
        override fun getEncoded(): ByteArray = original.encoded
        override fun verify(key: PublicKey) = original.verify(key)
        override fun verify(key: PublicKey, provider: String) = original.verify(key, provider)
        override fun hasUnsupportedCriticalExtension(): Boolean = original.hasUnsupportedCriticalExtension()
        override fun getCriticalExtensionOIDs(): Set<String>? = original.criticalExtensionOIDs
        override fun getNonCriticalExtensionOIDs(): Set<String>? = original.nonCriticalExtensionOIDs
        override fun getExtensionValue(oid: String): ByteArray? = original.getExtensionValue(oid)
        override fun toString() = "ExpiredCertificate(fixture)"
    }
}
