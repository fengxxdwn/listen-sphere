package io.listensphere.mobile.core.model

import java.net.InetAddress
import java.util.UUID

data class ControllerEndpoint(
    val deviceId: UUID?,
    val displayName: String,
    val address: InetAddress,
    val port: Int,
    val protocolMajor: Int = 1,
    val protocolMinor: Int = 0,
    val capabilities: ULong = 0u,
) {
    val stableKey: String = deviceId?.toString() ?: "${address.hostAddress}:$port"
    val addressLabel: String = "${address.hostAddress}:$port"
}

data class BluetoothPeer(
    val address: String,
    val displayName: String,
    val controllerId: UUID? = null,
) {
    val stableKey: String = address.uppercase()
}

enum class CaptureKind {
    MICROPHONE,
    DEVICE_PLAYBACK,
}

enum class TransportMode(
    val displayName: String,
    val description: String,
    val available: Boolean,
) {
    WIFI("无线网络", "局域网低延迟传输", true),
    BLUETOOTH("蓝牙", "RFCOMM 低带宽音频", true),
    USB("有线", "原生 USB · 不影响网络", true),
}

enum class StreamPhase {
    IDLE,
    DISCOVERING,
    CONNECTING,
    PAIRING_REQUIRED,
    STREAMING,
    ERROR,
}

data class AudioSession(
    val sessionId: UUID,
    val streamId: Long,
    val key: ByteArray,
    val salt: ByteArray,
    val controllerAddress: InetAddress,
    val udpPort: Int,
    val sampleRate: Int,
    val channelCount: Int,
    val frameSamples: Int,
)

data class StreamStatus(
    val phase: StreamPhase = StreamPhase.IDLE,
    val title: String = "尚未发送",
    val detail: String = "选择主控端和声音来源后开始发送。",
    val peak: Float = 0f,
    val framesSent: Long = 0,
    val datagramsSent: Long = 0,
    val bytesSent: Long = 0,
    val roundTripMilliseconds: Double? = null,
    val transportMode: TransportMode? = null,
    val targetName: String? = null,
)
