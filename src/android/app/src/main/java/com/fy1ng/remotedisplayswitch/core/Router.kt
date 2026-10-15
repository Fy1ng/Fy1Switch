package com.fy1ng.remotedisplayswitch.core

import java.io.IOException
import java.time.Clock
import java.time.Duration
import java.time.Instant
import java.util.UUID
import javax.net.ssl.SSLException

enum class Route(val label: String) { Direct("局域网"), Tailscale("Tailscale"), Relay("Relay") }
class ExpiredRequestException : Exception()
data class HttpResult(val status: Int, val body: String) {
    override fun toString() = "HttpResult(status=" + status + ", body=redacted)"
}

interface Transport {
    suspend fun pair(bundle: PairBundle, clientName: String, timeoutMillis: Long): HttpResult
    suspend fun control(device: DeviceProfile, route: Route, body: String, timeoutMillis: Long): HttpResult
}

sealed interface Delivery {
    data class Reply(val value: RemoteReply, val route: Route) : Delivery
    data class Failure(val feedback: Feedback, val mayHaveExecuted: Boolean) : Delivery
}

class Router(private val transport: Transport, private val clock: Clock = Clock.systemUTC()) {
    private data class PathKey(
        val deviceId: UUID, val route: Route, val endpoint: String,
        val pin: String, val token: String, val relayToken: String?,
    )
    private val cooldowns = mutableMapOf<PathKey, Instant>()

    private fun pathKey(device: DeviceProfile, route: Route) = PathKey(
        device.deviceId, route, when (route) {
            Route.Direct -> device.endpoint
            Route.Tailscale -> requireNotNull(device.tailscaleEndpoint)
            Route.Relay -> requireNotNull(device.relay).endpoint
        }, device.certificatePin, device.deviceToken,
        if (route == Route.Relay) device.relay?.controllerToken else null,
    )

    private fun limit(device: DeviceProfile, route: Route) = when {
        route == Route.Relay -> 12_000L
        device.tailscaleEndpoint != null -> 6_000L
        else -> 15_000L
    }

    suspend fun send(device: DeviceProfile, request: RemoteCommand): Delivery {
        if (device.deviceId != request.deviceId) return Delivery.Failure(Feedback.Protocol, false)
        val initialRemaining = Duration.between(clock.instant(), request.expiresAt).toMillis()
        if (initialRemaining <= 0 || initialRemaining > 120_000) return Delivery.Failure(Feedback.Expired, false)
        val body = Protocol.command(request)
        val configured = buildList {
            add(Route.Direct)
            if (device.tailscaleEndpoint != null) add(Route.Tailscale)
            if (device.relay != null) add(Route.Relay)
        }
        val keys = configured.associateWith { pathKey(device, it) }
        cooldowns.keys.removeAll { it.deviceId == device.deviceId && it !in keys.values }
        val failedThisRequest = mutableSetOf<PathKey>()
        var attempted = false
        try {
            for ((index, route) in configured.withIndex()) {
                val key = keys.getValue(route)
                var remaining = Duration.between(clock.instant(), request.expiresAt).toMillis()
                if (remaining <= 0) return Delivery.Failure(Feedback.Expired, attempted)
                val retryAt = cooldowns[key]
                var fallbackReserve = 0L
                if (retryAt != null) {
                    if (retryAt > clock.instant()) continue
                    val fallback = configured.drop(index + 1).firstOrNull { keys.getValue(it) !in cooldowns }
                    fallbackReserve = fallback?.let { limit(device, it) } ?: 0L
                    val reserve = if (fallback == null) 1_000L else fallbackReserve
                    if (remaining <= reserve) continue
                    val probeLimit = if (fallback == null) limit(device, route) else 1_000L
                    val probe = RemoteCommand(UUID.randomUUID(), device.deviceId, "status", request.expiresAt)
                    when (val result = exchange(device, route, probe, Protocol.command(probe), minOf(probeLimit, remaining - reserve))) {
                        is Delivery.Failure -> {
                            if (result.feedback != Feedback.Offline) return result.copy(mayHaveExecuted = attempted)
                            failedThisRequest.add(key)
                            continue
                        }
                        is Delivery.Reply -> {
                            if (!result.value.success) return Delivery.Failure(Feedback.Failed, attempted)
                            if (result.value.status == null) return Delivery.Failure(Feedback.Protocol, attempted)
                            cooldowns.remove(key)
                        }
                    }
                    remaining = Duration.between(clock.instant(), request.expiresAt).toMillis()
                    if (remaining <= 0) return Delivery.Failure(Feedback.Expired, attempted)
                }
                val sendBudget = remaining - fallbackReserve
                if (sendBudget <= 0) continue
                attempted = true
                val result = exchange(device, route, request, body, minOf(sendBudget, limit(device, route)))
                if (result is Delivery.Failure && result.feedback == Feedback.Offline) {
                    failedThisRequest.add(key)
                    continue
                }
                return result
            }
            return Delivery.Failure(Feedback.Offline, attempted)
        } finally {
            val retryAt = clock.instant().plusSeconds(5)
            failedThisRequest.forEach { cooldowns[it] = retryAt }
        }
    }

    private suspend fun exchange(device: DeviceProfile, route: Route, request: RemoteCommand, body: String, timeout: Long): Delivery {
        val response = try {
            transport.control(device, route, body, timeout)
        } catch (_: ExpiredRequestException) {
            return Delivery.Failure(Feedback.Expired, true)
        } catch (_: SSLException) {
            return Delivery.Failure(Feedback.Security, true)
        } catch (_: IOException) {
            return Delivery.Failure(Feedback.Offline, true)
        } catch (_: IllegalArgumentException) {
            return Delivery.Failure(Feedback.Protocol, true)
        }
        if (response.status == 401 || response.status == 403) return Delivery.Failure(Feedback.Unauthorized, true)
        if (response.status !in 200..299) return Delivery.Failure(Feedback.Failed, true)
        val reply = try {
            Protocol.reply(response.body, request)
        } catch (_: Exception) {
            return Delivery.Failure(Feedback.Protocol, true)
        }
        return Delivery.Reply(reply, route)
    }
}
