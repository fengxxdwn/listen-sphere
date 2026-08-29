package io.listensphere.mobile.core.network

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.content.Context
import android.os.Build
import com.google.protobuf.ByteString
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.protocol.ControlFrameCodec
import io.listensphere.mobile.core.protocol.GuidWire
import io.listensphere.mobile.core.security.LocalIdentity
import io.listensphere.mobile.core.security.TrustedControllerStore
import io.listensphere.protocol.v1.Envelope
import io.listensphere.protocol.v1.HelloRequest
import io.listensphere.protocol.v1.PairRequest
import io.listensphere.protocol.v1.ProtocolVersion
import io.listensphere.protocol.v1.StartStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.BufferedOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.UUID

data class BluetoothConnectResult(
    val controllerId: UUID,
    val controllerName: String,
)

class BluetoothPairingRequiredException : IOException("主控端需要六位配对验证码。")
class BluetoothPairingRejectedException(message: String) : IOException(message)

class BluetoothAudioSession private constructor(
    private val socket: android.bluetooth.BluetoothSocket,
    private val input: DataInputStream,
    private val output: DataOutputStream,
    val controllerId: UUID,
    val controllerName: String,
    val codec: BluetoothStreamCodec,
    val channelMode: BluetoothChannelMode,
) : AutoCloseable {
    private var bufferedFrames = 0
    companion object {
        private val AUDIO_MAGIC = byteArrayOf('L'.code.toByte(), 'S'.code.toByte(), 'A'.code.toByte(), 'F'.code.toByte())
        const val FRAME_SAMPLES = 480
        const val ADPCM_BYTES_PER_FRAME = 244
        private const val MAX_ENCODED_FRAME_BYTES = 8192

        @SuppressLint("MissingPermission")
        suspend fun connect(
            context: Context,
            peer: BluetoothPeer,
            identity: LocalIdentity,
            trustStore: TrustedControllerStore,
            pairingCode: String,
            selectedCodec: BluetoothStreamCodec,
            selectedChannelMode: BluetoothChannelMode,
            sourceKind: String = "device_playback",
            sourceName: String = "手机播放声音",
        ): BluetoothAudioSession = withContext(Dispatchers.IO) {
            val adapter = context.getSystemService(BluetoothManager::class.java)?.adapter
                ?: error("此手机不支持蓝牙。")
            val socket = adapter.getRemoteDevice(peer.address)
                .createRfcommSocketToServiceRecord(BluetoothRfcommProbeClient.SERVICE_ID)
            var stage = "建立 RFCOMM 连接"
            try {
                socket.connect()
                stage = "读取主控端能力与身份"
                val output = DataOutputStream(BufferedOutputStream(socket.outputStream, 16 * 1024))
                val input = DataInputStream(socket.inputStream)
                BluetoothHandshakeCodec.writeRequest(
                    output,
                    BluetoothHandshakeCodec.MODE_STREAM_WITH_FORMAT_REQUEST,
                )
                output.flush()

                val responseMagic = ByteArray(4).also(input::readFully)
                if (!responseMagic.contentEquals(BluetoothHandshakeCodec.magic)) {
                    throw IOException("主控端返回了无效的蓝牙握手响应。")
                }
                val major = input.readUnsignedByte()
                val responseFlags = input.readUnsignedByte()
                if (major != 1) throw IOException("主控端蓝牙协议版本不兼容：$major。")
                val nameLength = input.readUnsignedShort()
                require(nameLength in 1..512) { "主控端名称长度无效。" }
                ByteArray(nameLength).also(input::readFully)
                val certificateLength = input.readUnsignedShort()
                require(certificateLength in 1..8192) { "主控端身份凭据长度无效。" }
                val certificateBytes = ByteArray(certificateLength).also(input::readFully)
                if (responseFlags and 0x02 != 0) {
                    ByteArray(16).also(input::readFully)
                }
                stage = "验证主控端证书"
                val certificate = CertificateFactory.getInstance("X.509")
                    .generateCertificate(certificateBytes.inputStream()) as X509Certificate
                certificate.checkValidity()
                val controllerFingerprint = MessageDigest.getInstance("SHA-256")
                    .digest(certificate.encoded)

                val displayName = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
                val proof = identity.createProof(controllerFingerprint)
                var requestId = 1L
                ControlFrameCodec.write(
                    output,
                    envelope(requestId++).setHelloRequest(
                        HelloRequest.newBuilder()
                            .setDevice(identity.toProtocol(displayName))
                            .setDeviceCertificate(ByteString.copyFrom(proof.certificate))
                            .setClientNonce(ByteString.copyFrom(proof.nonce))
                            .setIdentitySignature(ByteString.copyFrom(proof.signature))
                            .setSourceId("android-default")
                            .setSourceName(sourceName)
                            .setSourceKind(sourceKind),
                    ).build(),
                )
                stage = "等待主控端身份确认"
                val hello = ControlFrameCodec.read(input)
                    ?: throw IOException("主控端在身份握手期间关闭了连接。")
                check(hello.hasHelloResponse() && hello.helloResponse.hasController()) {
                    "主控端返回了无效的 HelloResponse。"
                }
                val controller = hello.helloResponse.controller
                val controllerId = GuidWire.fromDotNetBytes(controller.deviceId.toByteArray())
                check(MessageDigest.isEqual(
                    controllerFingerprint,
                    controller.certificateFingerprint.toByteArray(),
                )) { "蓝牙主控端身份指纹不一致。" }
                val stored = trustStore.fingerprint(controllerId)
                check(stored == null || MessageDigest.isEqual(stored, controllerFingerprint)) {
                    "主控端身份已变化，请先在系统中删除旧信任。"
                }

                if (!hello.helloResponse.paired) {
                    if (pairingCode.isBlank()) throw BluetoothPairingRequiredException()
                    val pair = PairRequest.newBuilder()
                        .setSender(identity.toProtocol(displayName))
                        .setSenderNonce(ByteString.copyFrom(ByteArray(32).also(SecureRandom()::nextBytes)))
                        .setOneTimeCode(pairingCode.trim())
                        .build()
                    ControlFrameCodec.write(output, envelope(requestId++).setPairRequest(pair).build())
                    stage = "等待蓝牙配对确认"
                    val pairResponse = ControlFrameCodec.read(input)
                        ?: throw IOException("主控端在配对期间关闭了连接。")
                    if (!pairResponse.hasPairResponse() || !pairResponse.pairResponse.accepted) {
                        val reason = if (pairResponse.hasPairResponse()) {
                            pairResponse.pairResponse.error.name
                        } else "INVALID_RESPONSE"
                        throw BluetoothPairingRejectedException("蓝牙配对被拒绝：$reason")
                    }
                    trustStore.trust(controllerId, controllerFingerprint)
                } else {
                    check(stored != null) { "主控端报告已配对，但手机缺少本地信任记录。" }
                }

                stage = "发送蓝牙音频格式"
                val frameSamples = frameSamples(selectedCodec)
                val requestedFormat = StartStream.newBuilder()
                    .setStreamId(1)
                    .setCodec(selectedCodec.wireId)
                    .setSampleRate(48_000)
                    .setChannelCount(selectedChannelMode.channels)
                    .setFrameDurationMs(frameDurationMs(selectedCodec))
                    .setFrameSamples(frameSamples)
                    .build()
                ControlFrameCodec.write(
                    output,
                    envelope(requestId++).setStartStream(requestedFormat).build(),
                )
                stage = "等待主控端确认音频格式"
                val offer = ControlFrameCodec.read(input)
                    ?: throw IOException("主控端未下发蓝牙音频会话。")
                check(offer.hasStartStream()) { "主控端未返回 StartStream。" }
                val stream = offer.startStream
                check(
                    stream.codec == selectedCodec.wireId && stream.sampleRate == 48_000 &&
                        stream.channelCount == selectedChannelMode.channels &&
                        stream.frameDurationMs == frameDurationMs(selectedCodec) &&
                        stream.frameSamples == frameSamples
                ) { "主控端下发了不兼容的蓝牙音频格式。" }

                BluetoothAudioSession(
                    socket, input, output, controllerId,
                    controller.displayName.ifBlank { peer.displayName },
                    selectedCodec, selectedChannelMode,
                )
            } catch (error: Throwable) {
                runCatching { socket.close() }
                if (error is BluetoothPairingRequiredException ||
                    error is BluetoothPairingRejectedException
                ) {
                    throw error
                }
                if (error is IOException && !error.message.orEmpty().startsWith(stage)) {
                    throw IOException("$stage：${error.message ?: "连接被关闭"}", error)
                }
                throw error
            }
        }

        private fun envelope(requestId: Long): Envelope.Builder = Envelope.newBuilder()
            .setVersion(ProtocolVersion.newBuilder().setMajor(1).setMinor(0))
            .setRequestId(requestId)

        fun frameSamples(codec: BluetoothStreamCodec): Int =
            if (codec == BluetoothStreamCodec.AAC) 1024 else FRAME_SAMPLES

        fun frameDurationMs(codec: BluetoothStreamCodec): Int =
            if (codec == BluetoothStreamCodec.AAC) 21 else 10
    }

    @Synchronized
    fun sendFrame(encodedAudio: ByteArray, timestamp: Long) {
        val validSize = when (codec) {
            BluetoothStreamCodec.PCM16 -> FRAME_SAMPLES * channelMode.channels * 2
            BluetoothStreamCodec.IMA_ADPCM -> ADPCM_BYTES_PER_FRAME * channelMode.channels
            BluetoothStreamCodec.AAC -> encodedAudio.size
            else -> error("${codec.displayName} 尚未接入 RFCOMM 音频发送。")
        }
        require(encodedAudio.size == validSize && encodedAudio.size in 1..MAX_ENCODED_FRAME_BYTES)
        val packetBuffer = ByteArray(16 + encodedAudio.size)
        AUDIO_MAGIC.copyInto(packetBuffer, destinationOffset = 0)
        writeIntBigEndian(packetBuffer, 4, encodedAudio.size)
        writeLongBigEndian(packetBuffer, 8, timestamp)
        encodedAudio.copyInto(packetBuffer, destinationOffset = 16)
        output.write(packetBuffer)

        // PCM16 is bandwidth-heavy.  Flush two 10 ms frames together to reduce
        // Bluetooth/JNI write overhead while keeping added latency bounded to 20 ms.
        val flushFrames = if (codec == BluetoothStreamCodec.PCM16) 2 else 1
        if (++bufferedFrames >= flushFrames) {
            output.flush()
            bufferedFrames = 0
        }
    }

    fun awaitControllerDisconnect(): Nothing {
        ControllerDisconnectSignal.await(input)
    }

    override fun close() {
        runCatching { output.flush() }
        runCatching { socket.close() }
    }

    private fun writeIntBigEndian(destination: ByteArray, offset: Int, value: Int) {
        repeat(4) { index ->
            destination[offset + index] = (value ushr (24 - index * 8)).toByte()
        }
    }

    private fun writeLongBigEndian(destination: ByteArray, offset: Int, value: Long) {
        repeat(8) { index ->
            destination[offset + index] = (value ushr (56 - index * 8)).toByte()
        }
    }

}
