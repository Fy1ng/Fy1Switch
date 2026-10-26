package com.fy1ng.fy1switch.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
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
    private val monotonicMillis: () -> Long = { System.nanoTime() / 1_000_000 },
) {
    private val mutableState = MutableStateFlow(AppState())
    val state: StateFlow<AppState> = mutableState.asStateFlow()
    private val mutex = Mutex()
    private val router = Router(transport, clock)
    private val admission = Any()
    private var backgroundRead: Deferred<Feedback>? = null
    private var foregroundWaiters = 0
    private val relayLearningAttempts = mutableMapOf<UUID, Pair<String, java.time.Instant>>()

    suspend fun initialize() = initializeInternal()

    private suspend fun initializeInternal(notifyFailure: Boolean = true) {
        if (state.value.loaded) return
        mutex.withLock {
            if (!state.value.loaded) withContext(Dispatchers.IO) {
                mutableState.value = try {
                    store.read().let { restored ->
                        restored.copy(loaded = true, busy = false, request = null, devices = restored.devices.map { device ->
                            device.copy(lastLiveReadAtMillis = null,
                                requiresRefresh = device.requiresRefresh && device.operation?.outcome != OperationOutcome.Failed)
                        })
                    }
                } catch (_: Exception) {
                    AppState(loaded = true, storageFailed = true, notice = if (notifyFailure) Feedback.Storage else null)
                }
            }
        }
    }

    suspend fun pair(text: String, clientName: String): Feedback = guarded {
        val bundle = Protocol.pairBundle(text, clock.instant())
        val phoneName = Protocol.label(clientName)
        require(state.value.devices.size < 20 || state.value.devices.any { it.profile.deviceId == bundle.deviceId })
        val remaining = Duration.between(clock.instant(), bundle.expiresAt).toMillis()
        if (remaining <= 0) return@guarded Feedback.Expired
        val response = try {
            transport.pair(bundle, phoneName, minOf(remaining, 7_000))
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
            bundle.deviceId, paired.clientId, existing?.profile?.name ?: paired.deviceName ?: "Windows 电脑", bundle.endpoint,
            bundle.certificatePin, paired.token, null,
            bundle.tailscaleEndpoint.takeIf { existing?.profile?.autoLearnTailscale != false } ?: existing?.profile?.tailscaleEndpoint,
            existing?.profile?.autoLearnTailscale ?: true,
            existing?.profile?.autoLearnRelay ?: true,
        )
        val device = DeviceState(profile, feedback = Feedback.Paired)
        persist(state.value.copy(
            devices = state.value.devices.filterNot { it.profile.deviceId == profile.deviceId } + device,
            defaultDeviceId = profile.deviceId,
        ))
        readStatusInternal(profile.deviceId, live = false)
    }

    suspend fun pairRelay(relayEndpoint: String, code: String, clientName: String): Feedback = guarded {
        val endpoint = configurationValue(Feedback.InvalidRelayEndpoint) { Protocol.endpoint(relayEndpoint) }
        val pairingCode = configurationValue(Feedback.InvalidRelayCode) { Protocol.relayCode(code) }
        val phoneName = Protocol.label(clientName)
        require(state.value.devices.size < 20)
        val response = try {
            transport.pairRelay(endpoint, pairingCode, phoneName, 15_000)
        } catch (_: ExpiredRequestException) { return@guarded Feedback.Expired
        } catch (_: SSLException) { return@guarded Feedback.Security
        } catch (_: IOException) { return@guarded Feedback.Offline }
        if (response.status !in 200..299) return@guarded if (response.status in setOf(400, 401, 403, 404))
            Feedback.InvalidInput else relayFailure(response.status)
        val paired = try { Protocol.relayPairReply(response.body) } catch (_: Exception) { return@guarded Feedback.Protocol }
        currentCoroutineContext().ensureActive()
        val existing = state.value.devices.find { it.profile.deviceId == paired.deviceId }?.profile
        val profile = DeviceProfile(paired.deviceId, paired.clientId, existing?.name ?: paired.deviceName ?: "Windows 电脑", paired.endpoint,
            paired.certificatePin, paired.deviceToken, RelayConfig(endpoint, paired.controllerToken), paired.tailscaleEndpoint,
            existing?.autoLearnTailscale ?: true, existing?.autoLearnRelay ?: true)
        persist(state.value.copy(devices = state.value.devices.filterNot { it.profile.deviceId == paired.deviceId } +
            DeviceState(profile, feedback = Feedback.Paired), defaultDeviceId = paired.deviceId))
        Feedback.Paired
    }

    suspend fun configureConnection(
        deviceId: UUID, name: String, relayEndpoint: String, tailscaleEndpoint: String,
        autoLearnTailscale: Boolean, autoLearnRelay: Boolean, refreshRelay: Boolean = false,
    ): Feedback = guarded(invalidInputFeedback = Feedback.InvalidConfiguration) {
        val device = configurationValue(Feedback.DeviceMissing) { find(deviceId) }
        val savedName = configurationValue(Feedback.InvalidName) { Protocol.label(name) }
        val endpoint = if (relayEndpoint.isBlank()) null else configurationValue(Feedback.InvalidRelayEndpoint) { Protocol.endpoint(relayEndpoint) }
        val tailscale = if (tailscaleEndpoint.isBlank()) null else configurationValue(Feedback.InvalidTailscaleEndpoint) { Protocol.endpoint(tailscaleEndpoint) }
        val relay = if (endpoint == null) null else if (!refreshRelay && device.profile.relay?.let { Protocol.sameEndpoint(it.endpoint, endpoint) } == true) {
            device.profile.relay
        } else {
            val response = try {
                transport.authorizeRelay(device.profile, endpoint, 15_000)
            } catch (_: ExpiredRequestException) { return@guarded Feedback.Expired
            } catch (_: SSLException) { return@guarded Feedback.Security
            } catch (_: IOException) { return@guarded Feedback.Offline }
            if (response.status !in 200..299) return@guarded relayFailure(response.status)
            try { Protocol.relayAuthorization(response.body, device.profile, endpoint) }
            catch (_: Exception) { return@guarded Feedback.Protocol }
        }
        currentCoroutineContext().ensureActive()
        val removed = device.profile.relay != null && relay == null
        replace(device.copy(lastLiveReadAtMillis = null, profile = device.profile.copy(name = savedName, relay = relay,
            tailscaleEndpoint = tailscale, autoLearnTailscale = autoLearnTailscale,
            autoLearnRelay = autoLearnRelay && !removed)))
        relayLearningAttempts.remove(deviceId)
        Feedback.Saved
    }

    suspend fun select(deviceId: UUID): Feedback = guarded {
        require(state.value.devices.any { it.profile.deviceId == deviceId })
        persist(state.value.copy(defaultDeviceId = deviceId, devices = state.value.devices.map {
            if (state.value.defaultDeviceId != deviceId) it.copy(lastLiveReadAtMillis = null) else it
        }))
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
        replace(device.copy(lastLiveReadAtMillis = null, profile = device.profile.copy(name = savedName, relay = relay,
            tailscaleEndpoint = tailscale, autoLearnTailscale = learn,
            autoLearnRelay = device.profile.autoLearnRelay && !(device.profile.relay != null && relay == null))))
        Feedback.Saved
    }

    suspend fun remove(deviceId: UUID): Feedback = guarded {
        val devices = state.value.devices.filterNot { it.profile.deviceId == deviceId }
        persist(state.value.copy(devices = devices, defaultDeviceId =
            if (state.value.defaultDeviceId == deviceId) devices.firstOrNull()?.profile?.deviceId else state.value.defaultDeviceId))
        relayLearningAttempts.remove(deviceId)
        Feedback.Saved
    }

    suspend fun refresh(deviceId: UUID, live: Boolean = true): Feedback = guarded(
        if (live) RepositoryAction.Refresh else RepositoryAction.Other, deviceId,
    ) { readStatusInternal(deviceId, live) }

    fun hasFreshPower(deviceId: UUID, maxAgeMillis: Long = 25_000): Boolean {
        val snapshot = state.value
        if (!snapshot.loaded || snapshot.storageFailed || snapshot.defaultDeviceId != deviceId || maxAgeMillis < 0) return false
        val device = snapshot.selected ?: return false
        if (StatePolicy.confirmedPower(device) == DisplayPower.Unknown) return false
        val readAt = device.lastLiveReadAtMillis ?: return false
        return monotonicMillis() - readAt in 0..maxAgeMillis
    }

    // This child can be preempted without cancelling the service's polling coroutine.
    suspend fun backgroundRefresh(deviceId: UUID): Feedback {
        if (state.value.busy) return Feedback.Busy
        initializeInternal(notifyFailure = false)
        if (state.value.storageFailed) return Feedback.Storage
        return coroutineScope {
            val read = synchronized(admission) {
                if (foregroundWaiters > 0 || backgroundRead != null || state.value.busy ||
                    state.value.defaultDeviceId != deviceId || state.value.selected == null) return@coroutineScope Feedback.Busy
                async(Dispatchers.IO, start = CoroutineStart.LAZY) {
                    val locked = synchronized(admission) {
                        foregroundWaiters == 0 && !state.value.busy && !state.value.storageFailed &&
                            state.value.defaultDeviceId == deviceId && mutex.tryLock()
                    }
                    if (!locked) return@async if (state.value.storageFailed) Feedback.Storage else Feedback.Busy
                    try {
                        currentCoroutineContext().ensureActive()
                        readStatusInternal(deviceId, live = true)
                    } catch (_: StorageFailure) {
                        storageFailure(notify = false)
                        Feedback.Storage
                    } finally {
                        mutex.unlock()
                    }
                }.also { backgroundRead = it }
            }
            try {
                read.start()
                read.await()
            } catch (_: BackgroundReadPreempted) {
                Feedback.Busy
            } finally {
                withContext(NonCancellable) { read.join() }
                synchronized(admission) { if (backgroundRead === read) backgroundRead = null }
            }
        }
    }

    suspend fun recover(deviceId: UUID): Feedback = guarded(
        RepositoryAction.Refresh, deviceId, waitForIdle = true,
    ) {
        if (state.value.defaultDeviceId != deviceId) return@guarded Feedback.Busy
        val device = find(deviceId)
        if (device.requiresRefresh && StatePolicy.unresolved(device.operation)) {
            val queried = queryOperationInternal(deviceId)
            if (queried in recoveryStopFeedback) return@guarded queried
            if (StatePolicy.confirmedPower(find(deviceId)) != DisplayPower.Unknown) return@guarded queried
            // A failed route remains in Router's five-second cooldown after an uncertain write.
            if (queried == Feedback.Offline) delay(5_000)
        }
        var result = Feedback.Updated
        repeat(2) { attempt ->
            result = readStatusInternal(deviceId, live = true)
            val current = find(deviceId)
            if (result != Feedback.Updated || StatePolicy.confirmedPower(current) != DisplayPower.Unknown ||
                current.status?.available != true) return@guarded result
            if (attempt == 0) delay(500)
        }
        result
    }

    private suspend fun readStatusInternal(deviceId: UUID, live: Boolean): Feedback {
        val device = find(deviceId)
        val request = RemoteCommand(UUID.randomUUID(), deviceId, if (live) "refresh" else "status", clock.instant().plusSeconds(30))
        val delivery = router.send(device.profile, request)
        val feedback = applyDelivery(deviceId, request, delivery)
        if (delivery is Delivery.Reply && delivery.route == Route.Direct && delivery.value.success &&
            feedback == Feedback.Updated) learnRelay(deviceId, delivery.value.status?.relayEndpoint)
        return feedback
    }

    private suspend fun learnRelay(deviceId: UUID, relayEndpoint: String?) {
        if (relayEndpoint == null) return
        val device = find(deviceId)
        if (!device.profile.autoLearnRelay || device.profile.relay?.let { Protocol.sameEndpoint(it.endpoint, relayEndpoint) } == true) return
        val previous = relayLearningAttempts[deviceId]
        if (previous?.first == relayEndpoint && previous.second > clock.instant()) return
        relayLearningAttempts[deviceId] = relayEndpoint to clock.instant().plusSeconds(60)
        val response = try { transport.learnRelay(device.profile, 5_000) }
        catch (exception: CancellationException) { throw exception }
        catch (_: IOException) { return }
        catch (_: ExpiredRequestException) { return }
        catch (_: IllegalArgumentException) { return }
        if (response.status !in 200..299) return
        val relay = try { Protocol.relayConfiguration(response.body, device.profile, relayEndpoint) }
        catch (_: Exception) { return }
        currentCoroutineContext().ensureActive()
        replace(find(deviceId).let { it.copy(profile = it.profile.copy(relay = relay)) })
    }

    private fun relayFailure(status: Int): Feedback = when (status) {
        400 -> Feedback.InvalidInput
        401, 403 -> Feedback.Unauthorized
        409, 429 -> Feedback.Busy
        410 -> Feedback.Expired
        503, 504 -> Feedback.Offline
        else -> Feedback.Failed
    }

    suspend fun set(deviceId: UUID, intent: PowerIntent): Feedback = guarded(RepositoryAction.Set, deviceId) {
        require(state.value.defaultDeviceId == deviceId)
        if (!StatePolicy.canSet(state.value.copy(busy = false), intent)) return@guarded Feedback.Busy
        val device = find(deviceId)
        val targetId = requireNotNull(device.status?.targetId)
        val operationId = UUID.randomUUID()
        val request = RemoteCommand(UUID.randomUUID(), deviceId, "set", clock.instant().plusSeconds(30), targetId, intent, operationId)
        val pending = OperationRecord(operationId, targetId, intent, null, OperationOutcome.Pending, null, null, clock.instant())
        replace(device.copy(operation = pending, feedback = Feedback.Pending, requiresRefresh = true, lastLiveReadAtMillis = null, status = device.status.let {
            it.copy(observation = it.observation.copy(state = DisplayPower.Unknown))
        }))
        applyDelivery(deviceId, request, router.send(device.profile, request))
    }

    suspend fun queryOperation(deviceId: UUID): Feedback = guarded { queryOperationInternal(deviceId) }

    private suspend fun queryOperationInternal(deviceId: UUID): Feedback {
        val device = find(deviceId)
        val operation = device.operation ?: return Feedback.NotFound
        val request = RemoteCommand(
            UUID.randomUUID(), deviceId, "operation", clock.instant().plusSeconds(30),
            targetId = operation.targetId, operationId = operation.operationId,
        )
        return applyDelivery(deviceId, request, router.send(device.profile, request))
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
                replace(device.copy(operation = operation, connected = false, feedback = feedback, lastLiveReadAtMillis = null,
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
                    replace(device.copy(operation = operation, connected = false, feedback = feedback, lastLiveReadAtMillis = null,
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
                val updated = device.copy(profile = profile, status = status, operation = operation, connected = connected,
                    feedback = feedback, requiresRefresh = requiresRefresh, lastSuccessfulRoute = delivery.route)
                val trusted = StatePolicy.confirmedPower(updated) != DisplayPower.Unknown
                val readAt = when {
                    trusted && freshRead -> monotonicMillis()
                    trusted && request.command == "set" && confirmedRead && status?.observation == confirmedObservation -> monotonicMillis()
                    !trusted || request.command in setOf("refresh", "set", "operation") || status != device.status -> null
                    else -> device.lastLiveReadAtMillis
                }
                replace(updated.copy(lastLiveReadAtMillis = readAt))
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
        waitForIdle: Boolean = false,
        action: suspend () -> Feedback,
    ): Feedback {
        initialize()
        if (state.value.storageFailed) return Feedback.Storage
        val background = synchronized(admission) {
            foregroundWaiters += 1
            backgroundRead.also { it?.cancel(BackgroundReadPreempted()) }
        }
        val locked = try {
            background?.join()
            if (waitForIdle) mutex.lockWithin(30_000) else mutex.tryLock()
        } finally {
            synchronized(admission) { foregroundWaiters -= 1 }
        }
        if (!locked) return Feedback.Busy
        if (state.value.storageFailed) {
            mutex.unlock()
            return Feedback.Storage
        }
        val request = RepositoryRequest(UUID.randomUUID(), kind, deviceId)
        var result: Feedback? = null
        mutableState.value = state.value.copy(busy = true, notice = null, request = request)
        try {
            val feedback = withContext(Dispatchers.IO) { action() }
            result = feedback
            mutableState.value = state.value.copy(notice = feedback)
            return feedback
        } catch (exception: CancellationException) {
            if (kind == RepositoryAction.Set) withContext(NonCancellable + Dispatchers.IO) {
                try {
                    persist(state.value.copy(devices = state.value.devices.map {
                        if (it.profile.deviceId == deviceId && it.operation?.outcome == OperationOutcome.Pending) it.copy(
                            operation = it.operation.copy(outcome = OperationOutcome.Unknown, updatedAt = clock.instant()),
                            connected = false, feedback = Feedback.Unknown, requiresRefresh = true, lastLiveReadAtMillis = null,
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

    private fun storageFailure(notify: Boolean = true) {
        mutableState.value = state.value.copy(storageFailed = true, notice = if (notify) Feedback.Storage else state.value.notice,
            devices = state.value.devices.map { it.copy(lastLiveReadAtMillis = null) })
    }

    private fun <T> configurationValue(feedback: Feedback, read: () -> T): T = try { read() }
    catch (exception: Exception) {
        if (exception is IllegalArgumentException || exception is URISyntaxException) throw ConfigurationInputException(feedback)
        throw exception
    }

    private class ConfigurationInputException(val feedback: Feedback) : RuntimeException(feedback.name)
    private class StorageFailure : RuntimeException()
    private class BackgroundReadPreempted : CancellationException("Foreground request has priority")

    private companion object {
        val recoveryStopFeedback = setOf(Feedback.Security, Feedback.Unauthorized, Feedback.Protocol,
            Feedback.Storage, Feedback.InvalidInput, Feedback.Expired)
    }
}
