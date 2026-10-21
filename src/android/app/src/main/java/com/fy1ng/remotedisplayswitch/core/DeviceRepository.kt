package com.fy1ng.remotedisplayswitch.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.URISyntaxException
import java.time.Clock
import java.time.Duration
import java.util.UUID
import javax.net.ssl.SSLException

class DeviceRepository(
    private val store: StateStore,
    private val transport: Transport,
    private val clock: Clock = Clock.systemUTC(),
) {
    private val mutableState = MutableStateFlow(AppState())
    val state: StateFlow<AppState> = mutableState.asStateFlow()
    private val mutex = Mutex()
    private val router = Router(transport, clock)

    suspend fun initialize() {
        if (state.value.loaded) return
        mutex.withLock {
            if (!state.value.loaded) withContext(Dispatchers.IO) {
                mutableState.value = try {
                    store.read().let { restored ->
                        restored.copy(loaded = true, busy = false, request = null, devices = restored.devices.map { device ->
                            if (device.operation?.outcome == OperationOutcome.Failed) device.copy(requiresRefresh = false) else device
                        })
                    }
                } catch (_: Exception) {
                    AppState(loaded = true, storageFailed = true, notice = Feedback.Storage)
                }
            }
        }
    }

    suspend fun pair(text: String, clientName: String): Feedback = guarded {
        val bundle = Protocol.pairBundle(text, clock.instant())
        val name = Protocol.label(clientName)
        require(state.value.devices.size < 20 || state.value.devices.any { it.profile.deviceId == bundle.deviceId })
        val remaining = Duration.between(clock.instant(), bundle.expiresAt).toMillis()
        if (remaining <= 0) return@guarded Feedback.Expired
        val response = try {
            transport.pair(bundle, name, minOf(remaining, 7_000))
        } catch (_: ExpiredRequestException) {
            return@guarded Feedback.Expired
        } catch (_: SSLException) {
            return@guarded Feedback.Security
        } catch (_: IOException) {
            return@guarded Feedback.Offline
        }
        if (response.status !in 200..299) return@guarded Feedback.InvalidInput
        val paired = try {
            Protocol.pairReply(response.body, bundle.deviceId)
        } catch (_: Exception) {
            return@guarded Feedback.Protocol
        }
        val existing = state.value.devices.find { it.profile.deviceId == bundle.deviceId }
        val profile = DeviceProfile(
            bundle.deviceId, paired.clientId, existing?.profile?.name ?: name, bundle.endpoint,
            bundle.certificatePin, paired.token, existing?.profile?.relay,
            bundle.tailscaleEndpoint.takeIf { existing?.profile?.autoLearnTailscale != false } ?: existing?.profile?.tailscaleEndpoint,
            existing?.profile?.autoLearnTailscale ?: true,
        )
        val device = DeviceState(profile, feedback = Feedback.Paired)
        persist(state.value.copy(
            devices = state.value.devices.filterNot { it.profile.deviceId == profile.deviceId } + device,
            defaultDeviceId = profile.deviceId,
        ))
        readStatusInternal(profile.deviceId, live = false)
    }

    suspend fun select(deviceId: UUID): Feedback = guarded {
        require(state.value.devices.any { it.profile.deviceId == deviceId })
        persist(state.value.copy(defaultDeviceId = deviceId))
        Feedback.Saved
    }

    suspend fun configure(deviceId: UUID, name: String, relayEndpoint: String, relayToken: String, clearRelay: Boolean, tailscaleEndpoint: String? = null, autoLearnTailscale: Boolean? = null): Feedback = guarded(invalidInputFeedback = Feedback.InvalidConfiguration) {
        val device = configurationValue(Feedback.DeviceMissing) { find(deviceId) }
        val savedName = configurationValue(Feedback.InvalidName) { Protocol.label(name) }
        val relay = if (clearRelay || relayEndpoint.isBlank()) null else {
            val endpoint = configurationValue(Feedback.InvalidRelayEndpoint) { Protocol.endpoint(relayEndpoint) }
            val token = if (relayToken.isBlank()) {
                val existing = device.profile.relay ?: throw ConfigurationInputException(Feedback.RelayTokenRequired)
                if (!Protocol.sameEndpoint(existing.endpoint, endpoint)) throw ConfigurationInputException(Feedback.RelayTokenRequired)
                existing.controllerToken
            } else configurationValue(Feedback.InvalidRelayToken) { Protocol.token(relayToken.trim()) }
            if (token == device.profile.deviceToken) throw ConfigurationInputException(Feedback.RelayDeviceTokenConflict)
            RelayConfig(endpoint, token)
        }
        val tailscale = if (tailscaleEndpoint == null) device.profile.tailscaleEndpoint
            else if (tailscaleEndpoint.isBlank()) null else configurationValue(Feedback.InvalidTailscaleEndpoint) { Protocol.endpoint(tailscaleEndpoint) }
        val learn = autoLearnTailscale ?: if (tailscaleEndpoint == null) device.profile.autoLearnTailscale else tailscaleEndpoint.isNotBlank()
        replace(device.copy(profile = device.profile.copy(name = savedName, relay = relay,
            tailscaleEndpoint = tailscale, autoLearnTailscale = learn)))
        Feedback.Saved
    }

    suspend fun remove(deviceId: UUID): Feedback = guarded {
        val devices = state.value.devices.filterNot { it.profile.deviceId == deviceId }
        persist(state.value.copy(devices = devices, defaultDeviceId =
            if (state.value.defaultDeviceId == deviceId) devices.firstOrNull()?.profile?.deviceId else state.value.defaultDeviceId))
        Feedback.Saved
    }

    suspend fun refresh(deviceId: UUID, live: Boolean = true): Feedback = guarded(
        if (live) RepositoryAction.Refresh else RepositoryAction.Other, deviceId,
    ) { readStatusInternal(deviceId, live) }

    private suspend fun readStatusInternal(deviceId: UUID, live: Boolean): Feedback {
        val device = find(deviceId)
        val request = RemoteCommand(UUID.randomUUID(), deviceId, if (live) "refresh" else "status", clock.instant().plusSeconds(30))
        return applyDelivery(deviceId, request, router.send(device.profile, request))
    }

    suspend fun set(deviceId: UUID, intent: PowerIntent): Feedback = guarded(RepositoryAction.Set, deviceId) {
        require(state.value.defaultDeviceId == deviceId)
        if (!StatePolicy.canSet(state.value.copy(busy = false), intent)) return@guarded Feedback.Busy
        val device = find(deviceId)
        val targetId = requireNotNull(device.status?.targetId)
        val operationId = UUID.randomUUID()
        val request = RemoteCommand(UUID.randomUUID(), deviceId, "set", clock.instant().plusSeconds(30), targetId, intent, operationId)
        val pending = OperationRecord(operationId, targetId, intent, null, OperationOutcome.Pending, null, null, clock.instant())
        replace(device.copy(operation = pending, feedback = Feedback.Pending, requiresRefresh = true, status = device.status.let {
            it.copy(observation = it.observation.copy(state = DisplayPower.Unknown))
        }))
        applyDelivery(deviceId, request, router.send(device.profile, request))
    }

    suspend fun queryOperation(deviceId: UUID): Feedback = guarded {
        val device = find(deviceId)
        val operation = device.operation ?: return@guarded Feedback.NotFound
        val request = RemoteCommand(
            UUID.randomUUID(), deviceId, "operation", clock.instant().plusSeconds(30),
            targetId = operation.targetId, operationId = operation.operationId,
        )
        applyDelivery(deviceId, request, router.send(device.profile, request))
    }

    private fun applyDelivery(deviceId: UUID, request: RemoteCommand, delivery: Delivery): Feedback {
        val device = find(deviceId)
        when (delivery) {
            is Delivery.Failure -> {
                val operation = if (request.command == "set") device.operation?.copy(
                    outcome = if (delivery.mayHaveExecuted) OperationOutcome.Unknown else OperationOutcome.Failed,
                    updatedAt = clock.instant(),
                ) else device.operation
                val feedback = if (request.command == "set" && delivery.mayHaveExecuted) Feedback.Unknown else delivery.feedback
                replace(device.copy(operation = operation, connected = false, feedback = feedback,
                    requiresRefresh = if (request.command == "set") delivery.mayHaveExecuted else device.requiresRefresh))
                return feedback
            }
            is Delivery.Reply -> {
                val reply = delivery.value
                if (!reply.success) {
                    val operation = if (request.command == "set") device.operation?.copy(
                        outcome = OperationOutcome.Unknown, updatedAt = clock.instant(),
                    ) else device.operation
                    val feedback = if (request.command == "set") Feedback.Unknown else Feedback.Failed
                    replace(device.copy(operation = operation, connected = false, feedback = feedback,
                        requiresRefresh = device.requiresRefresh || request.command == "set"))
                    return feedback
                }
                var operation = reply.operation ?: device.operation
                val feedback = when (request.command) {
                    "set", "operation" -> if (reply.operation == null) {
                        if (request.command == "set") {
                            operation = operation?.copy(outcome = OperationOutcome.Unknown, updatedAt = clock.instant())
                            Feedback.Unknown
                        } else Feedback.NotFound
                    } else when (reply.operation.outcome) {
                        OperationOutcome.Confirmed -> Feedback.Confirmed
                        OperationOutcome.AcceptedUnconfirmed -> Feedback.AcceptedUnconfirmed
                        OperationOutcome.Pending -> Feedback.Pending
                        OperationOutcome.Unknown -> Feedback.Unknown
                        OperationOutcome.Failed -> Feedback.Failed
                    }
                    else -> if (reply.status != null) Feedback.Updated else Feedback.Protocol
                }
                var status = reply.status ?: device.status
                val confirmedObservation = reply.operation?.takeIf { it.outcome == OperationOutcome.Confirmed }?.observation
                if (status != null && confirmedObservation != null && status.targetId == reply.operation.targetId &&
                    confirmedObservation.observedAt >= status.observation.observedAt) {
                    status = status.copy(observation = confirmedObservation)
                }
                if (request.command in setOf("set", "operation") && StatePolicy.unresolved(operation) && status != null) {
                    status = status.copy(observation = status.observation.copy(state = DisplayPower.Unknown))
                }
                val connected = when {
                    feedback == Feedback.Protocol -> false
                    reply.status != null -> true
                    confirmedObservation != null && status != null -> device.connected
                    else -> device.connected
                }
                val freshRead = request.command == "refresh" && reply.status?.available == true &&
                    !reply.status.busy && reply.status.observation.state != DisplayPower.Unknown
                val confirmedRead = confirmedObservation?.state in setOf(DisplayPower.On, DisplayPower.Off) &&
                    status?.targetId == reply.operation?.targetId
                val requiresRefresh = when {
                    request.command in setOf("set", "operation") && reply.operation?.outcome == OperationOutcome.Failed -> false
                    freshRead || confirmedRead -> false
                    request.command in setOf("set", "operation") && StatePolicy.unresolved(operation) -> true
                    else -> device.requiresRefresh
                }
                val profile = device.profile.copy(tailscaleEndpoint =
                    reply.status?.tailscaleEndpoint.takeIf { device.profile.autoLearnTailscale } ?: device.profile.tailscaleEndpoint)
                replace(device.copy(profile = profile, status = status, operation = operation, connected = connected,
                    feedback = feedback, requiresRefresh = requiresRefresh, lastSuccessfulRoute = delivery.route))
                return feedback
            }
        }
    }

    private fun find(deviceId: UUID): DeviceState = requireNotNull(state.value.devices.find { it.profile.deviceId == deviceId })

    private fun replace(device: DeviceState) = persist(state.value.copy(devices = state.value.devices.map {
        if (it.profile.deviceId == device.profile.deviceId) device else it
    }))

    private fun persist(next: AppState) {
        try { store.write(next) } catch (_: Exception) { throw StorageFailure() }
        mutableState.value = next
    }

    private suspend fun guarded(
        kind: RepositoryAction = RepositoryAction.Other,
        deviceId: UUID? = null,
        invalidInputFeedback: Feedback = Feedback.InvalidInput,
        action: suspend () -> Feedback,
    ): Feedback {
        initialize()
        if (state.value.storageFailed) return Feedback.Storage
        if (!mutex.tryLock()) return Feedback.Busy
        val request = RepositoryRequest(UUID.randomUUID(), kind, deviceId)
        var result: Feedback? = null
        mutableState.value = state.value.copy(busy = true, notice = null, request = request)
        try {
            val feedback = withContext(Dispatchers.IO) { action() }
            result = feedback
            mutableState.value = state.value.copy(notice = feedback)
            return feedback
        } catch (exception: CancellationException) {
            withContext(NonCancellable + Dispatchers.IO) {
                try {
                    persist(state.value.copy(devices = state.value.devices.map {
                        if (it.operation?.outcome == OperationOutcome.Pending) it.copy(
                            operation = it.operation.copy(outcome = OperationOutcome.Unknown, updatedAt = clock.instant()),
                            connected = false, feedback = Feedback.Unknown, requiresRefresh = true,
                        ) else it
                    }))
                } catch (_: StorageFailure) { storageFailure() }
            }
            throw exception
        } catch (exception: ConfigurationInputException) {
            mutableState.value = state.value.copy(notice = exception.feedback)
            result = exception.feedback
            return exception.feedback
        } catch (_: StorageFailure) {
            storageFailure()
            result = Feedback.Storage
            return Feedback.Storage
        } catch (_: Exception) {
            mutableState.value = state.value.copy(notice = invalidInputFeedback)
            result = invalidInputFeedback
            return invalidInputFeedback
        } finally {
            mutableState.value = state.value.copy(busy = false, request = request.copy(result = result))
            mutex.unlock()
        }
    }

    private fun storageFailure() {
        mutableState.value = state.value.copy(storageFailed = true, notice = Feedback.Storage)
    }

    private fun <T> configurationValue(feedback: Feedback, read: () -> T): T = try { read() }
    catch (exception: Exception) {
        if (exception is IllegalArgumentException || exception is URISyntaxException) throw ConfigurationInputException(feedback)
        throw exception
    }

    private class ConfigurationInputException(val feedback: Feedback) : RuntimeException(feedback.name)
    private class StorageFailure : RuntimeException()
}
