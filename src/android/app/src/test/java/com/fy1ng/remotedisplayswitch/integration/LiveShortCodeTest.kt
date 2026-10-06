package com.fy1ng.remotedisplayswitch.integration

import com.fy1ng.remotedisplayswitch.core.*
import com.fy1ng.remotedisplayswitch.network.HttpsTransport
import com.fy1ng.remotedisplayswitch.pairing.*
import kotlinx.coroutines.runBlocking
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.json.JSONObject
import org.junit.Test
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URI
import java.nio.file.Files
import java.nio.file.Path
import java.time.Instant
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class LiveShortCodeTest {
    @Test(timeout = 70_000) fun explicitLoopbackFixture() = runBlocking {
        var stage = "fixture"
        val timer = Executors.newSingleThreadScheduledExecutor()
        val worker = Executors.newSingleThreadExecutor()
        try {
            val path = Path.of(requireNotNull(System.getenv("RDS_SHORT_CODE_FIXTURE")))
            require(Files.size(path) in 1..16384)
            val fixture = JSONObject(Files.newBufferedReader(path, Charsets.UTF_8).use { it.readText() }.removePrefix("\uFEFF"))
            val expectedId = UUID.fromString(fixture.getString("expectedDeviceId"))
            val target = UUID.fromString(fixture.getString("targetId"))
            val repository = DeviceRepository(object : StateStore {
                private var state = AppState()
                override fun read() = state
                override fun write(state: AppState) { this.state = state }
            }, HttpsTransport())
            repository.initialize()
            suspend fun finish(connection: Socket, serverSession: String, code: String, reverse: Boolean) {
                connection.soTimeout = 15_000
                val limit = timer.schedule({ connection.close() }, 45, TimeUnit.SECONDS)
                try {
                    val wire = SocketPairWire(connection)
                    stage = "SRP authentication"
                    val channel = if (reverse) ShortCodeProtocol.server(wire, serverSession, code)
                        else ShortCodeProtocol.client(wire, serverSession, code)
                    stage = "authenticated bundle"
                    val text = channel.decrypt("windows-bundle", wire.read())
                    val bundle = Protocol.pairBundle(text, Instant.now())
                    require(bundle.deviceId == expectedId)
                    require(bundle.endpoint.toHttpUrl().host in setOf("127.0.0.1", "::1", "localhost"))
                    stage = "strict-pin HTTPS pair"
                    val success = ShortCodePairing.acceptBundle(repository, text, "Android short-code JVM fixture")
                    wire.write(channel.encrypt("android-result", JSONObject().put("success", success).toString()))
                    require(success)
                    val device = repository.state.value.devices.single()
                    require(device.profile.deviceId == expectedId && device.status?.targetId == target)
                } finally { limit.cancel(false) }
            }
            val reverseUrl = fixture.optString("reverseControlUrl")
            if (reverseUrl.isEmpty()) {
                val host = fixture.getString("host")
                require(host in setOf("127.0.0.1", "::1", "localhost"))
                val port = fixture.getInt("port")
                require(port in 1..65535)
                Socket().use { connection ->
                    connection.connect(InetSocketAddress(host, port), 15_000)
                    finish(connection, fixture.getString("sessionId"), fixture.getString("code"), false)
                }
            } else {
                val uri = URI(reverseUrl)
                require(uri.scheme == "http" && uri.host in setOf("127.0.0.1", "localhost", "[::1]", "::1") && uri.userInfo == null)
                ServerSocket(0, 1, InetAddress.getByName("127.0.0.1")).use { listener ->
                    listener.soTimeout = 15_000
                    val session = ShortCodeProtocol.session()
                    val code = ShortCodeProtocol.newCode()
                    val request = JSONObject().put("host", "127.0.0.1").put("port", listener.localPort)
                        .put("sessionId", session).put("code", code).toString()
                    val trigger = worker.submit<Boolean> {
                        val control = uri.toURL().openConnection() as HttpURLConnection
                        try {
                            control.instanceFollowRedirects = false
                            control.connectTimeout = 5000
                            control.readTimeout = 50_000
                            control.requestMethod = "POST"
                            control.doOutput = true
                            control.setRequestProperty("Content-Type", "application/json")
                            control.outputStream.use { it.write(request.toByteArray(Charsets.UTF_8)) }
                            control.responseCode in 200..299
                        } finally { control.disconnect() }
                    }
                    listener.accept().use { finish(it, session, code, true) }
                    require(trigger.get(5, TimeUnit.SECONDS))
                }
            }
        } catch (_: Exception) {
            throw AssertionError("Short-code fixture failed at $stage; sensitive details suppressed.")
        } finally {
            timer.shutdownNow()
            worker.shutdownNow()
        }
    }
}
