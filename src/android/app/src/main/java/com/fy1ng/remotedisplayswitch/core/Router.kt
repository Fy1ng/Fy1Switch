package com.fy1ng.remotedisplayswitch.core

import java.io.IOException
import java.time.Clock
import java.time.Duration
import javax.net.ssl.SSLException

enum class Route { Direct, Relay }
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
    suspend fun send(device: DeviceProfile, request: RemoteCommand): Delivery {
        if (device.deviceId != request.deviceId) return Delivery.Failure(Feedback.Protocol, false)
        val initialRemaining = Duration.between(clock.instant(), request.expiresAt).toMillis()
        if (initialRemaining <= 0 || initialRemaining > 120_000) return Delivery.Failure(Feedback.Expired, false)
        val body = Protocol.command(request)
        val routes = if (device.relay == null) listOf(Route.Direct) else listOf(Route.Direct, Route.Relay)
        var attempted = false
        for (route in routes) {
            val remaining = Duration.between(clock.instant(), request.expiresAt).toMillis()
            if (remaining <= 0) return Delivery.Failure(Feedback.Expired, attempted)
            val timeout = minOf(remaining, if (route == Route.Direct) 15_000L else 12_000L)
            val response = try {
                attempted = true
                transport.control(device, route, body, timeout)
            } catch (_: ExpiredRequestException) {
                return Delivery.Failure(Feedback.Expired, attempted)
            } catch (_: SSLException) {
                return Delivery.Failure(Feedback.Security, attempted)
            } catch (_: IOException) {
                continue
            } catch (_: IllegalArgumentException) {
                return Delivery.Failure(Feedback.Protocol, attempted)
            }
            if (response.status == 401 || response.status == 403) return Delivery.Failure(Feedback.Unauthorized, attempted)
            if (response.status !in 200..299) return Delivery.Failure(Feedback.Failed, attempted)
            val reply = try {
                Protocol.reply(response.body, request)
            } catch (_: Exception) {
                return Delivery.Failure(Feedback.Protocol, attempted)
            }
            return Delivery.Reply(reply, route)
        }
        return Delivery.Failure(Feedback.Offline, attempted)
    }
}
