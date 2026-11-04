package com.fy1ng.fy1switch.core

import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant

object DisplayControlProtocol {
    private fun integer(json: JSONObject, key: String): Int = json.get(key).also { require(it is Int) } as Int
    private fun optionalInteger(json: JSONObject, key: String): Int? = if (json.isNull(key)) null else integer(json, key)
    private fun boolean(json: JSONObject, key: String): Boolean = json.get(key).also { require(it is Boolean) } as Boolean
    private fun error(json: JSONObject) = json.stringOrNull("errorCode")?.also { require(it.length <= 128 && it.none(Char::isISOControl)) }
    private fun values(array: JSONArray, maximum: Int): List<Int> {
        require(array.length() <= 256)
        val values = (0 until array.length()).map { array.get(it).also { value -> require(value is Int && value in 0..maximum) } as Int }
        require(values.distinct().size == values.size)
        return values
    }
    fun controls(json: JSONObject): DisplayControls {
        val items = json.getJSONArray("controls")
        require(items.length() <= DisplayControl.entries.size)
        val controls = (0 until items.length()).map { index -> items.getJSONObject(index).let {
            val control = DisplayControl.valueOf(it.getString("control"))
            val value = optionalInteger(it, "value")
            require(value == null || DisplayControlPolicy.legal(control, value))
            val supported = boolean(it, "supported")
            val choices = values(it.getJSONArray("supportedValues"), if (control == DisplayControl.Rotation) 3 else if (DisplayControlPolicy.continuous(control)) 100 else 255)
            require(!supported || DisplayControlPolicy.continuous(control) || choices.isNotEmpty())
            DisplayControlState(control, supported, value, choices)
        } }
        require(controls.map { it.control }.distinct().size == controls.size)
        val codes = json.getJSONArray("vcpCodes")
        require(codes.length() <= 256)
        val vcp = (0 until codes.length()).map { index -> codes.getJSONObject(index).let {
            VcpDescriptor(integer(it, "code").also { code -> require(code in 0..255) }, Protocol.label(it.getString("name")), values(it.getJSONArray("supportedValues"), 65535))
        } }
        require(vcp.map { it.code }.distinct().size == vcp.size)
        return DisplayControls(Protocol.uuid(json.getString("targetId")), controls, vcp, Instant.parse(json.getString("observedAt")))
    }
    fun operation(json: JSONObject): ControlOperation {
        val control = DisplayControl.valueOf(json.getString("control"))
        val requested = integer(json, "requestedValue")
        require(DisplayControlPolicy.legal(control, requested))
        val outcome = OperationOutcome.valueOf(json.getString("outcome"))
        val result = json.objectOrNull("result")?.let {
            val value = optionalInteger(it, "value")
            require(value == null || DisplayControlPolicy.legal(control, value))
            val confirmed = boolean(it, "confirmed")
            val accepted = if (it.isNull("writeAccepted")) null else boolean(it, "writeAccepted")
            val code = error(it)
            require(!confirmed || (accepted == true && value != null && code == null))
            if (confirmed && !DisplayControlPolicy.continuous(control)) require(value == requested)
            ControlWriteResult(accepted, value, confirmed, code)
        }
        val code = error(json)
        require(outcome != OperationOutcome.Confirmed || (result?.confirmed == true && code == null))
        require(result?.confirmed != true || outcome == OperationOutcome.Confirmed)
        return ControlOperation(Protocol.uuid(json.getString("operationId")), Protocol.uuid(json.getString("targetId")),
            control, requested, outcome, code, result, Instant.parse(json.getString("updatedAt")))
    }
    fun operationJson(value: ControlOperation): JSONObject = JSONObject().put("operationId", value.operationId.toString())
        .put("targetId", value.targetId.toString()).put("control", value.control.name).put("requestedValue", value.requestedValue)
        .put("outcome", value.outcome.name).put("errorCode", value.errorCode ?: JSONObject.NULL).put("updatedAt", value.updatedAt.toString())
        .put("result", value.result?.let { JSONObject().put("writeAccepted", it.writeAccepted ?: JSONObject.NULL)
            .put("value", it.value ?: JSONObject.NULL).put("confirmed", it.confirmed).put("errorCode", it.errorCode ?: JSONObject.NULL) } ?: JSONObject.NULL)

    fun explicitUnsupported(body: String, request: RemoteCommand): Boolean = runCatching {
        val json = JSONObject(body)
        json.getString("errorCode") == "invalid_command" && (json.isNull("version") || json.get("version") == 1) &&
            (json.isNull("requestId") || Protocol.uuid(json.getString("requestId")) == request.requestId)
    }.getOrDefault(false)
}
