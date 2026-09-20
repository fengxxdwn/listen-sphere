package io.listensphere.mobile.service

import android.content.Intent
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.describeConnectionError
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.core.network.BluetoothAudioSession
import io.listensphere.mobile.core.network.BluetoothPairingRejectedException
import io.listensphere.mobile.core.network.BluetoothPairingRequiredException
import io.listensphere.mobile.core.network.ControllerDisconnectedException
import io.listensphere.mobile.core.security.AndroidIdentityStore
import io.listensphere.mobile.core.security.TrustedControllerStore
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_BLUETOOTH_ADDRESS
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_BLUETOOTH_CHANNEL_MODE
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_BLUETOOTH_CODEC
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_CONTROLLER_NAME
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_PAIRING_CODE
import java.io.IOException
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive

internal suspend fun TransportSession.runBluetoothStream(intent: Intent, kind: CaptureKind) {
    val peer = BluetoothPeer(
        address = requireNotNull(intent.getStringExtra(EXTRA_BLUETOOTH_ADDRESS)),
        displayName = intent.getStringExtra(EXTRA_CONTROLLER_NAME).orEmpty()
            .ifBlank { "ListenSphere Controller" },
    )
    val identity = AndroidIdentityStore(applicationContext).loadOrCreate()
    val trustStore = TrustedControllerStore(applicationContext)
    val capture = captureCoordinator.create(kind, intent)
    val selectedCodec = BluetoothStreamCodec.valueOf(
        intent.getStringExtra(EXTRA_BLUETOOTH_CODEC) ?: BluetoothStreamCodec.IMA_ADPCM.name,
    )
    val selectedChannelMode = BluetoothChannelMode.valueOf(
        intent.getStringExtra(EXTRA_BLUETOOTH_CHANNEL_MODE) ?: BluetoothChannelMode.MONO.name,
    )
    var pairingCode = intent.getStringExtra(EXTRA_PAIRING_CODE).orEmpty()
    var retryIndex = 0
    try {
        capture.start()
        while (true) {
            var session: BluetoothAudioSession? = null
            try {
                mutableStatus.value = StreamStatus(
                    phase = StreamPhase.CONNECTING,
                    title = if (retryIndex == 0) {
                        "正在通过蓝牙连接 ${peer.displayName}"
                    } else {
                        "正在重新连接 ${peer.displayName}"
                    },
                    detail = "正在验证设备身份和配对状态…",
                )
                session = BluetoothAudioSession.connect(
                    applicationContext, peer, identity, trustStore, pairingCode,
                    selectedCodec, selectedChannelMode,
                    sourceProtocolKind(kind), sourceLabel(kind),
                )
                pairingCode = ""
                retryIndex = 0
                mutableStatus.value = StreamStatus(
                    phase = StreamPhase.STREAMING,
                    title = "正在通过蓝牙发送到 ${session.controllerName}",
                    detail = "${sourceLabel(kind)} · ${session.codec.displayName} " +
                        "${session.channelMode.displayName} · 48 kHz",
                    transportMode = TransportMode.BLUETOOTH,
                    targetName = session.controllerName,
                )
                updateNotification("蓝牙发送：${sourceLabel(kind)}")
                streamBluetooth(capture, session)
            } catch (error: Throwable) {
                // An explicit stop closes RFCOMM and may surface as IOException. Observe the
                // cancelled stream job first so the UI cannot be left in a false reconnect
                // state after the foreground service has already stopped.
                coroutineContext.ensureActive()
                if (error is BluetoothPairingRequiredException ||
                    error is BluetoothPairingRejectedException
                ) {
                    mutableStatus.value = StreamStatus(
                        phase = StreamPhase.PAIRING_REQUIRED,
                        title = if (error is BluetoothPairingRejectedException) {
                            "验证码无效"
                        } else {
                            "需要输入验证码"
                        },
                        detail = if (error is BluetoothPairingRejectedException) {
                            error.message ?: "蓝牙配对未完成，请重新输入验证码。"
                        } else {
                            "请在主控端生成六位验证码，然后重新连接蓝牙。"
                        },
                    )
                    updateNotification("等待六位蓝牙配对验证码")
                    return
                }
                if (error is ControllerDisconnectedException) {
                    mutableStatus.value = StreamStatus(
                        phase = StreamPhase.IDLE,
                        title = "已由主控端断开",
                        detail = error.message ?: "主控端已停止本次蓝牙连接。",
                    )
                    updateNotification("主控端已断开蓝牙连接")
                    return
                }
                if (error is CancellationException ||
                    !isRetryableNetworkError(error) ||
                    retryIndex >= RECONNECT_DELAYS_SECONDS.size
                ) throw error

                val delaySeconds = RECONNECT_DELAYS_SECONDS[retryIndex++]
                val presentation = describeConnectionError(TransportMode.BLUETOOTH, error)
                mutableStatus.value = StreamStatus(
                    phase = StreamPhase.CONNECTING,
                    title = "蓝牙中断，正在自动重连",
                    detail = "${presentation.detail}\n" +
                        "$delaySeconds 秒后进行第 $retryIndex 次尝试…",
                )
                updateNotification("蓝牙中断，$delaySeconds 秒后重连")
                delay(delaySeconds * 1_000L)
            } finally {
                session?.close()
            }
        }
    } finally {
        capture.close()
        captureCoordinator.close()
    }
}
