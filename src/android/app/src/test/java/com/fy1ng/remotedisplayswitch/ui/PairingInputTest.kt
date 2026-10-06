package com.fy1ng.remotedisplayswitch.ui

import com.fy1ng.remotedisplayswitch.core.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class PairingInputTest {
    @Test fun offlineQrRoundTripPreservesPairBundle() {
        val text = bundle().toString()
        val matrix = com.google.zxing.qrcode.QRCodeWriter().encode(
            text, com.google.zxing.BarcodeFormat.QR_CODE, 512, 512,
        )
        val pixels = IntArray(matrix.width * matrix.height) { index ->
            if (matrix[index % matrix.width, index / matrix.width]) 0xff000000.toInt() else 0xffffffff.toInt()
        }
        val source = com.google.zxing.RGBLuminanceSource(matrix.width, matrix.height, pixels)
        val bitmap = com.google.zxing.BinaryBitmap(com.google.zxing.common.HybridBinarizer(source))
        val decoded = com.google.zxing.qrcode.QRCodeReader().decode(bitmap).text
        assertEquals(text, PairingInput.validate(decoded, NOW))
    }

    private fun bundle() = JSONObject().put("version", 1).put("deviceId", DEVICE.toString())
        .put("endpoint", "https://192.0.2.1:8443").put("certificatePin", PIN)
        .put("code", "fixture-one-time-code").put("expiresAt", NOW.plusSeconds(120).toString())

    @Test fun validQrPassesUnmodifiedToExistingProtocol() {
        val text = bundle().toString()
        assertEquals(text, PairingInput.validate(text, NOW))
        val parsed = Protocol.pairBundle(PairingInput.validate(text, NOW), NOW)
        assertEquals(PIN, parsed.certificatePin)
        assertEquals(DEVICE, parsed.deviceId)
    }

    @Test fun urlsCommandsAndMalformedJsonAreRejected() {
        for (text in listOf("https://example.com/pair", "intent://pair", "{", "123456", "[]",
            Protocol.command(request()), "x".repeat(Protocol.MAX_BODY + 1))) {
            assertThrows(Exception::class.java) { PairingInput.validate(text, NOW) }
        }
    }

    @Test fun originalPinVersionAndExpiryChecksStillApply() {
        for (json in listOf(bundle().put("certificatePin", "sha256/invalid"), bundle().put("version", 2),
            bundle().put("expiresAt", NOW.toString()), bundle().put("expiresAt", NOW.plusSeconds(301).toString()),
            bundle().put("endpoint", "http://192.0.2.1:8443"), bundle().put("code", ""))) {
            assertThrows(Exception::class.java) { PairingInput.validate(json.toString(), NOW) }
        }
    }
}
