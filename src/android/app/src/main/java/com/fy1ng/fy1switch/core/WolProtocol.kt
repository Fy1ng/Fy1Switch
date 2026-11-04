package com.fy1ng.fy1switch.core

import org.json.JSONArray
import org.json.JSONObject
import java.net.URI
import java.time.Instant
import java.util.UUID

class WolFailure(val code: String, val httpStatus: Int? = null) : Exception(code) {
    // Only the protocol's explicit pre-execution rejections prove this request was not admitted.
    // Proxy errors, failed result persistence and replay/conflict errors may follow an actual send.
    val rejectedBeforeExecution: Boolean get() = when (httpStatus) {
        400 -> code in setOf("invalid_request", "request_expired")
        401 -> code == "unauthorized"
        403 -> code == "device_forbidden"
        409 -> code == "device_busy"
        429 -> code in setOf("rate_limited", "device_cooldown")
        else -> false
    }
}

object WolProtocol {
    const val MAX_BODY = 65_536
    fun endpoint(value: String): String {
        val uri = URI(Protocol.endpoint(value))
        val host = uri.host.lowercase()
        val authority = if (host.contains(':') && !host.startsWith('[')) "[$host]" else host
        return "https://" + authority + if (uri.port == -1 || uri.port == 443) "" else ":${uri.port}"
    }
    fun token(value: String): String = value.also { require(it.matches(Regex("[A-Za-z0-9_-]{43}"))) }
    fun body(text: String): JSONObject {
        require(text.length <= MAX_BODY)
        return JSONObject(text).also { require(it.get("version") == 1) }
    }
    fun targets(array: JSONArray): List<WolTarget> {
        require(array.length() in 0..100)
        val values = (0 until array.length()).map { index -> array.getJSONObject(index).let {
            WolTarget(Protocol.uuid(it.getString("deviceId")), Protocol.label(it.getString("name")))
        } }
        require(values.map { it.deviceId }.distinct().size == values.size)
        return values
    }
    fun register(text: String, endpoint: String): WolAuthorization = body(text).let {
        WolAuthorization(endpoint(endpoint), Protocol.uuid(it.getString("clientId")), token(it.getString("accessToken")), targets(it.getJSONArray("devices")))
    }
    fun devices(text: String, authorization: WolAuthorization): WolAuthorization = body(text).let {
        require(Protocol.uuid(it.getString("clientId")) == authorization.clientId)
        authorization.copy(devices = targets(it.getJSONArray("devices")))
    }
    fun operation(text: String, original: WolOperation): WolOperation = body(text).let {
        require(Protocol.uuid(it.getString("deviceId")) == original.deviceId)
        require(Protocol.uuid(it.getString("operationId")) == original.operationId)
        val status = when (it.getString("status")) {
            "pending" -> WolStatus.Pending
            "packet_sent" -> WolStatus.PacketSent
            "failed" -> WolStatus.Failed
            "unknown" -> WolStatus.Unknown
            else -> throw IllegalArgumentException()
        }
        val error = it.stringOrNull("errorCode")?.also { value -> require(value.matches(Regex("[a-z_]{1,64}"))) }
        original.copy(status = status, updatedAt = Instant.parse(it.getString("updatedAt")), errorCode = error)
    }
    fun check(text: String, deviceId: UUID) = body(text).let {
        require(Protocol.uuid(it.getString("deviceId")) == deviceId && it.getString("status") == "ready")
    }
    fun request(deviceId: UUID, issuedAt: Instant, expiresAt: Instant, operationId: UUID? = null): String {
        require(expiresAt > issuedAt && expiresAt <= issuedAt.plusSeconds(120))
        return JSONObject().put("version", 1).put("deviceId", deviceId.toString()).put("requestId", UUID.randomUUID().toString())
            .put("issuedAt", issuedAt.toString()).put("expiresAt", expiresAt.toString()).apply {
                operationId?.let { put("operationId", it.toString()) }
            }.toString()
    }
    fun error(text: String): String = runCatching { body(text).getString("errorCode")
        .also { require(it.matches(Regex("[a-z_]{1,64}"))) } }.getOrDefault("protocol")
}

interface WolTransport {
    suspend fun send(endpoint: String, path: String, method: String, token: String?, body: String?, timeoutMillis: Long = 15_000): HttpResult
}
