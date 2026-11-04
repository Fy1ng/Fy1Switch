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
        .put("wolTileDeviceId", state.wolTileDeviceId?.toString() ?: JSONObject.NULL)
        .put("wolAuthorizations", JSONArray().apply { state.wolAuthorizations.forEach { authorization ->
            put(JSONObject().put("endpoint", authorization.endpoint).put("clientId", authorization.clientId.toString())
                .put("accessToken", authorization.accessToken).put("devices", JSONArray().apply {
                    authorization.devices.forEach { put(JSONObject().put("deviceId", it.deviceId.toString()).put("name", it.name)) }
                }))
        } })
        .put("devices", JSONArray().apply {
            state.devices.forEach { device ->
                val profile = device.profile
                put(JSONObject().put("deviceId", profile.deviceId.toString())
                    .put("clientId", profile.clientId.toString()).put("name", profile.name)
                    .put("endpoint", profile.endpoint).put("certificatePin", profile.certificatePin)
                    .put("tailscaleEndpoint", profile.tailscaleEndpoint ?: JSONObject.NULL)
                    .put("autoLearnTailscale", profile.autoLearnTailscale)
                    .put("autoLearnRelay", profile.autoLearnRelay)
                    .put("deviceToken", profile.deviceToken)
                    .put("wol", profile.wol?.let { JSONObject().put("endpoint", it.endpoint)
                        .put("targetId", it.targetId.toString()).put("enabled", it.enabled) } ?: JSONObject.NULL)
                    .put("wolOperation", device.wolOperation?.let { JSONObject().put("operationId", it.operationId.toString())
                        .put("deviceId", it.deviceId.toString()).put("status", it.status.name).put("issuedAt", it.issuedAt.toString())
                        .put("expiresAt", it.expiresAt.toString()).put("updatedAt", it.updatedAt.toString())
                        .put("errorCode", it.errorCode ?: JSONObject.NULL) } ?: JSONObject.NULL)
                    .put("relay", profile.relay?.let {
                        JSONObject().put("endpoint", it.endpoint).put("controllerToken", it.controllerToken)
                    } ?: JSONObject.NULL)
                    .put("status", device.status?.let(Protocol::statusJson) ?: JSONObject.NULL)
                    .put("operation", device.operation?.let(Protocol::operationJson) ?: JSONObject.NULL)
                    .put("controlOperation", device.controlOperation?.let(DisplayControlProtocol::operationJson) ?: JSONObject.NULL)
                    .put("lastSuccessfulRoute", device.lastSuccessfulRoute?.name ?: JSONObject.NULL)
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
                if (device.has("autoLearnRelay")) device.get("autoLearnRelay").also { require(it is Boolean) } as Boolean else true,
                device.objectOrNull("wol")?.let { WolBinding(WolProtocol.endpoint(it.getString("endpoint")),
                    Protocol.uuid(it.getString("targetId")), it.getBoolean("enabled")) },
            )
            val status = device.objectOrNull("status")?.let(Protocol::status)
            require(status == null || status.deviceId == profile.deviceId)
            val operation = device.objectOrNull("operation")?.let(Protocol::operation)?.let {
                if (it.outcome == OperationOutcome.Pending) it.copy(outcome = OperationOutcome.Unknown) else it
            }
            val controlOperation = device.objectOrNull("controlOperation")?.let(DisplayControlProtocol::operation)?.let {
                if (it.outcome == OperationOutcome.Pending) it.copy(outcome = OperationOutcome.Unknown) else it
            }
            val feedback = if (StatePolicy.unresolved(operation)) Feedback.Unknown else Feedback.valueOf(device.getString("feedback"))
            // This optional hint only affects connection probing, never identity or live state.
            val routeName = device.opt("lastSuccessfulRoute") as? String
            val lastSuccessfulRoute = Route.entries.firstOrNull { it.name == routeName }?.takeIf {
                when (it) {
                    Route.Direct -> true
                    Route.Tailscale -> profile.tailscaleEndpoint != null
                    Route.Relay -> profile.relay != null
                }
            }
            DeviceState(profile, status, operation, connected = false, feedback = feedback,
                requiresRefresh = device.optBoolean("requiresRefresh", StatePolicy.unresolved(operation)) || operation?.outcome == OperationOutcome.Unknown,
                lastSuccessfulRoute = lastSuccessfulRoute,
                wolOperation = device.objectOrNull("wolOperation")?.let {
                    val savedStatus = WolStatus.valueOf(it.getString("status"))
                    WolOperation(Protocol.uuid(it.getString("operationId")), Protocol.uuid(it.getString("deviceId")),
                        if (savedStatus == WolStatus.Pending) WolStatus.Unknown else savedStatus,
                        java.time.Instant.parse(it.getString("issuedAt")), java.time.Instant.parse(it.getString("expiresAt")),
                        java.time.Instant.parse(it.getString("updatedAt")), it.stringOrNull("errorCode"))
                }?.also { require(profile.wol != null && it.deviceId == profile.wol.targetId) },
                controlOperation = controlOperation,
                controlsRequireRefresh = DisplayControlPolicy.unresolved(controlOperation),
                controlsFeedback = if (DisplayControlPolicy.unresolved(controlOperation)) ControlsFeedback.Unknown else ControlsFeedback.Ready)
        }
        require(devices.map { it.profile.deviceId }.distinct().size == devices.size)
        val defaultId = json.stringOrNull("defaultDeviceId")?.let(Protocol::uuid)
        require(defaultId == null || devices.any { it.profile.deviceId == defaultId })
        val authorizations = json.optJSONArray("wolAuthorizations")?.let { array ->
            require(array.length() <= 20)
            (0 until array.length()).map { index -> array.getJSONObject(index).let {
                WolAuthorization(WolProtocol.endpoint(it.getString("endpoint")), Protocol.uuid(it.getString("clientId")),
                    WolProtocol.token(it.getString("accessToken")), WolProtocol.targets(it.getJSONArray("devices")))
            } }
        } ?: emptyList()
        require(authorizations.map { it.endpoint }.distinct().size == authorizations.size)
        return AppState(devices, defaultId ?: devices.firstOrNull()?.profile?.deviceId, loaded = true,
            wolAuthorizations = authorizations, wolTileDeviceId = json.stringOrNull("wolTileDeviceId")?.let(Protocol::uuid))
    }
}
