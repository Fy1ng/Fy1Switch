package com.fy1ng.fy1switch.core

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

internal fun controlsJson(target: UUID = TARGET, brightness: Int = 65): JSONObject = JSONObject().put("targetId", target.toString())
    .put("observedAt", NOW.toString()).put("controls", JSONArray().apply {
        put(JSONObject().put("control", "Brightness").put("supported", true).put("value", brightness).put("supportedValues", JSONArray()))
        put(JSONObject().put("control", "Contrast").put("supported", true).put("value", 50).put("supportedValues", JSONArray()))
        put(JSONObject().put("control", "Volume").put("supported", false).put("value", JSONObject.NULL).put("supportedValues", JSONArray()))
        put(JSONObject().put("control", "InputSource").put("supported", true).put("value", 17).put("supportedValues", JSONArray(listOf(15, 17, 18))))
        put(JSONObject().put("control", "ColorTemperature").put("supported", true).put("value", 5).put("supportedValues", JSONArray(listOf(1, 2, 4, 5, 6, 8, 11))))
        put(JSONObject().put("control", "Rotation").put("supported", true).put("value", 0).put("supportedValues", JSONArray(listOf(0, 1, 2, 3))))
    }).put("vcpCodes", JSONArray().put(JSONObject().put("code", 16).put("name", "Brightness").put("supportedValues", JSONArray())))

internal fun controlOperation(outcome: OperationOutcome = OperationOutcome.Confirmed, control: DisplayControl = DisplayControl.Brightness,
    value: Int = 70) = ControlOperation(OPERATION, TARGET, control, value, outcome, null,
    if (outcome == OperationOutcome.Pending) null else ControlWriteResult(true, if (outcome == OperationOutcome.Confirmed) value else null,
        outcome == OperationOutcome.Confirmed, null), NOW)

internal fun controlReply(body: String, controls: JSONObject? = null, operation: ControlOperation? = null): String =
    JSONObject(replyJson(body)).put("controls", controls ?: JSONObject.NULL)
        .put("controlOperation", operation?.let(DisplayControlProtocol::operationJson) ?: JSONObject.NULL).toString()

internal fun parameterTransport(store: MemoryStore) = FakeTransport().apply { handler = { attempt ->
    val request = JSONObject(attempt.body)
    when (request.getString("command")) {
        "controls" -> HttpResult(200, controlReply(attempt.body, controlsJson(Protocol.uuid(request.getString("targetId")))))
        "set-control", "control-operation" -> {
            val pending = store.value.devices.find { it.profile.deviceId == attempt.device.deviceId }!!.controlOperation!!
            HttpResult(200, controlReply(attempt.body, operation = pending.copy(outcome = OperationOutcome.Confirmed,
                result = ControlWriteResult(true, pending.requestedValue, true, null))))
        }
        else -> HttpResult(200, replyJson(attempt.body, status = status(deviceId = attempt.device.deviceId)))
    }
} }
