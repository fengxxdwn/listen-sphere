package io.listensphere.mobile.service

import android.content.Context
import android.content.Intent
import io.listensphere.mobile.core.audio.UdpAudioSender
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.ControllerEndpoint
import io.listensphere.mobile.core.model.describeConnectionError
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.core.network.ControlConnectResult
import io.listensphere.mobile.core.network.ControllerDisconnectedException
import io.listensphere.mobile.core.network.ListenSphereControlClient
import io.listensphere.mobile.core.security.AndroidIdentityStore
import io.listensphere.mobile.core.security.TrustedControllerStore
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_CONTROLLER_NAME
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_DEVICE_ID
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_HOST
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_PAIRING_CODE
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_PORT
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_TRANSPORT_MODE
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_USB_NATIVE
import java.net.InetAddress
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive

/** Owns one transport run. Wire handshakes, buffers and retry timings are unchanged. */
internal class TransportSession(
    internal val applicationContext: Context,
    internal val mutableStatus: SessionStatus,
    internal val captureCoordinator: AudioCaptureCoordinator,
    internal val updateNotification: (String) -> Unit,
) {
    internal val RECONNECT_DELAYS_SECONDS = intArrayOf(1, 2, 4, 8, 12, 15)
    suspend fun run(intent: Intent, kind: CaptureKind) {
        val mode = TransportMode.valueOf(
            intent.getStringExtra(EXTRA_TRANSPORT_MODE) ?: TransportMode.WIFI.name,
        )
        if (mode == TransportMode.BLUETOOTH) {
            runBluetoothStream(intent, kind)
            return
        }
        if (mode == TransportMode.USB && intent.getBooleanExtra(EXTRA_USB_NATIVE, true)) {
            runUsbAccessoryStream(intent, kind)
            return
        }
        val host = requireNotNull(intent.getStringExtra(EXTRA_HOST))
        val endpoint = ControllerEndpoint(
            deviceId = intent.getStringExtra(EXTRA_DEVICE_ID)?.takeIf(String::isNotBlank)
                ?.let(java.util.UUID::fromString),
            displayName = intent.getStringExtra(EXTRA_CONTROLLER_NAME).orEmpty()
                .ifBlank { "ListenSphere Controller" },
            address = InetAddress.getByName(host),
            port = intent.getIntExtra(EXTRA_PORT, 0),
        )
        require(endpoint.port in 1..65_535) { "主控端端口无效。" }
        val identity = AndroidIdentityStore(applicationContext).loadOrCreate()
        val trustStore = TrustedControllerStore(applicationContext)
        val capture = captureCoordinator.create(kind, intent)
        var pairingCode = intent.getStringExtra(EXTRA_PAIRING_CODE).orEmpty()
        var retryIndex = 0
        try {
            capture.start()
            while (true) {
                val client = ListenSphereControlClient(identity, trustStore)
                var sender: UdpAudioSender? = null
                try {
                    mutableStatus.value = StreamStatus(
                        phase = StreamPhase.CONNECTING,
                        title = if (retryIndex == 0) {
                            "正在连接 ${endpoint.displayName}"
                        } else {
                            "正在重新连接 ${endpoint.displayName}"
                        },
                        detail = "正在验证设备身份和配对状态…",
                    )
                    when (val result = client.connect(
                        endpoint,
                        pairingCode,
                        mode,
                        sourceProtocolKind(kind),
                        sourceLabel(kind),
                    )) {
                        is ControlConnectResult.PairingRequired -> {
                            mutableStatus.value = StreamStatus(
                                phase = StreamPhase.PAIRING_REQUIRED,
                                title = "需要输入验证码",
                                detail = "请在主控端生成六位验证码，然后重新连接。",
                            )
                            updateNotification("等待六位配对验证码")
                            return
                        }

                        is ControlConnectResult.PairingRejected -> {
                            mutableStatus.value = StreamStatus(
                                phase = StreamPhase.PAIRING_REQUIRED,
                                title = "验证码无效",
                                detail = result.reason,
                            )
                            updateNotification("配对未完成，请重新输入验证码")
                            return
                        }

                        is ControlConnectResult.Connected -> {
                            pairingCode = ""
                            retryIndex = 0
                            val activeSender = UdpAudioSender(result.audioSession)
                            sender = activeSender
                            mutableStatus.value = StreamStatus(
                                phase = StreamPhase.STREAMING,
                                title = "正在发送到 ${result.controllerName}",
                                detail = "${sourceLabel(kind)} · ${capture.formatLabel}",
                                transportMode = mode,
                                targetName = result.controllerName,
                            )
                            updateNotification("正在发送：${sourceLabel(kind)}")
                            streamUntilDisconnected(client, capture, activeSender)
                        }
                    }
                } catch (error: Throwable) {
                    // Closing the active transport is part of an explicit stop. The close can
                    // surface as IOException before the cancelled parent is observed, so check
                    // the coroutine state before publishing an automatic-reconnect status.
                    coroutineContext.ensureActive()
                    if (error is ControllerDisconnectedException) {
                        mutableStatus.value = StreamStatus(
                            phase = StreamPhase.IDLE,
                            title = "已由主控端断开",
                            detail = error.message ?: "主控端已停止本次连接。",
                        )
                        updateNotification("主控端已断开连接")
                        return
                    }
                    if (error is CancellationException ||
                        !isRetryableNetworkError(error) ||
                        retryIndex >= RECONNECT_DELAYS_SECONDS.size
                    ) {
                        throw error
                    }

                    val delaySeconds = RECONNECT_DELAYS_SECONDS[retryIndex++]
                    val presentation = describeConnectionError(mode, error)
                    mutableStatus.value = StreamStatus(
                        phase = StreamPhase.CONNECTING,
                        title = "网络中断，正在自动重连",
                        detail = "${presentation.detail}\n" +
                            "$delaySeconds 秒后进行第 $retryIndex 次尝试…",
                    )
                    updateNotification("网络中断，$delaySeconds 秒后重连")
                    delay(delaySeconds * 1_000L)
                } finally {
                    sender?.close()
                    client.close()
                }
            }
        } finally {
            capture.close()
            captureCoordinator.close()
        }
    }

    internal fun isRetryableNetworkError(error: Throwable): Boolean = TransportRetryPolicy.isRetryable(error)

    internal fun sourceLabel(kind: CaptureKind): String = when (kind) {
        CaptureKind.MICROPHONE -> "手机麦克风"
        CaptureKind.DEVICE_PLAYBACK -> "手机播放声音（仅限系统允许的应用）"
    }

    internal fun sourceProtocolKind(kind: CaptureKind): String = when (kind) {
        CaptureKind.MICROPHONE -> "microphone"
        CaptureKind.DEVICE_PLAYBACK -> "device_playback"
    }

}
