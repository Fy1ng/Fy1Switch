package com.fy1ng.remotedisplayswitch.network

import com.fy1ng.remotedisplayswitch.core.DeviceProfile
import com.fy1ng.remotedisplayswitch.core.ExpiredRequestException
import com.fy1ng.remotedisplayswitch.core.HttpResult
import com.fy1ng.remotedisplayswitch.core.PairBundle
import com.fy1ng.remotedisplayswitch.core.Protocol
import com.fy1ng.remotedisplayswitch.core.Route
import com.fy1ng.remotedisplayswitch.core.Transport
import kotlinx.coroutines.suspendCancellableCoroutine
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.json.JSONObject
import java.io.IOException
import java.time.Duration
import java.time.Instant
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

class HttpsTransport : Transport {
    private val systemClient = OkHttpClient.Builder()
        .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false)
        .connectTimeout(5, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS).build()
    private data class TlsOrigin(val endpoint: String, val pin: String)
    private val directClients = LinkedHashMap<TlsOrigin, OkHttpClient>(16, 0.75f, true)

    private fun directClient(endpoint: String, pin: String): OkHttpClient = synchronized(directClients) {
        val url = endpoint.toHttpUrl()
        val key = TlsOrigin(url.toString(), pin)
        directClients[key]?.let { return@synchronized it }
        val trust = PinnedTrustManager(pin)
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf(trust), null)
        // Stable TLS objects let OkHttp reuse authenticated connections for this origin and pin.
        val client = systemClient.newBuilder().sslSocketFactory(context.socketFactory, trust)
            .hostnameVerifier(trust.hostnameVerifier(url.host)).build()
        directClients[key] = client
        if (directClients.size > MAX_DIRECT_CLIENTS) {
            val oldest = directClients.entries.iterator()
            oldest.next()
            oldest.remove()
        }
        client
    }

    override suspend fun pair(bundle: PairBundle, clientName: String, timeoutMillis: Long): HttpResult {
        val request = Request.Builder().url(bundle.endpoint + "/v1/pair")
            .post(Protocol.pairRequest(bundle.code, clientName).toRequestBody(JSON)).build()
        return post(directClient(bundle.endpoint, bundle.certificatePin), request, timeoutMillis, bundle.expiresAt)
    }

    internal fun controlRequest(device: DeviceProfile, route: Route, body: String): Request {
        val builder = Request.Builder().post(body.toRequestBody(JSON))
        when (route) {
            Route.Direct, Route.Tailscale -> {
                val endpoint = if (route == Route.Direct) device.endpoint else requireNotNull(device.tailscaleEndpoint)
                builder.url(endpoint + "/v1/control")
                    .header("Authorization", "Bearer " + device.deviceToken)
            }
            Route.Relay -> {
                val relay = requireNotNull(device.relay)
                builder.url(relay.endpoint + "/v1/devices/" + device.deviceId + "/control")
                    .header("Authorization", "Bearer " + relay.controllerToken)
                    .header("X-Device-Token", device.deviceToken)
            }
        }
        return builder.build()
    }

    internal fun controlClient(device: DeviceProfile, route: Route): OkHttpClient = when (route) {
        Route.Direct -> directClient(device.endpoint, device.certificatePin)
        Route.Tailscale -> directClient(requireNotNull(device.tailscaleEndpoint), device.certificatePin)
        Route.Relay -> systemClient
    }

    override suspend fun control(device: DeviceProfile, route: Route, body: String, timeoutMillis: Long): HttpResult {
        return post(controlClient(device, route), controlRequest(device, route, body), timeoutMillis,
            Instant.parse(JSONObject(body).getString("expiresAt")))
    }

    internal fun readResponse(response: Response): HttpResult = response.use {
        if (it.code !in 200..299) return@use HttpResult(it.code, "")
        val source = requireNotNull(it.body) { "Missing response" }.source()
        require(!source.request(Protocol.MAX_BODY.toLong() + 1)) { "Response too large" }
        HttpResult(it.code, source.readUtf8())
    }

    private suspend fun post(client: OkHttpClient, request: Request, timeoutMillis: Long, expiresAt: Instant): HttpResult =
        suspendCancellableCoroutine { continuation ->
            val remaining = Duration.between(Instant.now(), expiresAt).toMillis()
            if (remaining <= 0) {
                continuation.resumeWithException(ExpiredRequestException())
                return@suspendCancellableCoroutine
            }
            val call = client.newCall(request)
            call.timeout().timeout(minOf(timeoutMillis, remaining).coerceAtLeast(1), TimeUnit.MILLISECONDS)
            continuation.invokeOnCancellation { call.cancel() }
            call.enqueue(object : Callback {
                @Suppress("PARAMETER_NAME_CHANGED_ON_OVERRIDE")
                override fun onFailure(call: Call, exception: IOException) {
                    continuation.resumeWithException(exception)
                }

                override fun onResponse(call: Call, response: Response) {
                    try {
                        val result = readResponse(response)
                        continuation.resume(result)
                    } catch (exception: Exception) {
                        continuation.resumeWithException(exception)
                    }
                }
            })
        }

    private companion object {
        const val MAX_DIRECT_CLIENTS = 64
        val JSON = "application/json; charset=utf-8".toMediaType()
    }
}
