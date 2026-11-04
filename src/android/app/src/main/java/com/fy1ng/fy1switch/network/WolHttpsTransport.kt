package com.fy1ng.fy1switch.network

import com.fy1ng.fy1switch.core.HttpResult
import com.fy1ng.fy1switch.core.WolProtocol
import com.fy1ng.fy1switch.core.WolTransport
import kotlinx.coroutines.suspendCancellableCoroutine
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

class WolHttpsTransport : WolTransport {
    private val client = OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .retryOnConnectionFailure(false).connectTimeout(5, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS).build()
    internal fun request(endpoint: String, path: String, method: String, token: String?, body: String?): Request {
        require(path.startsWith("/v1/wol/") && !path.contains('?') && !path.contains('#'))
        val builder = Request.Builder().url(WolProtocol.endpoint(endpoint) + path)
            .header("Accept", "application/json")
        token?.let { builder.header("Authorization", "Bearer " + WolProtocol.token(it)) }
        return builder.method(method, body?.toRequestBody("application/json; charset=utf-8".toMediaType())).build()
    }
    override suspend fun send(endpoint: String, path: String, method: String, token: String?, body: String?, timeoutMillis: Long): HttpResult =
        suspendCancellableCoroutine { continuation ->
            val call = client.newCall(request(endpoint, path, method, token, body))
            call.timeout().timeout(timeoutMillis.coerceIn(1, 20_000), TimeUnit.MILLISECONDS)
            continuation.invokeOnCancellation { call.cancel() }
            call.enqueue(object : Callback {
                override fun onFailure(call: Call, e: IOException) { if (continuation.isActive) continuation.resumeWithException(e) }
                override fun onResponse(call: Call, response: Response) {
                    try {
                        val result = response.use {
                            val source = it.body?.source()
                            require(source == null || !source.request(WolProtocol.MAX_BODY.toLong() + 1))
                            HttpResult(it.code, source?.readUtf8().orEmpty())
                        }
                        if (continuation.isActive) continuation.resume(result)
                    } catch (error: Exception) { if (continuation.isActive) continuation.resumeWithException(error) }
                }
            })
        }
}
