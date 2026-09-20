package io.listensphere.mobile.service

import android.content.Intent
import io.listensphere.mobile.core.audio.BluetoothPcm16Encoder
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.core.usb.UsbAccessoryAudioSession
import io.listensphere.mobile.core.usb.UsbAccessoryPairingRejectedException
import io.listensphere.mobile.core.usb.UsbAccessoryPairingRequiredException
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_PAIRING_CODE
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.ensureActive

internal suspend fun TransportSession.runUsbAccessoryStream(intent: Intent, kind: CaptureKind) {
    val capture = captureCoordinator.create(kind, intent)
    var session: UsbAccessoryAudioSession? = null
    try {
        capture.start()
        mutableStatus.value = StreamStatus(
            phase = StreamPhase.CONNECTING,
            title = "正在建立原生 USB 连接",
            detail = "正在验证主控端身份和配对状态…",
        )
        session = UsbAccessoryAudioSession.connect(
            applicationContext,
            intent.getStringExtra(EXTRA_PAIRING_CODE).orEmpty(),
            sourceProtocolKind(kind),
            sourceLabel(kind),
        )
        mutableStatus.value = StreamStatus(
            phase = StreamPhase.STREAMING,
            title = "正在通过 USB 发送到 ${session.controllerName}",
            detail = "${sourceLabel(kind)} · PCM16 双声道 · 不使用网络",
            transportMode = TransportMode.USB,
            targetName = session.controllerName,
        )
        updateNotification("USB 发送：${sourceLabel(kind)}")
        val stereo = FloatArray(960)
        val pcm = ByteArray(480 * 2 * 2)
        var timestamp = 0L
        var frames = 0L
        var bytesSent = 0L
        var lastUiUpdate = 0L
        while (true) {
            coroutineContext.ensureActive()
            val peak = capture.readStereoFrame(stereo)
            BluetoothPcm16Encoder.encode(stereo, pcm, 2)
            session.sendPcm16(pcm, timestamp)
            timestamp += 480
            frames++
            bytesSent += pcm.size + 24L
            val now = android.os.SystemClock.elapsedRealtime()
            if (now - lastUiUpdate >= 100) {
                mutableStatus.value = mutableStatus.value.copy(
                    peak = peak,
                    framesSent = frames,
                    datagramsSent = frames,
                    bytesSent = bytesSent,
                )
                lastUiUpdate = now
            }
        }
    } catch (error: UsbAccessoryPairingRejectedException) {
        mutableStatus.value = StreamStatus(
            phase = StreamPhase.PAIRING_REQUIRED,
            title = "验证码无效",
            detail = error.message ?: "USB 配对未完成，请重新输入验证码。",
        )
        updateNotification("USB 配对未完成，请重新输入验证码")
    } catch (error: UsbAccessoryPairingRequiredException) {
        mutableStatus.value = StreamStatus(
            phase = StreamPhase.PAIRING_REQUIRED,
            title = "需要输入验证码",
            detail = "请在主控端生成六位验证码，然后重新连接 USB。",
        )
        updateNotification("等待六位 USB 配对验证码")
    } finally {
        session?.close()
        capture.close()
        captureCoordinator.close()
    }
}
