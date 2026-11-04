package com.fy1ng.fy1switch.core

import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import org.json.JSONObject
import java.io.IOException
import java.time.Clock
import java.time.Duration
import java.util.UUID
import javax.net.ssl.SSLException

/** WOL has its own network admission; all durable writes still use DeviceRepository's single store. */
class WolRepository(
    private val repository: DeviceRepository,
    private val transport: WolTransport?,
    private val clock: Clock,
    private val monotonicMillis: () -> Long,
    private val pause: suspend (Long) -> Unit = { delay(it) },
) {
    private val mutex = Mutex()
    private val mutableUi = MutableStateFlow(WolUiState())
    val ui = mutableUi.asStateFlow()
    private val observing = mutableSetOf<UUID>()

    private fun device(id: UUID) = repository.state.value.devices.find { it.profile.deviceId == id }
        ?: throw WolFailure("not_configured")
    private fun authorization(id: UUID): Pair<WolBinding, WolAuthorization> {
        val binding = device(id).profile.wol ?: throw WolFailure("not_configured")
        return binding to (WolPolicy.authorization(repository.state.value, binding) ?: throw WolFailure("unauthorized"))
    }
    private suspend fun call(auth: WolAuthorization?, endpoint: String, path: String, method: String, body: String? = null): HttpResult {
        val result = (transport ?: throw WolFailure("offline")).send(endpoint, path, method, auth?.accessToken, body)
        if (result.status !in 200..299) throw WolFailure(WolProtocol.error(result.body), result.status)
        return result
    }
    private suspend fun guarded(id: UUID?, block: suspend () -> String): Boolean {
        if (!mutex.tryLock()) return false
        mutableUi.value = WolUiState(busy = true, deviceId = id)
        try {
            repository.initialize()
            if (repository.state.value.storageFailed) throw WolFailure("storage")
            val message = block()
            mutableUi.value = WolUiState(deviceId = id, message = message)
            return true
        } catch (cancel: CancellationException) { throw cancel
        } catch (error: Exception) {
            val code = when (error) {
                is WolFailure -> error.code
                is SSLException -> "security"
                is IOException -> "offline"
                is IllegalArgumentException -> "invalid_request"
                else -> "protocol"
            }
            mutableUi.value = WolUiState(deviceId = id, message = WolPolicy.error(code), errorCode = code)
            return false
        } finally {
            mutableUi.value = mutableUi.value.copy(busy = false)
            mutex.unlock()
        }
    }

    suspend fun register(deviceId: UUID, address: String, code: String, clientName: String): Boolean = guarded(deviceId) {
        device(deviceId)
        val endpoint = WolProtocol.endpoint(address)
        require(code.trim().length in 6..128 && code.trim().none { it.isISOControl() })
        require(repository.state.value.wolAuthorizations.none { it.endpoint == endpoint })
        require(repository.state.value.wolAuthorizations.size < 20)
        val body = JSONObject().put("version", 1).put("registrationCode", code.trim())
            .put("clientName", Protocol.label(clientName)).toString()
        val authorized = WolProtocol.register(call(null, endpoint, "/v1/wol/register", "POST", body).body, endpoint)
        try {
            repository.updateWol { it.copy(wolAuthorizations = it.wolAuthorizations + authorized) }
        } catch (error: Exception) {
            // A credential that could not be stored must not intentionally remain usable remotely.
            withContext(NonCancellable) { runCatching { call(authorized, endpoint, "/v1/wol/clients/me", "DELETE") } }
            throw error
        }
        "已授权此手机. 请选择匹配这台 Windows 电脑的目标并启用远程开机."
    }

    suspend fun configure(deviceId: UUID, address: String, targetId: UUID?, enabled: Boolean): Boolean = guarded(deviceId) {
        device(deviceId)
        val endpoint = WolProtocol.endpoint(address)
        val target = targetId ?: throw WolFailure("not_configured")
        // The gateway uses the Windows identity, so a Host response cannot confirm a different PC.
        require(target == deviceId)
        val binding = WolBinding(endpoint, target, enabled)
        if (enabled && WolPolicy.authorization(repository.state.value, binding) == null) throw WolFailure("device_forbidden")
        repository.updateWol { state -> state.copy(devices = state.devices.map {
            if (it.profile.deviceId == deviceId) it.copy(profile = it.profile.copy(wol = binding),
                wolOperation = it.wolOperation.takeIf { _ -> it.profile.wol?.endpoint == endpoint && it.profile.wol.targetId == target }) else it
        }) }
        "远程开机设置已保存."
    }

    suspend fun setTileTarget(deviceId: UUID?): Boolean = guarded(deviceId) {
        deviceId?.let { require(repository.state.value.devices.any { value -> value.profile.deviceId == it }) }
        repository.updateWol { it.copy(wolTileDeviceId = deviceId) }
        if (deviceId == null) "远程开机磁贴跟随当前电脑." else "远程开机磁贴已固定目标."
    }

    suspend fun refreshDevices(deviceId: UUID, address: String): Boolean = guarded(deviceId) {
        val endpoint = WolProtocol.endpoint(address)
        val auth = repository.state.value.wolAuthorizations.find { it.endpoint == endpoint } ?: throw WolFailure("unauthorized")
        val updated = WolProtocol.devices(call(auth, endpoint, "/v1/wol/devices", "GET").body, auth)
        repository.updateWol { it.copy(wolAuthorizations = it.wolAuthorizations.map { old -> if (old.endpoint == endpoint) updated else old }) }
        "Gateway 身份验证成功, 授权设备列表已更新."
    }

    suspend fun clear(deviceId: UUID, address: String, revoke: Boolean): Boolean = guarded(deviceId) {
        val endpoint = WolProtocol.endpoint(address)
        val auth = repository.state.value.wolAuthorizations.find { it.endpoint == endpoint }
        if (revoke && auth != null) call(auth, endpoint, "/v1/wol/clients/me", "DELETE")
        repository.updateWol { state -> state.copy(
            wolAuthorizations = state.wolAuthorizations.filterNot { it.endpoint == endpoint },
            devices = state.devices.map { if (it.profile.wol?.endpoint == endpoint)
                it.copy(profile = it.profile.copy(wol = null), wolOperation = null) else it },
        ) }
        if (revoke) "已撤销此手机在该 Gateway 的授权, 所有关联电脑均已清除配置."
        else "已清除此手机的本地授权. 服务端权限仍存在, 请在 VPS 撤销."
    }

    suspend fun check(deviceId: UUID): Boolean = guarded(deviceId) {
        val (binding, auth) = authorization(deviceId)
        val now = clock.instant()
        WolProtocol.check(call(auth, binding.endpoint, "/v1/wol/devices/${binding.targetId}/check", "POST",
            WolProtocol.request(binding.targetId, now, now.plusSeconds(30))).body, binding.targetId)
        "Gateway 与 OpenWrt 连接及目标配置检查通过. 未发送唤醒包, 未验证电脑网卡或实际发包权限."
    }

    suspend fun wake(deviceId: UUID, allowNewAfterUnknown: Boolean = false): Boolean = guarded(deviceId) {
        val (binding, auth) = authorization(deviceId)
        if (!binding.enabled) throw WolFailure("not_configured")
        val previous = device(deviceId).wolOperation
        if (previous != null) {
            val age = Duration.between(previous.issuedAt, clock.instant()).seconds
            if (previous.status in setOf(WolStatus.Pending, WolStatus.Unknown) && !allowNewAfterUnknown) {
                queryInternal(deviceId, binding, auth, previous)
                return@guarded "已查询原唤醒请求, 没有重新发送."
            }
            if (age < if (previous.status == WolStatus.Failed) 15 else 90) throw WolFailure("device_cooldown")
        }
        val now = clock.instant()
        val pending = WolOperation(UUID.randomUUID(), binding.targetId, WolStatus.Pending, now, now.plusSeconds(30), now)
        saveOperation(deviceId, binding, pending)
        var finished = false
        try {
            val result = call(auth, binding.endpoint, "/v1/wol/devices/${binding.targetId}/wake", "POST",
                WolProtocol.request(binding.targetId, now, pending.expiresAt, pending.operationId))
            saveOperation(deviceId, binding, WolProtocol.operation(result.body, pending))
            finished = true
        } catch (error: WolFailure) {
            // A gateway/proxy error may occur after SSH has sent the packet but before its result was saved.
            // Preserve the operation for queries unless the protocol proves rejection before execution.
            saveOperation(deviceId, binding, pending.copy(
                status = if (error.rejectedBeforeExecution) WolStatus.Failed else WolStatus.Unknown,
                updatedAt = clock.instant(), errorCode = error.code))
            finished = true
            throw error
        } finally {
            if (!finished) withContext(NonCancellable) {
                saveOperation(deviceId, binding, pending.copy(status = WolStatus.Unknown, updatedAt = clock.instant()))
            }
        }
        WolPolicy.result(device(deviceId).wolOperation, false, clock.instant())
    }

    suspend fun query(deviceId: UUID): Boolean = guarded(deviceId) {
        val (binding, auth) = authorization(deviceId)
        val operation = device(deviceId).wolOperation ?: throw WolFailure("operation_not_found")
        queryInternal(deviceId, binding, auth, operation)
        WolPolicy.result(device(deviceId).wolOperation, repository.hasFreshHost(deviceId), clock.instant())
    }

    private suspend fun queryInternal(id: UUID, binding: WolBinding, auth: WolAuthorization, operation: WolOperation) {
        val result = call(auth, binding.endpoint, "/v1/wol/devices/${binding.targetId}/operations/${operation.operationId}", "GET")
        saveOperation(id, binding, WolProtocol.operation(result.body, operation))
    }
    private suspend fun saveOperation(id: UUID, binding: WolBinding, operation: WolOperation) = repository.updateWol { state ->
        require(state.devices.any { it.profile.deviceId == id && it.profile.wol == binding })
        state.copy(devices = state.devices.map { if (it.profile.deviceId == id) it.copy(wolOperation = operation) else it })
    }

    /** Lifecycle callers cancel this coroutine. It only reads, never resends WOL or creates background work. */
    suspend fun observeHost(deviceId: UUID, operationId: UUID, stillVisible: () -> Boolean) {
        var acquired = false
        try {
            val initial = repository.state.value.devices.find { it.profile.deviceId == deviceId }
                ?.wolOperation?.takeIf { it.operationId == operationId } ?: return
            val remaining = Duration.between(clock.instant(), initial.issuedAt.plusSeconds(90)).toMillis().coerceIn(0, 90_000)
            if (remaining == 0L) return
            val started = monotonicMillis()
            withTimeoutOrNull(remaining) {
                // A second visible surface can take over after the first one leaves. It does not poll in parallel.
                while (!acquired) {
                    if (!stillVisible()) return@withTimeoutOrNull
                    acquired = synchronized(observing) { observing.add(deviceId) }
                    if (!acquired) delay(250)
                }
                while (stillVisible() && monotonicMillis() - started < remaining) {
                    val current = repository.state.value.devices.find { it.profile.deviceId == deviceId } ?: return@withTimeoutOrNull
                    if (current.wolOperation?.operationId != operationId || !WolPolicy.configured(repository.state.value, current)) return@withTimeoutOrNull
                    if (current.wolOperation.status == WolStatus.Failed) return@withTimeoutOrNull
                    if (current.wolOperation.status in setOf(WolStatus.Pending, WolStatus.Unknown) && !ui.value.busy) {
                        query(deviceId)
                        if (ui.value.errorCode in setOf("unauthorized", "device_forbidden", "security", "protocol", "storage")) return@withTimeoutOrNull
                    }
                    if (!repository.state.value.busy) {
                        val feedback = repository.refresh(deviceId, live = false)
                        if (feedback in setOf(Feedback.Security, Feedback.Unauthorized, Feedback.Protocol, Feedback.Storage)) return@withTimeoutOrNull
                        if (repository.hasFreshHost(deviceId)) {
                            if (repository.state.value.defaultDeviceId == deviceId) repository.recover(deviceId)
                            return@withTimeoutOrNull
                        }
                    }
                    pause(5_000)
                }
            }
        } finally { if (acquired) synchronized(observing) { observing.remove(deviceId) } }
    }
}
