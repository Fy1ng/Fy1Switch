package com.fy1ng.fy1switch.core

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

internal const val WOL_ENDPOINT = "https://wol.example"
internal val WOL_TOKEN = "A".repeat(43)
internal fun wolAuth() = WolAuthorization(WOL_ENDPOINT, CLIENT, WOL_TOKEN, listOf(WolTarget(DEVICE, "Desktop"), WolTarget(OTHER, "Other")))
internal fun wolBinding(id: UUID = DEVICE) = WolBinding(WOL_ENDPOINT, id, true)
internal fun wolState() = activeState().copy(devices = listOf(activeState().selected!!.copy(profile = profile().copy(wol = wolBinding()), connected = false)),
    wolAuthorizations = listOf(wolAuth()))
internal fun wolOperation(status: WolStatus = WolStatus.PacketSent) = WolOperation(OPERATION, DEVICE, status, NOW, NOW.plusSeconds(30), NOW)
internal fun wolReply(operation: WolOperation = wolOperation()) = JSONObject().put("version", 1)
    .put("deviceId", operation.deviceId.toString()).put("operationId", operation.operationId.toString())
    .put("status", when (operation.status) { WolStatus.Pending -> "pending"; WolStatus.PacketSent -> "packet_sent"; WolStatus.Failed -> "failed"; WolStatus.Unknown -> "unknown" })
    .put("errorCode", operation.errorCode ?: JSONObject.NULL).put("updatedAt", operation.updatedAt.toString()).toString()
internal fun wolDevices() = JSONArray().put(JSONObject().put("deviceId", DEVICE.toString()).put("name", "Desktop"))
internal class FakeWolTransport : WolTransport {
    data class Call(val endpoint: String, val path: String, val method: String, val token: String?, val body: String?)
    val calls = mutableListOf<Call>()
    var handler: suspend (Call) -> HttpResult = { call ->
        when {
            call.path.endsWith("/wake") -> {
                val request = JSONObject(call.body!!)
                HttpResult(200, wolReply(wolOperation().copy(operationId = UUID.fromString(request.getString("operationId")),
                    deviceId = UUID.fromString(request.getString("deviceId")))))
            }
            call.path.endsWith("/check") -> HttpResult(200, JSONObject().put("version", 1).put("deviceId", DEVICE).put("status", "ready").toString())
            call.path.contains("/operations/") -> HttpResult(200, wolReply(wolOperation().copy(operationId = UUID.fromString(call.path.substringAfterLast('/')))))
            call.path.endsWith("/register") -> HttpResult(200, JSONObject().put("version", 1).put("clientId", CLIENT).put("accessToken", WOL_TOKEN).put("devices", wolDevices()).toString())
            call.path.endsWith("/devices") -> HttpResult(200, JSONObject().put("version", 1).put("clientId", CLIENT).put("devices", wolDevices()).toString())
            else -> HttpResult(204, "")
        }
    }
    override suspend fun send(endpoint: String, path: String, method: String, token: String?, body: String?, timeoutMillis: Long): HttpResult {
        val call = Call(endpoint, path, method, token, body)
        calls += call
        return handler(call)
    }
}
