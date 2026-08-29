package io.listensphere.mobile.core.usb

import android.content.Context
import android.os.Build
import com.google.protobuf.ByteString
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.protocol.GuidWire
import io.listensphere.mobile.core.security.AndroidIdentityStore
import io.listensphere.mobile.core.security.TrustedControllerStore
import io.listensphere.protocol.v1.Envelope
import io.listensphere.protocol.v1.HelloRequest
import io.listensphere.protocol.v1.PairRequest
import io.listensphere.protocol.v1.ProtocolVersion
import io.listensphere.protocol.v1.StartStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayInputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.UUID

class UsbAccessoryPairingRequiredException : IOException("主控端需要六位配对验证码。")
class UsbAccessoryPairingRejectedException(message: String) : IOException(message)

class UsbAccessoryAudioSession private constructor(
    private val accessory: OpenUsbAccessory,
    private val input: DataInputStream,
    private val output: DataOutputStream,
    val controllerId: UUID,
    val controllerName: String,
) : AutoCloseable {
    private var sequence = 1L

    companion object {
        private val requestMagic = byteArrayOf('L'.code.toByte(), 'S'.code.toByte(), 'U'.code.toByte(), 'R'.code.toByte())
        private val responseMagic = byteArrayOf('L'.code.toByte(), 'S'.code.toByte(), 'U'.code.toByte(), 'H'.code.toByte())

        suspend fun connect(
            context: Context,
            pairingCode: String,
            sourceKind: String = "device_playback",
            sourceName: String = "手机播放声音",
        ): UsbAccessoryAudioSession = withContext(Dispatchers.IO) {
            val openAccessory = AndroidUsbAccessoryManager(context).open()
            val input = DataInputStream(BufferedInputStream(FileInputStream(openAccessory.descriptor.fileDescriptor), 32 * 1024))
            val output = DataOutputStream(BufferedOutputStream(FileOutputStream(openAccessory.descriptor.fileDescriptor), 32 * 1024))
            var stage = "建立原生 USB 通道"
            try {
                UsbTransportFrameCodec.write(
                    output,
                    UsbFrame(UsbFrameKind.STATUS, 0, 0, 0, requestMagic + 1.toByte()),
                )
                stage = "读取主控端身份"
                val identityFrame = UsbTransportFrameCodec.read(input)
                check(identityFrame.kind == UsbFrameKind.STATUS) { "主控端未返回 USB 身份。" }
                val identityInput = DataInputStream(ByteArrayInputStream(identityFrame.payload))
                val magic = ByteArray(4).also(identityInput::readFully)
                check(magic.contentEquals(responseMagic)) { "主控端 USB 身份响应无效。" }
                val protocolMajor = identityInput.readUnsignedByte()
                check(protocolMajor == 1) { "主控端 USB 协议版本不兼容：$protocolMajor。" }
                val nameLength = identityInput.readUnsignedShort()
                require(nameLength in 1..512) { "主控端名称长度无效。" }
                val announcedName = ByteArray(nameLength).also(identityInput::readFully).toString(Charsets.UTF_8)
                val certificateLength = identityInput.readUnsignedShort()
                require(certificateLength in 1..8192) { "主控端身份凭据长度无效。" }
                val certificateBytes = ByteArray(certificateLength).also(identityInput::readFully)
                val certificate = CertificateFactory.getInstance("X.509")
                    .generateCertificate(certificateBytes.inputStream()) as X509Certificate
                certificate.checkValidity()
                val controllerFingerprint = MessageDigest.getInstance("SHA-256").digest(certificate.encoded)

                val localIdentity = AndroidIdentityStore(context).loadOrCreate()
                val trustStore = TrustedControllerStore(context)
                val displayName = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
                val proof = localIdentity.createProof(controllerFingerprint)
                var requestId = 1L
                writeControl(
                    output,
                    envelope(requestId++).setHelloRequest(
                        HelloRequest.newBuilder()
                            .setDevice(localIdentity.toProtocol(displayName))
                            .setDeviceCertificate(ByteString.copyFrom(proof.certificate))
                            .setClientNonce(ByteString.copyFrom(proof.nonce))
                            .setIdentitySignature(ByteString.copyFrom(proof.signature))
                            .setSourceId("android-default")
                            .setSourceName(sourceName)
                            .setSourceKind(sourceKind),
                    ).build(),
                    requestId,
                )
                stage = "验证主控端身份"
                val hello = readControl(input)
                check(hello.hasHelloResponse() && hello.helloResponse.hasController()) {
                    "主控端返回了无效的 HelloResponse。"
                }
                val controller = hello.helloResponse.controller
                val controllerId = GuidWire.fromDotNetBytes(controller.deviceId.toByteArray())
                check(MessageDigest.isEqual(controllerFingerprint, controller.certificateFingerprint.toByteArray())) {
                    "USB 主控端身份指纹不一致。"
                }
                val stored = trustStore.fingerprint(controllerId)
                check(stored == null || MessageDigest.isEqual(stored, controllerFingerprint)) {
                    "主控端身份已变化，请先删除旧信任。"
                }

                if (!hello.helloResponse.paired) {
                    if (pairingCode.isBlank()) throw UsbAccessoryPairingRequiredException()
                    val pair = PairRequest.newBuilder()
                        .setSender(localIdentity.toProtocol(displayName))
                        .setSenderNonce(ByteString.copyFrom(ByteArray(32).also(SecureRandom()::nextBytes)))
                        .setOneTimeCode(pairingCode.trim())
                        .build()
                    writeControl(output, envelope(requestId++).setPairRequest(pair).build(), requestId)
                    val pairResponse = readControl(input)
                    if (!pairResponse.hasPairResponse() || !pairResponse.pairResponse.accepted) {
                        throw UsbAccessoryPairingRejectedException(
                            "USB 配对被拒绝：${pairResponse.pairResponse.error.name}",
                        )
                    }
                    trustStore.trust(controllerId, controllerFingerprint)
                } else {
                    check(stored != null) { "主控端报告已配对，但手机缺少本地信任记录。" }
                }

                stage = "协商 USB 音频格式"
                val format = StartStream.newBuilder()
                    .setStreamId(1)
                    .setCodec(BluetoothStreamCodec.PCM16.wireId)
                    .setSampleRate(48_000)
                    .setChannelCount(2)
                    .setFrameDurationMs(10)
                    .setFrameSamples(480)
                    .build()
                writeControl(output, envelope(requestId++).setStartStream(format).build(), requestId)
                val accepted = readControl(input)
                check(accepted.hasStartStream()) { "主控端未确认 USB 音频格式。" }

                UsbAccessoryAudioSession(
                    openAccessory,
                    input,
                    output,
                    controllerId,
                    controller.displayName.ifBlank { announcedName },
                )
            } catch (error: Throwable) {
                runCatching { input.close() }
                runCatching { output.close() }
                runCatching { openAccessory.close() }
                if (error is UsbAccessoryPairingRequiredException ||
                    error is UsbAccessoryPairingRejectedException
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

        private fun writeControl(output: DataOutputStream, envelope: Envelope, sequence: Long) =
            UsbTransportFrameCodec.write(
                output,
                UsbFrame(UsbFrameKind.CONTROL, 0, sequence, 0, envelope.toByteArray()),
            )

        private fun readControl(input: DataInputStream): Envelope {
            val frame = UsbTransportFrameCodec.read(input)
            check(frame.kind == UsbFrameKind.CONTROL) { "主控端返回了非控制 USB 帧。" }
            return Envelope.parseFrom(frame.payload)
        }
    }

    @Synchronized
    fun sendPcm16(pcm: ByteArray, timestamp: Long) {
        require(pcm.size == 480 * 2 * 2)
        UsbTransportFrameCodec.write(
            output,
            UsbFrame(UsbFrameKind.AUDIO, 0, sequence++, timestamp, pcm),
        )
    }

    override fun close() {
        runCatching { output.flush() }
        runCatching { input.close() }
        runCatching { output.close() }
        runCatching { accessory.close() }
    }
}
