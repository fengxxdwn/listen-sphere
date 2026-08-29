package io.listensphere.mobile.core.network

import android.os.Build
import android.os.SystemClock
import com.google.protobuf.ByteString
import io.listensphere.mobile.core.model.AudioSession
import io.listensphere.mobile.core.model.ControllerEndpoint
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.core.protocol.ControlFrameCodec
import io.listensphere.mobile.core.protocol.GuidWire
import io.listensphere.mobile.core.security.LocalIdentity
import io.listensphere.mobile.core.security.TrustedControllerStore
import io.listensphere.protocol.v1.DeviceIdentity
import io.listensphere.protocol.v1.Envelope
import io.listensphere.protocol.v1.Heartbeat
import io.listensphere.protocol.v1.HelloRequest
import io.listensphere.protocol.v1.PairRequest
import io.listensphere.protocol.v1.ProtocolVersion
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket
import java.io.EOFException
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.UUID
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import kotlin.coroutines.coroutineContext

sealed interface ControlConnectResult {
    data class Connected(
        val controllerId: UUID,
        val controllerName: String,
        val audioSession: AudioSession,
    ) : ControlConnectResult

    data class PairingRequired(val controllerName: String) : ControlConnectResult
    data class PairingRejected(val reason: String) : ControlConnectResult
}

class ControllerDisconnectedException(message: String) : Exception(message)

