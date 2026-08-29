package io.listensphere.mobile.core.discovery

import android.annotation.SuppressLint
import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import io.listensphere.mobile.core.model.ControllerEndpoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.suspendCancellableCoroutine
import java.util.UUID
import kotlin.coroutines.resume

class AndroidControllerDiscovery(context: Context) : AutoCloseable {
    companion object {
        private const val SERVICE_TYPE = "_listensphere._tcp."
    }

    private val manager = context.getSystemService(NsdManager::class.java)
    private val wifiManager = context.applicationContext.getSystemService(WifiManager::class.java)
    private val multicastLock = wifiManager.createMulticastLock("ListenSphere-Discovery").apply {
        setReferenceCounted(false)
    }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val resolveMutex = Mutex()
    private val mutableControllers = MutableStateFlow<List<ControllerEndpoint>>(emptyList())
    private var listener: NsdManager.DiscoveryListener? = null
    private var generation = 0L
    private var restartAfterStop = false
    private var closed = false

    val controllers: StateFlow<List<ControllerEndpoint>> = mutableControllers.asStateFlow()

    private fun createListener(listenerGeneration: Long) = object : NsdManager.DiscoveryListener {
        override fun onDiscoveryStarted(serviceType: String) = Unit

        override fun onServiceFound(serviceInfo: NsdServiceInfo) {
            if (!serviceInfo.serviceType.startsWith("_listensphere._tcp")) return
            scope.launch {
                resolveMutex.withLock {
                    resolve(serviceInfo)?.let { endpoint ->
                        if (isCurrent(listenerGeneration)) addOrUpdate(endpoint)
                    }
                }
            }
        }

        override fun onServiceLost(serviceInfo: NsdServiceInfo) {
            mutableControllers.value = mutableControllers.value.filterNot {
                serviceInfo.serviceName.contains(
                    it.deviceId?.toString()?.replace("-", "") ?: "<manual>",
                    ignoreCase = true,
                )
            }
        }

        override fun onDiscoveryStopped(serviceType: String) = completeStop(this)
        override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) = completeStop(this)
        override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = completeStop(this)
    }

    @Synchronized
    fun start() {
        if (closed || listener != null) return
        beginDiscovery()
    }

    @Synchronized
    fun refresh() {
        mutableControllers.value = emptyList()
        if (closed) return
        val current = listener
        if (current == null) {
            beginDiscovery()
            return
        }

        restartAfterStop = true
        try {
            manager.stopServiceDiscovery(current)
        } catch (_: RuntimeException) {
            completeStop(current)
        }
    }

    @Synchronized
    fun stop() {
        restartAfterStop = false
        val current = listener ?: return
        try {
            manager.stopServiceDiscovery(current)
        } catch (_: RuntimeException) {
            completeStop(current)
        }
    }

    private fun beginDiscovery() {
        generation++
        val current = createListener(generation)
        listener = current
        try {
            if (!multicastLock.isHeld) multicastLock.acquire()
            manager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, current)
        } catch (_: RuntimeException) {
            listener = null
            if (multicastLock.isHeld) multicastLock.release()
        }
    }

    @Synchronized
    private fun completeStop(stopped: NsdManager.DiscoveryListener) {
        if (listener !== stopped) return
        listener = null
        if (multicastLock.isHeld) multicastLock.release()
        val shouldRestart = restartAfterStop && !closed
        restartAfterStop = false
        if (shouldRestart) beginDiscovery()
    }

    @Synchronized
    private fun isCurrent(listenerGeneration: Long): Boolean =
        !closed && listener != null && generation == listenerGeneration

    @Suppress("DEPRECATION")
    @SuppressLint("NewApi")
    private suspend fun resolve(service: NsdServiceInfo): ControllerEndpoint? =
        suspendCancellableCoroutine { continuation ->
            manager.resolveService(
                service,
                object : NsdManager.ResolveListener {
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                        if (continuation.isActive) continuation.resume(null)
                    }

                    override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                        val attributes = serviceInfo.attributes.mapValues {
                            it.value.toString(Charsets.UTF_8)
                        }
                        val version = attributes["pv"]?.split('.', limit = 2)
                        val endpoint = runCatching {
                            ControllerEndpoint(
                                deviceId = attributes["id"]?.let(UUID::fromString),
                                displayName = attributes["name"].orEmpty().ifBlank {
                                    "ListenSphere Controller"
                                },
                                address = serviceInfo.host,
                                port = serviceInfo.port,
                                protocolMajor = version?.getOrNull(0)?.toIntOrNull() ?: 1,
                                protocolMinor = version?.getOrNull(1)?.toIntOrNull() ?: 0,
                                capabilities = attributes["caps"]?.toULongOrNull(16) ?: 0u,
                            )
                        }.getOrNull()?.takeIf {
                            !it.address.isLoopbackAddress &&
                                (it.address.isSiteLocalAddress || it.address.isLinkLocalAddress)
                        }
                        if (continuation.isActive) continuation.resume(endpoint)
                    }
                },
            )
        }

    private fun addOrUpdate(endpoint: ControllerEndpoint) {
        if (endpoint.protocolMajor != 1) return
        val controllers = mutableControllers.value.toMutableList()
        val index = controllers.indexOfFirst { it.stableKey == endpoint.stableKey }
        if (index >= 0) controllers[index] = endpoint else controllers += endpoint
        mutableControllers.value = controllers.sortedBy { it.displayName.lowercase() }
    }

    override fun close() {
        synchronized(this) { closed = true }
        stop()
        scope.cancel()
    }
}
