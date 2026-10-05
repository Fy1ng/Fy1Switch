package com.fy1ng.remotedisplayswitch.core

import org.json.JSONObject
import java.time.Clock
import java.time.Instant
import java.time.ZoneId
import java.time.ZoneOffset
import java.util.UUID

internal val NOW: Instant = Instant.parse("2026-10-05T05:00:00Z")
internal val DEVICE: UUID = UUID.fromString("11111111-1111-4111-8111-111111111111")
internal val OTHER: UUID = UUID.fromString("22222222-2222-4222-8222-222222222222")
internal val TARGET: UUID = UUID.fromString("33333333-3333-4333-8333-333333333333")
internal val OPERATION: UUID = UUID.fromString("44444444-4444-4444-8444-444444444444")
internal val REQUEST: UUID = UUID.fromString("55555555-5555-4555-8555-555555555555")
internal val CLIENT: UUID = UUID.fromString("66666666-6666-4666-8666-666666666666")
internal const val PIN = "sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="

internal fun profile(relay: Boolean = false, deviceId: UUID = DEVICE) = DeviceProfile(
    deviceId, CLIENT, "Test display", "https://192.0.2.1:8443", PIN, "fixture-device-token",
    if (relay) RelayConfig("https://relay.example", "fixture-controller-token") else null,
)
internal fun observation(power: DisplayPower = DisplayPower.On, at: Instant = NOW.minusSeconds(5)) = Observation(power, 1, at, "ddc")
internal fun status(power: DisplayPower = DisplayPower.On, deviceId: UUID = DEVICE) = RemoteStatus(deviceId, TARGET, "Monitor", observation(power), available = true, busy = false)
internal fun operation(outcome: OperationOutcome = OperationOutcome.Confirmed) = OperationRecord(
    OPERATION, TARGET, PowerIntent.Toggle, PowerIntent.Off, outcome, null, observation(DisplayPower.Off, NOW), NOW,
)
internal fun request(command: String = "set", expires: Instant = NOW.plusSeconds(30)) = RemoteCommand(
    REQUEST, DEVICE, command, expires, TARGET, if (command == "set") PowerIntent.Toggle else null,
    if (command in setOf("set", "operation")) OPERATION else null,
)
internal fun activeState(relay: Boolean = false) = AppState(
    listOf(DeviceState(profile(relay), status(), connected = true)), DEVICE, loaded = true,
)
internal fun replyJson(body: String, status: RemoteStatus? = null, operation: OperationRecord? = null, success: Boolean = true): String =
    JSONObject().put("requestId", JSONObject(body).getString("requestId")).put("success", success)
        .put("errorCode", JSONObject.NULL).put("version", 1)
        .put("status", status?.let(Protocol::statusJson) ?: JSONObject.NULL)
        .put("operation", operation?.let(Protocol::operationJson) ?: JSONObject.NULL).toString()

internal class MutableClock(var now: Instant = NOW) : Clock() {
    override fun instant(): Instant = now
    override fun getZone(): ZoneId = ZoneOffset.UTC
    override fun withZone(zone: ZoneId): Clock = this
}

internal class MemoryStore(var value: AppState = activeState()) : StateStore {
    var writes = 0
    var failWrite = false
    var failRead = false
    override fun read(): AppState {
        check(!failRead)
        return value
    }
    override fun write(state: AppState) {
        check(!failWrite)
        value = state.copy(busy = false)
        writes += 1
    }
}

internal data class Attempt(val device: DeviceProfile, val route: Route, val body: String, val timeout: Long)
internal class FakeTransport : Transport {
    val attempts = mutableListOf<Attempt>()
    var pairCalls = 0
    var pairHandler: suspend (PairBundle, String) -> HttpResult = { bundle, _ ->
        HttpResult(200, JSONObject().put("deviceId", bundle.deviceId.toString()).put("clientId", CLIENT.toString()).put("token", "fixture-token").toString())
    }
    var handler: suspend (Attempt) -> HttpResult = { HttpResult(200, replyJson(it.body, status = status(deviceId = it.device.deviceId))) }
    override suspend fun pair(bundle: PairBundle, clientName: String, timeoutMillis: Long): HttpResult {
        pairCalls += 1
        return pairHandler(bundle, clientName)
    }
    override suspend fun control(device: DeviceProfile, route: Route, body: String, timeoutMillis: Long): HttpResult {
        val attempt = Attempt(device, route, body, timeoutMillis)
        attempts += attempt
        return handler(attempt)
    }
}

internal fun bundleJson(deviceId: UUID = DEVICE, expiresAt: Instant = NOW.plusSeconds(300)): String = JSONObject()
    .put("version", 1).put("deviceId", deviceId.toString()).put("endpoint", "https://192.0.2.1:8443")
    .put("certificatePin", PIN).put("code", "fixture-pair-code").put("expiresAt", expiresAt.toString()).toString()
