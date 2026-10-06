package com.fy1ng.remotedisplayswitch.pairing

import android.content.Context
import android.net.Network
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.net.InetAddress

data class NearbyComputer(val name: String, val host: InetAddress, val port: Int, val sessionId: String, val network: Network?)

@Suppress("DEPRECATION")
class AndroidNsd(context: Context, private val changed: (List<NearbyComputer>) -> Unit, private val failed: () -> Unit) {
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val wifi = context.applicationContext.getSystemService(WifiManager::class.java)
    private val handler = Handler(Looper.getMainLooper())
    private var generation = 0L
    private var discovery: NsdManager.DiscoveryListener? = null
    private var registration: NsdManager.RegistrationListener? = null
    private var multicast: WifiManager.MulticastLock? = null
    private val computers = linkedMapOf<String, NearbyComputer>()
    private val present = mutableSetOf<String>()
    private val queue = ArrayDeque<Pair<Long, NsdServiceInfo>>()
    private var resolving = false
    private var fallbackNetwork: Network? = null
    fun start(sessionId: String, port: Int, network: Network?) {
        stop()
        val current = generation
        fallbackNetwork = network
        runCatching {
            multicast = wifi?.createMulticastLock("rds-pair").also { it?.setReferenceCounted(false); it?.acquire() }
        }
        val info = NsdServiceInfo().apply {
            serviceName = "RDS Android ${sessionId.take(8)}"
            serviceType = "_rds-pair._tcp."
            setPort(port)
            setAttribute("v", "1")
            setAttribute("role", "android")
            setAttribute("sid", sessionId)
            if (Build.VERSION.SDK_INT >= 33) setNetwork(network)
        }
        val registered = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(service: NsdServiceInfo) {
                handler.post { if (current != generation) runCatching { nsd.unregisterService(this) } }
            }
            override fun onRegistrationFailed(service: NsdServiceInfo, error: Int) { handler.post { if (current == generation) failed() } }
            override fun onServiceUnregistered(service: NsdServiceInfo) = Unit
            override fun onUnregistrationFailed(service: NsdServiceInfo, error: Int) = Unit
        }
        registration = registered
        runCatching { nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, registered) }.onFailure { failed() }
        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(type: String) { handler.post {
                if (current != generation) runCatching { nsd.stopServiceDiscovery(this) }
            } }
            override fun onDiscoveryStopped(type: String) = Unit
            override fun onStartDiscoveryFailed(type: String, error: Int) { handler.post { if (current == generation) failed() } }
            override fun onStopDiscoveryFailed(type: String, error: Int) = Unit
            override fun onServiceFound(service: NsdServiceInfo) { handler.post {
                if (current == generation && service.serviceType.trimEnd('.') == "_rds-pair._tcp" && present.size < 64) {
                    present.add(service.serviceName)
                    if (queue.none { it.second.serviceName == service.serviceName }) queue.addLast(current to service)
                    resolveNext()
                }
            } }
            override fun onServiceLost(service: NsdServiceInfo) { handler.post {
                if (current == generation) {
                    present.remove(service.serviceName)
                    computers.remove(service.serviceName)
                    changed(computers.values.toList())
                }
            } }
        }
        discovery = listener
        runCatching { nsd.discoverServices("_rds-pair._tcp.", NsdManager.PROTOCOL_DNS_SD, listener) }.onFailure { failed() }
    }
    private fun resolveNext() {
        if (resolving || queue.isEmpty()) return
        val (current, info) = queue.removeFirst()
        if (current != generation || info.serviceName !in present) { resolveNext(); return }
        resolving = true
        val listener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(service: NsdServiceInfo, error: Int) { handler.post {
                resolving = false
                if (current == generation) failed()
                resolveNext()
            } }
            override fun onServiceResolved(service: NsdServiceInfo) { handler.post {
                resolving = false
                if (current == generation && service.serviceName in present) {
                    val attributes = service.attributes.mapValues { it.value.toString(Charsets.UTF_8) }
                    val sid = attributes["sid"].orEmpty()
                    val host = service.host
                    if (attributes["v"] == "1" && attributes["role"] == "windows" && ShortCodeProtocol.validSession(sid) &&
                        service.port in 1..65535 && host != null && !host.isAnyLocalAddress && !host.isMulticastAddress) {
                        computers[service.serviceName] = NearbyComputer(service.serviceName.take(80), host, service.port, sid,
                            if (Build.VERSION.SDK_INT >= 33) service.network ?: fallbackNetwork else fallbackNetwork)
                        changed(computers.values.toList())
                    }
                }
                resolveNext()
            } }
        }
        runCatching { enqueueResolve(nsd, info, listener) { current == generation && info.serviceName in present } }.onFailure {
            resolving = false
            failed()
            resolveNext()
        }
    }
    fun stop() {
        generation++
        discovery?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        registration?.let { runCatching { nsd.unregisterService(it) } }
        discovery = null
        registration = null
        runCatching { multicast?.let { if (it.isHeld) it.release() } }
        multicast = null
        queue.clear()
        present.clear()
        computers.clear()
        changed(emptyList())
    }
    companion object {
        private val resolutions = ArrayDeque<() -> Unit>()
        private var resolutionActive = false
        private val callbacks = Handler(Looper.getMainLooper())
        private fun enqueueResolve(manager: NsdManager, info: NsdServiceInfo, listener: NsdManager.ResolveListener, valid: () -> Boolean) {
            resolutions.addLast {
                val forwarded = object : NsdManager.ResolveListener {
                    override fun onResolveFailed(service: NsdServiceInfo, error: Int) { callbacks.post {
                        listener.onResolveFailed(service, error)
                        resolutionActive = false
                        resolveQueued()
                    } }
                    override fun onServiceResolved(service: NsdServiceInfo) { callbacks.post {
                        listener.onServiceResolved(service)
                        resolutionActive = false
                        resolveQueued()
                    } }
                }
                runCatching {
                    if (valid()) manager.resolveService(info, forwarded)
                    else forwarded.onResolveFailed(info, NsdManager.FAILURE_INTERNAL_ERROR)
                }.onFailure {
                    forwarded.onResolveFailed(info, NsdManager.FAILURE_INTERNAL_ERROR)
                }
            }
            resolveQueued()
        }
        private fun resolveQueued() {
            if (resolutionActive || resolutions.isEmpty()) return
            resolutionActive = true
            resolutions.removeFirst().invoke()
        }
    }
}
