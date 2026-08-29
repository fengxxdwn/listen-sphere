package io.listensphere.mobile.ui

import android.Manifest
import android.app.Application
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import androidx.core.content.ContextCompat
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import io.listensphere.mobile.core.discovery.AndroidControllerDiscovery
import io.listensphere.mobile.core.discovery.AndroidBluetoothDiscovery
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.AndroidCodecCapabilityDetector
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothCodecChoice
import io.listensphere.mobile.core.model.BluetoothControllerCapabilities
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.ControllerEndpoint
import io.listensphere.mobile.core.model.MobilePreferenceSnapshot
import io.listensphere.mobile.core.model.MobilePreferences
import io.listensphere.mobile.core.model.RecentConnection
import io.listensphere.mobile.core.model.RuntimeReadinessState
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.core.model.describeConnectionError
import io.listensphere.mobile.core.network.BluetoothRfcommProbeClient
import io.listensphere.mobile.core.usb.AndroidUsbAccessoryManager
import io.listensphere.mobile.core.usb.UsbAccessoryState
import io.listensphere.mobile.service.AudioStreamingService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.InetAddress
import java.util.UUID

class MobileViewModel(application: Application) : AndroidViewModel(application) {
    private val preferences = MobilePreferences(application)
    private val savedPreferences = preferences.load()
    private val discovery = AndroidControllerDiscovery(application)
    private val bluetoothDiscovery = AndroidBluetoothDiscovery(application)
    private val bluetoothProbe = BluetoothRfcommProbeClient(application)
    private val usbAccessoryManager = AndroidUsbAccessoryManager(application)
    val controllers: StateFlow<List<ControllerEndpoint>> = discovery.controllers
    private val mutableBluetoothControllerIds =
        MutableStateFlow(savedPreferences.bluetoothControllerIds)
    val bluetoothPeers: StateFlow<List<BluetoothPeer>> = combine(
        bluetoothDiscovery.peers,
        mutableBluetoothControllerIds,
    ) { peers, associations ->
        peers.map { peer -> peer.copy(controllerId = associations[peer.stableKey]) }
    }.stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())
    val bluetoothDiscoveryStatus: StateFlow<String> = bluetoothDiscovery.status

    private val mutableSelectedKey =
        MutableStateFlow(savedPreferences.selectedControllerKey)
    val selectedKey = mutableSelectedKey.asStateFlow()
    private val mutableSelectedBluetoothKey =
        MutableStateFlow(savedPreferences.selectedBluetoothKey)
    val selectedBluetoothKey = mutableSelectedBluetoothKey.asStateFlow()
    val bluetoothProbeStatus = MutableStateFlow("尚未测试蓝牙通道。")
    val bluetoothProbeRunning = MutableStateFlow(false)
    private val localCodecCapabilities = AndroidCodecCapabilityDetector.detect()
    private val mutableControllerCapabilities = MutableStateFlow<BluetoothControllerCapabilities?>(null)
    val bluetoothCodecChoices = MutableStateFlow(buildCodecChoices(null))
    val bluetoothChannelModes = MutableStateFlow<List<BluetoothChannelMode>>(emptyList())
    val selectedBluetoothCodec = MutableStateFlow(savedPreferences.bluetoothCodec)
    val selectedBluetoothChannelMode =
        MutableStateFlow(savedPreferences.bluetoothChannelMode)

    val pairingCode = MutableStateFlow("")
    val manualMode = MutableStateFlow(savedPreferences.manualMode)
    val manualHost = MutableStateFlow(savedPreferences.manualHost)
    val manualPort = MutableStateFlow(savedPreferences.manualPort)
    val usbCompatibilityMode = MutableStateFlow(savedPreferences.usbCompatibilityMode)
    val usbAccessoryState = MutableStateFlow(usbAccessoryManager.state())
    val captureKind = MutableStateFlow(savedPreferences.captureKind)
    val transportMode = MutableStateFlow(savedPreferences.transportMode)
    val recentConnections = MutableStateFlow(preferences.loadRecentConnections())
    val runtimeReadiness = MutableStateFlow(readRuntimeReadiness())
    private var lastRecordedConnection: Pair<TransportMode, String>? = null

    init {
        discovery.start()
        bluetoothDiscovery.refresh()
        viewModelScope.launch {
            controllers.collect { values ->
                if (mutableSelectedKey.value == null && values.isNotEmpty()) {
                    mutableSelectedKey.value = values.first().stableKey
                }
            }
        }
        viewModelScope.launch {
            bluetoothPeers.collect { values ->
                if (mutableSelectedBluetoothKey.value == null && values.isNotEmpty()) {
                    mutableSelectedBluetoothKey.value = values.first().stableKey
                }
            }
        }
        listOf(
            mutableSelectedKey,
            mutableSelectedBluetoothKey,
            selectedBluetoothCodec,
            selectedBluetoothChannelMode,
            manualMode,
            manualHost,
            manualPort,
            usbCompatibilityMode,
            captureKind,
            transportMode,
            mutableBluetoothControllerIds,
        ).forEach { flow ->
            viewModelScope.launch {
                flow.drop(1).collect { persistPreferences() }
            }
        }
        viewModelScope.launch {
            AudioStreamingService.status.collect { status ->
                val mode = status.transportMode
                val target = status.targetName?.takeIf(String::isNotBlank)
                if (status.phase == StreamPhase.STREAMING && mode != null && target != null &&
                    lastRecordedConnection != (mode to target)
                ) {
                    preferences.recordSuccessfulConnection(mode, target)
                    recentConnections.value = preferences.loadRecentConnections()
                    lastRecordedConnection = mode to target
                } else if (status.phase != StreamPhase.STREAMING) {
                    lastRecordedConnection = null
                }
            }
        }
    }

    fun refreshRuntimeState() {
        discovery.refresh()
        bluetoothDiscovery.refresh()
        refreshUsbAccessoryState()
        runtimeReadiness.value = readRuntimeReadiness()
    }

    private fun readRuntimeReadiness(): RuntimeReadinessState {
        val application = getApplication<Application>()
        fun granted(permission: String): Boolean = ContextCompat.checkSelfPermission(
            application,
            permission,
        ) == PackageManager.PERMISSION_GRANTED
        val bluetoothGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            granted(Manifest.permission.BLUETOOTH_CONNECT)
        val notificationGranted = Build.VERSION.SDK_INT < 33 ||
            granted(Manifest.permission.POST_NOTIFICATIONS)
        val powerManager = application.getSystemService(PowerManager::class.java)
        return RuntimeReadinessState(
            microphonePermissionGranted = granted(Manifest.permission.RECORD_AUDIO),
            bluetoothPermissionGranted = bluetoothGranted,
            notificationPermissionGranted = notificationGranted,
            batteryOptimizationDisabled = powerManager?.isIgnoringBatteryOptimizations(
                application.packageName,
            ) == true,
        )
    }

    private fun persistPreferences() {
        preferences.save(
            MobilePreferenceSnapshot(
                transportMode = transportMode.value,
                captureKind = captureKind.value,
                selectedControllerKey = selectedKey.value,
                selectedBluetoothKey = selectedBluetoothKey.value,
                manualMode = manualMode.value,
                manualHost = manualHost.value,
                manualPort = manualPort.value,
                usbCompatibilityMode = usbCompatibilityMode.value,
                bluetoothCodec = selectedBluetoothCodec.value,
                bluetoothChannelMode = selectedBluetoothChannelMode.value,
                bluetoothControllerIds = mutableBluetoothControllerIds.value,
            ),
        )
    }

    fun selectController(endpoint: ControllerEndpoint) {
        mutableSelectedKey.value = endpoint.stableKey
        val controllerId = endpoint.deviceId ?: return
        bluetoothPeers.value.firstOrNull { it.controllerId == controllerId }?.let {
            mutableSelectedBluetoothKey.value = it.stableKey
        }
    }

    fun refreshDiscovery() {
        discovery.refresh()
    }

    fun selectTransportMode(mode: TransportMode) {
        transportMode.value = mode
        if (mode == TransportMode.BLUETOOTH) {
            refreshBluetoothPeers()
        } else if (mode == TransportMode.USB) {
            refreshUsbAccessoryState()
        }
    }

    fun refreshUsbAccessoryState() {
        usbAccessoryState.value = usbAccessoryManager.state()
    }

    fun updateUsbAccessoryState(state: UsbAccessoryState) {
        usbAccessoryState.value = state
    }

    fun selectBluetoothPeer(peer: BluetoothPeer) {
        mutableSelectedBluetoothKey.value = peer.stableKey
        peer.controllerId?.let { controllerId ->
            controllers.value.firstOrNull { it.deviceId == controllerId }?.let {
                mutableSelectedKey.value = it.stableKey
            }
        }
        mutableControllerCapabilities.value = null
        bluetoothCodecChoices.value = buildCodecChoices(null)
        bluetoothChannelModes.value = emptyList()
        bluetoothProbeStatus.value = "请测试连接以读取双方编解码能力。"
    }

    fun selectBluetoothCodec(codec: BluetoothStreamCodec) {
        if (bluetoothCodecChoices.value.any { it.codec == codec && it.enabled }) {
            selectedBluetoothCodec.value = codec
        }
    }

    fun selectBluetoothChannelMode(mode: BluetoothChannelMode) {
        if (bluetoothChannelModes.value.contains(mode)) selectedBluetoothChannelMode.value = mode
    }

    fun refreshBluetoothPeers() {
        bluetoothDiscovery.refresh()
    }

    fun selectedBluetoothPeer(): BluetoothPeer? = bluetoothPeers.value.firstOrNull {
        it.stableKey == selectedBluetoothKey.value
    }

    suspend fun probeSelectedBluetooth() {
        val peer = bluetoothPeers.value.firstOrNull {
            it.stableKey == selectedBluetoothKey.value
        } ?: run {
            bluetoothProbeStatus.value = "请先选择一台已配对电脑。"
            return
        }
        bluetoothProbeRunning.value = true
        bluetoothProbeStatus.value = "正在连接 ${peer.displayName} 的 ListenSphere 服务…"
        try {
            val result = bluetoothProbe.probe(peer)
            result.controllerId?.let { controllerId ->
                mutableBluetoothControllerIds.value =
                    mutableBluetoothControllerIds.value + (peer.stableKey to controllerId)
                controllers.value.firstOrNull { it.deviceId == controllerId }?.let {
                    mutableSelectedKey.value = it.stableKey
                }
            }
            mutableControllerCapabilities.value = result.capabilities
            bluetoothCodecChoices.value = buildCodecChoices(result.capabilities)
            bluetoothChannelModes.value = BluetoothChannelMode.entries.filter(result.capabilities::supports)
            if (!bluetoothCodecChoices.value.any {
                    it.codec == selectedBluetoothCodec.value && it.enabled
                }) {
                selectedBluetoothCodec.value = bluetoothCodecChoices.value
                    .firstOrNull { it.enabled }?.codec ?: BluetoothStreamCodec.IMA_ADPCM
            }
            if (!bluetoothChannelModes.value.contains(selectedBluetoothChannelMode.value)) {
                selectedBluetoothChannelMode.value = bluetoothChannelModes.value.firstOrNull()
                    ?: BluetoothChannelMode.MONO
            }
            bluetoothProbeStatus.value =
                "能力检测完成：${result.controllerName} · 可选择 ${bluetoothCodecChoices.value.count { it.enabled }} 种编码"
        } catch (error: Exception) {
            val presentation = describeConnectionError(TransportMode.BLUETOOTH, error)
            bluetoothProbeStatus.value = "${presentation.title}：${presentation.detail}"
        } finally {
            bluetoothProbeRunning.value = false
        }
    }

    private fun buildCodecChoices(
        controller: BluetoothControllerCapabilities?,
    ): List<BluetoothCodecChoice> = localCodecCapabilities.map { local ->
        val enabled = local.encoderDetected && local.codec.implementedByListenSphere &&
            controller?.supports(local.codec) == true
        val status = when {
            controller == null -> "${local.evidence}；等待读取主控能力"
            !local.encoderDetected -> local.evidence
            !local.codec.implementedByListenSphere ->
                "${local.evidence}；ListenSphere RFCOMM 链路尚未实现此格式"
            !controller.supports(local.codec) -> "主控端不支持解码"
            local.hardwareAccelerated -> "双方支持 · 硬件编码"
            else -> "双方支持 · 软件编码"
        }
        BluetoothCodecChoice(local.codec, enabled, status)
    }

    fun updatePairingCode(value: String) {
        pairingCode.value = value.filter(Char::isDigit).take(6)
    }

    fun clearPairingCode() {
        pairingCode.value = ""
    }

    suspend fun resolveEndpoint(): ControllerEndpoint? = withContext(Dispatchers.IO) {
        val mode = transportMode.value
        if (mode != TransportMode.WIFI && mode != TransportMode.USB) return@withContext null
        if (mode == TransportMode.USB && !usbCompatibilityMode.value) return@withContext null
        if ((mode == TransportMode.USB && usbCompatibilityMode.value) || manualMode.value) {
            val host = manualHost.value.trim()
            val port = manualPort.value.toIntOrNull()
            if (host.isBlank() || port !in 1..65_535) return@withContext null
            ControllerEndpoint(
                deviceId = null,
                displayName = if (mode == TransportMode.USB) {
                    "USB 有线主控端"
                } else {
                    "手动连接的主控端"
                },
                address = InetAddress.getByName(host),
                port = requireNotNull(port),
            )
        } else {
            controllers.value.firstOrNull { it.stableKey == selectedKey.value }
        }
    }

    override fun onCleared() {
        discovery.close()
        super.onCleared()
    }
}
