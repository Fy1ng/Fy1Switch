package com.fy1ng.remotedisplayswitch.core

import org.json.JSONObject
import java.net.URI
import java.time.Instant
import java.util.Base64
import java.util.UUID

object Protocol {
    const val MAX_BODY = 65536

    fun uuid(value: String): UUID = UUID.fromString(value).also {
        require(it.toString().equals(value, ignoreCase = true))
    }

    fun endpoint(value: String): String {
        require(value.length <= 2048)
        val uri = URI(value.trim())
        require(uri.scheme.equals("https", ignoreCase = true) && !uri.host.isNullOrBlank())
        require(uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null)
        require(uri.rawPath.isNullOrEmpty() || uri.rawPath == "/")
        require(uri.port == -1 || uri.port in 1..65535)
        return uri.toASCIIString().trimEnd('/')
    }

    fun pin(value: String): String {
        require(value.startsWith("sha256/") && value.length == 51)
        val digest = Base64.getDecoder().decode(value.substring(7))
        require(digest.size == 32 && Base64.getEncoder().encodeToString(digest) == value.substring(7))
        return value
    }

    fun token(value: String): String {
        require(value.length in 1..4096 && value.all { it.code in 33..126 })
        return value
    }

    fun label(value: String): String {
        val trimmed = value.trim()
        require(trimmed.length in 1..80 && trimmed.none { it.isISOControl() })
        return trimmed
    }

    fun pairBundle(text: String, now: Instant): PairBundle {
        require(text.length <= MAX_BODY)
        val json = JSONObject(text)
        require(json.get("version") == 1)
        val expiresAt = Instant.parse(json.getString("expiresAt"))
        require(expiresAt > now && expiresAt <= now.plusSeconds(300))
        val code = json.getString("code")
        require(code.length in 1..256 && code.none { it.isISOControl() })
        return PairBundle(
            uuid(json.getString("deviceId")), endpoint(json.getString("endpoint")),
            pin(json.getString("certificatePin")), code, expiresAt,
        )
    }

    fun pairRequest(code: String, clientName: String): String =
        JSONObject().put("code", code).put("clientName", label(clientName)).toString()

    fun pairReply(text: String, expectedDevice: UUID): PairReply {
        require(text.length <= MAX_BODY)
        val json = JSONObject(text)
        val deviceId = uuid(json.getString("deviceId"))
        require(deviceId == expectedDevice)
        return PairReply(deviceId, uuid(json.getString("clientId")), token(json.getString("token")))
    }

    fun command(request: RemoteCommand): String {
        require(request.version == 1 && request.command in setOf("status", "refresh", "set", "operation"))
        require(request.command != "set" || (request.targetId != null && request.operationId != null && request.intent != null))
        require(request.command != "operation" || request.operationId != null)
        return JSONObject().put("version", request.version).put("requestId", request.requestId.toString())
            .put("deviceId", request.deviceId.toString()).put("command", request.command)
            .put("expiresAt", request.expiresAt.toString()).apply {
                request.targetId?.let { put("targetId", it.toString()) }
                request.intent?.let { put("intent", it.name) }
                request.operationId?.let { put("operationId", it.toString()) }
            }.toString()
    }

    fun reply(text: String, request: RemoteCommand): RemoteReply {
        require(text.length <= MAX_BODY)
        val json = JSONObject(text)
        require(json.get("version") == 1)
        val requestId = uuid(json.getString("requestId"))
        require(requestId == request.requestId)
        val status = json.objectOrNull("status")?.let(::status)
        require(status == null || status.deviceId == request.deviceId)
        val operation = json.objectOrNull("operation")?.let(::operation)
        if (operation != null) {
            require(request.operationId != null && operation.operationId == request.operationId)
            require(request.targetId == null || operation.targetId == request.targetId)
            require(request.intent == null || operation.requested == request.intent)
        }
        return RemoteReply(requestId, json.boolean("success"), json.stringOrNull("errorCode"), status, operation, 1)
    }

    fun observation(json: JSONObject) = Observation(
        DisplayPower.valueOf(json.getString("state")), json.intOrNull("rawValue"),
        Instant.parse(json.getString("observedAt")), json.getString("source"),
    )

    fun status(json: JSONObject) = RemoteStatus(
        uuid(json.getString("deviceId")), json.stringOrNull("targetId")?.let(::uuid),
        json.stringOrNull("displayName"), observation(json.getJSONObject("observation")),
        json.boolean("available"), json.boolean("busy"),
    )

    fun operation(json: JSONObject) = OperationRecord(
        uuid(json.getString("operationId")), uuid(json.getString("targetId")),
        PowerIntent.valueOf(json.getString("requested")),
        json.stringOrNull("resolved")?.let(PowerIntent::valueOf),
        OperationOutcome.valueOf(json.getString("outcome")), json.stringOrNull("errorCode"),
        json.objectOrNull("observation")?.let(::observation), Instant.parse(json.getString("updatedAt")),
    )

    fun observationJson(value: Observation) = JSONObject().put("state", value.state.name)
        .put("rawValue", value.rawValue ?: JSONObject.NULL).put("observedAt", value.observedAt.toString())
        .put("source", value.source)

    fun statusJson(value: RemoteStatus) = JSONObject().put("deviceId", value.deviceId.toString())
        .put("targetId", value.targetId?.toString() ?: JSONObject.NULL)
        .put("displayName", value.displayName ?: JSONObject.NULL)
        .put("observation", observationJson(value.observation)).put("available", value.available).put("busy", value.busy)

    fun operationJson(value: OperationRecord) = JSONObject().put("operationId", value.operationId.toString())
        .put("targetId", value.targetId.toString()).put("requested", value.requested.name)
        .put("resolved", value.resolved?.name ?: JSONObject.NULL).put("outcome", value.outcome.name)
        .put("errorCode", value.errorCode ?: JSONObject.NULL)
        .put("observation", value.observation?.let(::observationJson) ?: JSONObject.NULL)
        .put("updatedAt", value.updatedAt.toString())
}

internal fun JSONObject.stringOrNull(key: String): String? =
    if (isNull(key)) null else get(key).also { require(it is String) } as String

internal fun JSONObject.objectOrNull(key: String): JSONObject? =
    if (isNull(key)) null else getJSONObject(key)

private fun JSONObject.boolean(key: String): Boolean = get(key).also { require(it is Boolean) } as Boolean
private fun JSONObject.intOrNull(key: String): Int? =
    if (isNull(key)) null else get(key).also { require(it is Int) } as Int
