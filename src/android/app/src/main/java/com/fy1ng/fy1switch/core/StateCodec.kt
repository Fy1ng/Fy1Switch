package com.fy1ng.fy1switch.core

import org.json.JSONArray
import org.json.JSONObject

interface StateStore {
    fun read(): AppState
    fun write(state: AppState)
}

object StateCodec {
    fun encode(state: AppState): String = JSONObject().put("version", 1)
        .put("defaultDeviceId", state.defaultDeviceId?.toString() ?: JSONObject.NULL)
        .put("devices", JSONArray().apply {
            state.devices.forEach { device ->
                val profile = device.profile
                put(JSONObject().put("deviceId", profile.deviceId.toString())
                    .put("clientId", profile.clientId.toString()).put("name", profile.name)
                    .put("endpoint", profile.endpoint).put("certificatePin", profile.certificatePin)
                    .put("tailscaleEndpoint", profile.tailscaleEndpoint ?: JSONObject.NULL)
                    .put("autoLearnTailscale", profile.autoLearnTailscale)
                    .put("deviceToken", profile.deviceToken)
                    .put("relay", profile.relay?.let {
                        JSONObject().put("endpoint", it.endpoint).put("controllerToken", it.controllerToken)
                    } ?: JSONObject.NULL)
                    .put("status", device.status?.let(Protocol::statusJson) ?: JSONObject.NULL)
                    .put("operation", device.operation?.let(Protocol::operationJson) ?: JSONObject.NULL)
                    .put("feedback", device.feedback.name).put("requiresRefresh", device.requiresRefresh))
            }
        }).toString()

    fun decode(text: String): AppState {
        val json = JSONObject(text)
        require(json.get("version") == 1)
        val array = json.getJSONArray("devices")
        require(array.length() <= 20)
        val devices = (0 until array.length()).map { index ->
            val device = array.getJSONObject(index)
            val profile = DeviceProfile(
                Protocol.uuid(device.getString("deviceId")), Protocol.uuid(device.getString("clientId")),
                Protocol.label(device.getString("name")), Protocol.endpoint(device.getString("endpoint")),
                Protocol.pin(device.getString("certificatePin")), Protocol.token(device.getString("deviceToken")),
                device.objectOrNull("relay")?.let {
                    RelayConfig(Protocol.endpoint(it.getString("endpoint")), Protocol.token(it.getString("controllerToken")))
                },
                device.stringOrNull("tailscaleEndpoint")?.let(Protocol::endpoint),
                if (device.has("autoLearnTailscale")) device.get("autoLearnTailscale").also { require(it is Boolean) } as Boolean else true,
            )
            val status = device.objectOrNull("status")?.let(Protocol::status)
            require(status == null || status.deviceId == profile.deviceId)
            val operation = device.objectOrNull("operation")?.let(Protocol::operation)?.let {
                if (it.outcome == OperationOutcome.Pending) it.copy(outcome = OperationOutcome.Unknown) else it
            }
            val feedback = if (StatePolicy.unresolved(operation)) Feedback.Unknown else Feedback.valueOf(device.getString("feedback"))
            DeviceState(profile, status, operation, connected = false, feedback = feedback,
                requiresRefresh = device.optBoolean("requiresRefresh", StatePolicy.unresolved(operation)) || operation?.outcome == OperationOutcome.Unknown)
        }
        require(devices.map { it.profile.deviceId }.distinct().size == devices.size)
        val defaultId = json.stringOrNull("defaultDeviceId")?.let(Protocol::uuid)
        require(defaultId == null || devices.any { it.profile.deviceId == defaultId })
        return AppState(devices, defaultId ?: devices.firstOrNull()?.profile?.deviceId, loaded = true)
    }
}
