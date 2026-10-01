package io.fy1ng.fydisplay.network

import android.content.Context
import org.json.JSONObject
import java.io.BufferedReader
import java.net.HttpURLConnection
import java.net.URL

data class DisplayResult(
    val ok: Boolean,
    val displayState: String = "unknown",
    val powerState: String = "",
    val message: String = "",
    val transport: String = "",
)

class RemoteClient(private val context: Context) {
    fun status(): DisplayResult = perform("status", "GET")
    fun toggle(): DisplayResult = perform("toggle", "POST")
    fun turnOn(): DisplayResult = perform("on", "POST")
    fun turnOff(): DisplayResult = perform("off", "POST")

    private fun perform(action: String, method: String): DisplayResult {
        val cfg = AppConfig.load(context)
        if (cfg.token.isBlank()) return DisplayResult(false, message = "请先配置 Token")
        return when (cfg.mode) {
            ConnectionMode.DIRECT -> direct(cfg, action, method)
            ConnectionMode.RELAY -> relay(cfg, action, method)
            ConnectionMode.AUTO -> direct(cfg, action, method).let { if (it.ok) it else relay(cfg, action, method) }
        }
    }

    private fun direct(cfg: AppConfig, action: String, method: String): DisplayResult {
        if (cfg.directUrl.isBlank()) return DisplayResult(false, message = "Direct URL 未配置", transport = "direct")
        val path = if (action == "status") "/v1/status" else "/v1/display/$action"
        return request(cfg.directUrl + path, cfg.token, method, 900, 2500, "direct")
    }

    private fun relay(cfg: AppConfig, action: String, method: String): DisplayResult {
        if (cfg.relayUrl.isBlank()) return DisplayResult(false, message = "Relay URL 未配置", transport = "relay")
        if (cfg.deviceId.isBlank()) return DisplayResult(false, message = "Device ID 未配置", transport = "relay")
        val path = if (action == "status") "/v1/devices/${encodePath(cfg.deviceId)}/status" else "/v1/devices/${encodePath(cfg.deviceId)}/display/$action"
        return request(cfg.relayUrl + path, cfg.token, method, 3000, 15000, "relay")
    }

    private fun request(urlString: String, token: String, method: String, connectTimeout: Int, readTimeout: Int, transport: String): DisplayResult {
        var conn: HttpURLConnection? = null
        return try {
            conn = URL(urlString).openConnection() as HttpURLConnection
            conn.requestMethod = method
            conn.connectTimeout = connectTimeout
            conn.readTimeout = readTimeout
            conn.setRequestProperty("Authorization", "Bearer $token")
            conn.setRequestProperty("Accept", "application/json")
            if (method == "POST") {
                conn.doOutput = true
                conn.setFixedLengthStreamingMode(0)
                conn.outputStream.use { }
            }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val body = stream?.bufferedReader()?.use(BufferedReader::readText).orEmpty()
            val json = runCatching { JSONObject(body) }.getOrNull()
            DisplayResult(
                ok = code in 200..299 && (json?.optBoolean("ok", true) ?: true),
                displayState = json?.optString("display_state", "unknown") ?: "unknown",
                powerState = json?.optString("power_state", "") ?: "",
                message = json?.optString("message", if (code in 200..299) "OK" else "HTTP $code") ?: "HTTP $code",
                transport = transport,
            )
        } catch (e: Exception) {
            DisplayResult(false, message = e.javaClass.simpleName + ": " + (e.message ?: "request failed"), transport = transport)
        } finally {
            conn?.disconnect()
        }
    }

    private fun encodePath(value: String): String = java.net.URLEncoder.encode(value, "UTF-8").replace("+", "%20")
}
