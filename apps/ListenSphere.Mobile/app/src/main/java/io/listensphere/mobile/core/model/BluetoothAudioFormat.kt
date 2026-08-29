package io.listensphere.mobile.core.model

import android.media.MediaCodecList
import android.os.Build

enum class BluetoothStreamCodec(
    val wireId: Int,
    val capabilityFlag: Int,
    val displayName: String,
    val description: String,
    val implementedByListenSphere: Boolean,
) {
    PCM16(3, 1 shl 0, "PCM16", "无损、带宽占用较高", true),
    IMA_ADPCM(4, 1 shl 1, "IMA ADPCM", "低延迟、带宽占用较低", true),
    AAC(5, 1 shl 2, "AAC-LC", "高兼容性有损编码 · 160 kbps", true),
    SBC(6, 1 shl 3, "SBC", "蓝牙 A2DP 基础编码", false),
    LDAC(7, 1 shl 4, "LDAC", "高码率蓝牙编码", false),
}

enum class BluetoothChannelMode(val channels: Int, val capabilityFlag: Int, val displayName: String) {
    MONO(1, 1 shl 0, "单声道"),
    STEREO(2, 1 shl 1, "双声道"),
}

data class LocalCodecCapability(
    val codec: BluetoothStreamCodec,
    val encoderDetected: Boolean,
    val hardwareAccelerated: Boolean,
    val evidence: String,
)

data class BluetoothControllerCapabilities(
    val codecMask: Int,
    val channelMask: Int,
) {
    fun supports(codec: BluetoothStreamCodec): Boolean = codecMask and codec.capabilityFlag != 0
    fun supports(mode: BluetoothChannelMode): Boolean = channelMask and mode.capabilityFlag != 0
}

data class BluetoothCodecChoice(
    val codec: BluetoothStreamCodec,
    val enabled: Boolean,
    val status: String,
)

object AndroidCodecCapabilityDetector {
    fun detect(): List<LocalCodecCapability> {
        val encoders = runCatching {
            MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos.filter { it.isEncoder }
        }.getOrDefault(emptyList())

        return BluetoothStreamCodec.entries.map { codec ->
            if (codec == BluetoothStreamCodec.PCM16 || codec == BluetoothStreamCodec.IMA_ADPCM) {
                LocalCodecCapability(codec, true, false, "聆界内置软件编码器")
            } else {
                val aliases = when (codec) {
                    BluetoothStreamCodec.AAC -> listOf("audio/mp4a-latm")
                    BluetoothStreamCodec.SBC -> listOf("audio/sbc")
                    BluetoothStreamCodec.LDAC -> listOf("audio/ldac", "audio/x-ldac")
                    else -> emptyList()
                }
                val matches = encoders.filter { info ->
                    info.supportedTypes.any { type ->
                        aliases.any(type::equals) ||
                            (codec == BluetoothStreamCodec.LDAC && type.contains("ldac", true))
                    }
                }
                val hardware = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q &&
                    matches.any { it.isHardwareAccelerated }
                val evidence = when {
                    matches.isEmpty() -> "未发现应用可调用的编码器"
                    hardware -> "检测到硬件编码器"
                    else -> "仅检测到软件编码器"
                }
                LocalCodecCapability(codec, matches.isNotEmpty(), hardware, evidence)
            }
        }
    }
}