class ListenSphereControlClient(
    private val identity: LocalIdentity,
    private val trustStore: TrustedControllerStore,
) : AutoCloseable {
    private val requestId = AtomicLong()
    private val writeLock = Any()
    private var socket: SSLSocket? = null
    private var connectedControllerId: UUID? = null

    suspend fun connect(
        endpoint: ControllerEndpoint,
        pairingCode: String,
        transportMode: TransportMode = TransportMode.WIFI,
        sourceKind: String = "device_playback",
        sourceName: String = "手机播放声音",
    ): ControlConnectResult = withContext(Dispatchers.IO) {
        close()
        val expectedFingerprint = endpoint.deviceId?.let(trustStore::fingerprint)
        val trustManager = PinningTrustManager(expectedFingerprint)
        val context = SSLContext.getInstance("TLSv1.3").apply {
            init(
                null,
                arrayOf<TrustManager>(trustManager),
                SecureRandom(),
            )
        }
        val tcp = Socket()
        tcp.connect(InetSocketAddress(endpoint.address, endpoint.port), 5_000)
        tcp.getOutputStream().apply {
            write(ControlTransportPreface.encode(transportMode))
            flush()
        }
        val ssl = context.socketFactory.createSocket(
            tcp,
            endpoint.address.hostAddress,
            endpoint.port,
            true,
        ) as SSLSocket
        socket = ssl
        ssl.enabledProtocols = ssl.supportedProtocols.filter { it == "TLSv1.3" }.toTypedArray()
        ssl.soTimeout = 10_000
        ssl.startHandshake()

        val displayName = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
        val remoteFingerprint = trustManager.remoteFingerprint
            ?: error("无法读取主控端 TLS 身份。")
        val proof = identity.createProof(remoteFingerprint)
        write(
            envelope().setHelloRequest(
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
        val hello = read() ?: error("主控端在握手期间关闭了连接。")
        requireCompatible(hello)
        check(hello.hasHelloResponse() && hello.helloResponse.hasController()) {
            "主控端返回了无效的 HelloResponse。"
        }
        val controllerIdentity = hello.helloResponse.controller
        val controllerId = GuidWire.fromDotNetBytes(controllerIdentity.deviceId.toByteArray())
        check(MessageDigest.isEqual(
            remoteFingerprint,
            controllerIdentity.certificateFingerprint.toByteArray(),
        )) { "主控端证书与协议身份指纹不一致。" }
        check(endpoint.deviceId == null || endpoint.deviceId == controllerId) {
            "发现到的主控端 UUID 与连接目标不一致。"
        }
        val storedFingerprint = trustStore.fingerprint(controllerId)
        check(storedFingerprint == null || MessageDigest.isEqual(storedFingerprint, remoteFingerprint)) {
            "主控端证书已变化，请先清除该设备的信任记录。"
        }

        if (!hello.helloResponse.paired) {
            if (pairingCode.isBlank()) {
                close()
                return@withContext ControlConnectResult.PairingRequired(
                    controllerIdentity.displayName,
                )
            }
            val pair = PairRequest.newBuilder()
                .setSender(identity.toProtocol(displayName))
                .setSenderNonce(ByteString.copyFrom(ByteArray(32).also(SecureRandom()::nextBytes)))
                .setOneTimeCode(pairingCode.trim())
                .build()
            write(envelope().setPairRequest(pair).build())
            val pairResponse = read() ?: error("主控端在配对期间关闭了连接。")
            requireCompatible(pairResponse)
            if (!pairResponse.hasPairResponse() || !pairResponse.pairResponse.accepted) {
                val reason = if (pairResponse.hasPairResponse()) {
                    pairResponse.pairResponse.error.name
                } else {
                    "INVALID_RESPONSE"
                }
                close()
                return@withContext ControlConnectResult.PairingRejected(reason)
            }
            trustStore.trust(controllerId, remoteFingerprint)
        } else {
            check(storedFingerprint != null) {
                "主控端报告已配对，但手机没有本地证书固定记录。"
            }
        }

        val streamOffer = read() ?: error("主控端未下发音频会话。")
        requireCompatible(streamOffer)
        check(streamOffer.hasStartStream()) { "主控端未返回 StartStream。" }
        val offer = streamOffer.startStream
        check(
            offer.sessionId.size() == 16 &&
                offer.sessionKey.size() == 32 &&
                offer.sessionSalt.size() == 4 &&
                offer.streamId != 0 &&
                offer.codec == 1 &&
                offer.sampleRate == 48_000 &&
                offer.channelCount == 2 &&
                offer.frameDurationMs == 10 &&
                offer.frameSamples == 480 &&
                offer.udpPort in 1..65_535
        ) { "主控端下发了不兼容的音频格式。" }
        connectedControllerId = controllerId
        ssl.soTimeout = 15_000
        ControlConnectResult.Connected(
            controllerId = controllerId,
            controllerName = controllerIdentity.displayName,
            audioSession = AudioSession(
                sessionId = GuidWire.fromDotNetBytes(offer.sessionId.toByteArray()),
                streamId = offer.streamId.toLong() and 0xffff_ffffL,
                key = offer.sessionKey.toByteArray(),
                salt = offer.sessionSalt.toByteArray(),
                controllerAddress = endpoint.address,
                udpPort = offer.udpPort,
                sampleRate = offer.sampleRate,
                channelCount = offer.channelCount,
                frameSamples = offer.frameSamples,
            ),
        )
    }

    suspend fun heartbeatLoop(onRoundTrip: (Double) -> Unit) =
        withContext(Dispatchers.IO) {
            while (true) {
                coroutineContext.ensureActive()
                val started = SystemClock.elapsedRealtime()
                val heartbeat = Heartbeat.newBuilder()
                    .setMonotonicMilliseconds(started)
                    .build()
                write(envelope().setHeartbeat(heartbeat).build())
                val response = read() ?: throw EOFException("主控端已断开控制连接。")
                requireCompatible(response)
                if (response.hasDisconnect()) {
                    throw ControllerDisconnectedException(
                        response.disconnect.reason.ifBlank { "主控端已断开本次连接。" },
                    )
                }
                check(response.hasHeartbeatAck()) { "主控端未确认心跳。" }
                onRoundTrip((SystemClock.elapsedRealtime() - started).toDouble())
                delay(5_000)
            }
        }

    private fun envelope(): Envelope.Builder = Envelope.newBuilder()
        .setVersion(ProtocolVersion.newBuilder().setMajor(1).setMinor(0))
        .setRequestId(requestId.incrementAndGet())

    private fun write(envelope: Envelope) {
        val current = socket ?: error("控制连接尚未建立。")
        synchronized(writeLock) {
            ControlFrameCodec.write(current.outputStream, envelope)
        }
    }

    private fun read(): Envelope? = ControlFrameCodec.read(
        socket?.inputStream ?: error("控制连接尚未建立。"),
    )

    private fun requireCompatible(envelope: Envelope) {
        check(envelope.hasVersion() && envelope.version.major == 1) {
            "ListenSphere Protocol 主版本不兼容。"
        }
    }

    override fun close() {
        runCatching { socket?.close() }
        socket = null
        connectedControllerId = null
    }
}

internal object ControlTransportPreface {
    private val magic = byteArrayOf('L'.code.toByte(), 'S'.code.toByte(), 'T'.code.toByte(), 'H'.code.toByte())

    fun encode(mode: TransportMode): ByteArray = magic + when (mode) {
        TransportMode.WIFI -> 1.toByte()
        TransportMode.BLUETOOTH -> 2.toByte()
        TransportMode.USB -> 3.toByte()
    }
}

private class PinningTrustManager(
    private val expectedFingerprint: ByteArray?,
) : X509TrustManager {
    var remoteFingerprint: ByteArray? = null
        private set

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val certificate = chain?.firstOrNull()
            ?: throw CertificateException("主控端未提供 TLS 证书。")
        val actual = MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
        if (expectedFingerprint != null && !MessageDigest.isEqual(expectedFingerprint, actual)) {
            throw CertificateException("主控端证书固定校验失败。")
        }
        remoteFingerprint = actual
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
