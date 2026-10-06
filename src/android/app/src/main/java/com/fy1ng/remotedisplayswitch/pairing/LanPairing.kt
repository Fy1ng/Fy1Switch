package com.fy1ng.remotedisplayswitch.pairing

import android.app.KeyguardManager
import android.content.Context
import android.content.BroadcastReceiver
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import com.fy1ng.remotedisplayswitch.core.DeviceRepository
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

data class LanPairingState(
    val code: String? = null, val seconds: Long = 0, val computers: List<NearbyComputer> = emptyList(),
    val busy: Boolean = false, val message: String? = null,
)

class LanPairing(private val context: Context, private val repository: DeviceRepository, private val paired: () -> Unit) {
    private val mutable = MutableStateFlow(LanPairingState())
    val state = mutable.asStateFlow()
    private var generation = 0L
    private var scope: CoroutineScope? = null
    private var server: ServerSocket? = null
    private var socket: Socket? = null
    private var session: CodeSession? = null
    private var screenReceiver: BroadcastReceiver? = null
    private val nsd = AndroidNsd(context, { mutable.value = mutable.value.copy(computers = it) }, {
        mutable.value = mutable.value.copy(message = "局域网发现或广播失败，请检查 Wi-Fi，或使用二维码。")
    })
    fun start() {
        if (scope != null || repository.state.value.busy || context.getSystemService(KeyguardManager::class.java).isKeyguardLocked) return
        stop()
        val current = generation
        val ownedScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        scope = ownedScope
        screenReceiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) { stop() }
        }.also { context.registerReceiver(it, IntentFilter(Intent.ACTION_SCREEN_OFF)) }
        val ownedSession = CodeSession()
        session = ownedSession
        mutable.value = LanPairingState(ownedSession.code, 300)
        ownedScope.launch {
            try {
                val connectivity = context.getSystemService(ConnectivityManager::class.java)
                val network = connectivity.allNetworks.firstOrNull {
                    connectivity.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
                } ?: throw IllegalStateException("wifi_required")
                val address = connectivity.getLinkProperties(network)?.linkAddresses?.map { it.address }
                    ?.firstOrNull { it is java.net.Inet4Address && !it.isLoopbackAddress && !it.isLinkLocalAddress }
                    ?: throw IllegalStateException("wifi_address_required")
                val listener = ServerSocket(0, 1, address).also { server = it }
                nsd.start(ownedSession.id, listener.localPort, network)
                launch {
                    while (isActive) {
                        delay(1000)
                        if (context.getSystemService(KeyguardManager::class.java).isKeyguardLocked) { stop(); break }
                        val seconds = ownedSession.remainingSeconds()
                        mutable.value = mutable.value.copy(seconds = seconds, code = if (seconds > 0) ownedSession.code else null)
                        if (seconds == 0L && !mutable.value.busy) {
                            stop("短码已到期或尝试次数已用完，请手动刷新。")
                            break
                        }
                    }
                }
                while (isActive) {
                    var accepted: Socket? = null
                    val incoming = try {
                        withContext(Dispatchers.IO) { listener.accept().also { accepted = it } }
                    } catch (failure: Exception) {
                        runCatching { accepted?.close() }
                        throw failure
                    }
                    if (current != generation || repository.state.value.busy || !ownedSession.admit(PairingLimits.process)) {
                        incoming.close()
                    } else {
                        socket = incoming
                        mutable.value = mutable.value.copy(busy = true)
                        ownedScope.launch { exchange(incoming, current, ownedSession, null, "") }
                    }
                }
            } catch (_: CancellationException) {
                throw CancellationException()
            } catch (_: Exception) {
                if (current == generation && !mutable.value.busy) stop("局域网监听不可用，请刷新或使用二维码。")
            }
        }
    }
    fun connect(computer: NearbyComputer, code: String) {
        val ownedSession = session ?: return
        val ownedScope = scope ?: return
        if (mutable.value.busy || repository.state.value.busy || !ShortCodeProtocol.validCode(code)) return
        if (!ownedSession.admit(PairingLimits.process)) {
            mutable.value = mutable.value.copy(message = "尝试次数过多或短码已到期，请稍后重试。")
            return
        }
        val outgoing = Socket()
        socket = outgoing
        mutable.value = mutable.value.copy(busy = true, message = "正在验证短码与电脑证书…")
        val current = generation
        ownedScope.launch { exchange(outgoing, current, ownedSession, computer, code) }
    }
    private suspend fun exchange(connection: Socket, current: Long, ownedSession: CodeSession, computer: NearbyComputer?, code: String) {
        var authenticated = false
        val ownedListener = server
        val began = System.nanoTime()
        var deadline = deadlines.schedule({ runCatching { connection.close() } }, 15, TimeUnit.SECONDS)
        try {
            withTimeout(45_000) {
                withContext(Dispatchers.IO) {
                    connection.soTimeout = 15_000
                    if (computer != null) {
                        computer.network?.bindSocket(connection)
                        connection.connect(InetSocketAddress(computer.host, computer.port), 15_000)
                    }
                    val wire = SocketPairWire(connection)
                    val onProof: () -> Unit = {
                        ownedSession.consume()
                        authenticated = true
                        runCatching { ownedListener?.close() }
                    }
                    val channel = if (computer == null) ShortCodeProtocol.server(wire, ownedSession.id, ownedSession.code, onProof = onProof)
                        else ShortCodeProtocol.client(wire, computer.sessionId, code, onProof = onProof)
                    deadline.cancel(false)
                    val remaining = 45_000 - TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - began)
                    require(remaining > 0)
                    deadline = deadlines.schedule({ runCatching { connection.close() } }, remaining, TimeUnit.MILLISECONDS)
                    connection.soTimeout = remaining.toInt()
                    withContext(Dispatchers.Main.immediate) {
                        ensureActive()
                        require(current == generation)
                        nsd.stop()
                        mutable.value = mutable.value.copy(code = null, seconds = 0)
                    }
                    val bundle = channel.decrypt("windows-bundle", wire.read())
                    val success = withContext(Dispatchers.Main.immediate) {
                        ensureActive()
                        require(current == generation && !context.getSystemService(KeyguardManager::class.java).isKeyguardLocked)
                        ShortCodePairing.acceptBundle(repository, bundle, "Android 手机")
                    }
                    ensureActive()
                    wire.write(channel.encrypt("android-result", JSONObject().put("success", success).toString()))
                    withContext(Dispatchers.Main.immediate) {
                        if (current == generation) {
                            if (success) paired()
                            stop(if (success) "配对成功。" else "证书或 HTTPS 配对失败，或电脑已经配对；请在电脑上重新开始。")
                        }
                    }
                }
            }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (_: Exception) {
            if (current == generation) {
                if (authenticated) stop("短码已消费；配对未确认，请在两端重新开始。")
                else mutable.value = mutable.value.copy(message = "短码验证失败或连接超时，请核对电脑与短码。")
            }
        } finally {
            deadline.cancel(false)
            runCatching { connection.close() }
            PairingLimits.process.release()
            if (current == generation) {
                socket = null
                mutable.value = mutable.value.copy(busy = false)
            }
        }
    }
    fun stop(message: String? = null) {
        generation++
        screenReceiver?.let { runCatching { context.unregisterReceiver(it) } }
        screenReceiver = null
        runCatching { server?.close() }
        runCatching { socket?.close() }
        server = null
        socket = null
        session = null
        scope?.cancel()
        scope = null
        nsd.stop()
        mutable.value = LanPairingState(message = message)
    }
    companion object {
        private val deadlines = Executors.newSingleThreadScheduledExecutor { runnable -> Thread(runnable, "rds-pair-deadline").apply { isDaemon = true } }
    }
}
