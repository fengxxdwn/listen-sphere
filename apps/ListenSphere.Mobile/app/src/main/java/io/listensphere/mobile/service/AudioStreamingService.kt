package io.listensphere.mobile.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.core.app.NotificationCompat
import io.listensphere.mobile.R
import io.listensphere.mobile.core.audio.FloatAudioCapture
import io.listensphere.mobile.core.audio.BluetoothPcm16Encoder
import io.listensphere.mobile.core.audio.ImaAdpcmEncoder
import io.listensphere.mobile.core.audio.AacLcEncoder
import io.listensphere.mobile.core.audio.MicrophoneAudioCapture
import io.listensphere.mobile.core.audio.PlaybackAudioCapture
import io.listensphere.mobile.core.audio.UdpAudioSender
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.model.ControllerEndpoint
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.core.model.describeConnectionError
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus
import io.listensphere.mobile.core.network.ControlConnectResult
import io.listensphere.mobile.core.network.BluetoothAudioSession
import io.listensphere.mobile.core.network.BluetoothPairingRejectedException
import io.listensphere.mobile.core.network.BluetoothPairingRequiredException
import io.listensphere.mobile.core.network.ControllerDisconnectedException
import io.listensphere.mobile.core.network.ListenSphereControlClient
import io.listensphere.mobile.core.security.AndroidIdentityStore
import io.listensphere.mobile.core.security.TrustedControllerStore
import io.listensphere.mobile.core.usb.UsbAccessoryAudioSession
import io.listensphere.mobile.core.usb.UsbAccessoryPairingRejectedException
import io.listensphere.mobile.core.usb.UsbAccessoryPairingRequiredException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.selects.select
import java.net.InetAddress
import java.io.IOException
import java.net.SocketException
import kotlin.coroutines.coroutineContext

class AudioStreamingService : Service() {
    companion object {
        const val ACTION_START = "io.listensphere.mobile.START_STREAM"
        const val ACTION_STOP = "io.listensphere.mobile.STOP_STREAM"
        const val EXTRA_DEVICE_ID = "device_id"
        const val EXTRA_CONTROLLER_NAME = "controller_name"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_PAIRING_CODE = "pairing_code"
        const val EXTRA_CAPTURE_KIND = "capture_kind"
        const val EXTRA_TRANSPORT_MODE = "transport_mode"
        const val EXTRA_USB_NATIVE = "usb_native"
        const val EXTRA_BLUETOOTH_ADDRESS = "bluetooth_address"
        const val EXTRA_BLUETOOTH_CODEC = "bluetooth_codec"
        const val EXTRA_BLUETOOTH_CHANNEL_MODE = "bluetooth_channel_mode"
        const val EXTRA_PROJECTION_RESULT = "projection_result"
        const val EXTRA_PROJECTION_DATA = "projection_data"

        private const val CHANNEL_ID = "listensphere_stream"
        private const val NOTIFICATION_ID = 1701
        private val RECONNECT_DELAYS_SECONDS = intArrayOf(1, 2, 4, 8, 12, 15)
        private val mutableStatus = MutableStateFlow(StreamStatus())
        val status: StateFlow<StreamStatus> = mutableStatus.asStateFlow()

        fun stopIntent(context: Context): Intent = Intent(
            context,
            AudioStreamingService::class.java,
        ).setAction(ACTION_STOP)
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var streamJob: Job? = null
    private var projection: MediaProjection? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> stopStreaming()
            ACTION_START -> startStreaming(intent)
        }
        return START_NOT_STICKY
    }

    private fun startStreaming(intent: Intent) {
        streamJob?.cancel()
        val kind = CaptureKind.valueOf(
            intent.getStringExtra(EXTRA_CAPTURE_KIND) ?: CaptureKind.MICROPHONE.name,
        )
        startAsForeground(kind, "正在连接聆界主控端…")
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                runCatching { runStream(intent, kind) }
                    .onFailure { error ->
                        if (error !is CancellationException) {
                            val mode = runCatching {
                                TransportMode.valueOf(
                                    intent.getStringExtra(EXTRA_TRANSPORT_MODE)
                                        ?: TransportMode.WIFI.name,
                                )
                            }.getOrDefault(TransportMode.WIFI)
                            val presentation = describeConnectionError(mode, error)
                            mutableStatus.value = StreamStatus(
                                phase = StreamPhase.ERROR,
                                title = presentation.title,
                                detail = presentation.detail,
                            )
                            updateNotification(presentation.title)
                        }
                    }
            } finally {
                val completedJob = coroutineContext[Job]
                withContext(NonCancellable + Dispatchers.Main.immediate) {
                    if (streamJob === completedJob) {
                        streamJob = null
                        stopForeground(STOP_FOREGROUND_REMOVE)
                        stopSelf()
                    }
                }
            }
        }
        streamJob = job
        job.start()
    }

    private suspend fun runStream(intent: Intent, kind: CaptureKind) {
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
        val capture = createCapture(kind, intent)
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
            projection?.stop()
            projection = null
        }
    }

    private suspend fun runUsbAccessoryStream(intent: Intent, kind: CaptureKind) {
        val capture = createCapture(kind, intent)
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
            projection?.stop()
            projection = null
        }
    }

    private suspend fun runBluetoothStream(intent: Intent, kind: CaptureKind) {
        val peer = BluetoothPeer(
            address = requireNotNull(intent.getStringExtra(EXTRA_BLUETOOTH_ADDRESS)),
            displayName = intent.getStringExtra(EXTRA_CONTROLLER_NAME).orEmpty()
                .ifBlank { "ListenSphere Controller" },
        )
        val identity = AndroidIdentityStore(applicationContext).loadOrCreate()
        val trustStore = TrustedControllerStore(applicationContext)
        val capture = createCapture(kind, intent)
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
            projection?.stop()
            projection = null
        }
    }

    private suspend fun streamBluetooth(
        capture: FloatAudioCapture,
        session: BluetoothAudioSession,
    ) = coroutineScope {
        val control = async(Dispatchers.IO) { session.awaitControllerDisconnect() }
        val audio = async { sendBluetoothAudio(capture, session) }
        try {
            select<Unit> {
                control.onAwait { }
                audio.onAwait { }
            }
        } finally {
            // Closing the socket unblocks a pending RFCOMM read when the user stops
            // streaming or the audio writer fails.
            session.close()
            control.cancel()
            audio.cancel()
        }
    }

    private suspend fun sendBluetoothAudio(
        capture: FloatAudioCapture,
        session: BluetoothAudioSession,
    ) {
        val stereo = FloatArray(960)
        val channels = session.channelMode.channels
        val pcm = ByteArray(BluetoothPcm16Encoder.outputBytes(channels))
        val payload = when (session.codec) {
            BluetoothStreamCodec.PCM16 -> pcm
            BluetoothStreamCodec.IMA_ADPCM -> ByteArray(ImaAdpcmEncoder.encodedBytes(channels))
            BluetoothStreamCodec.AAC -> null
            else -> error("${session.codec.displayName} 尚未接入 RFCOMM 音频发送。")
        }
        val aacEncoder = if (session.codec == BluetoothStreamCodec.AAC) AacLcEncoder(channels) else null
        var timestamp = 0L
        var frames = 0L
        var lastUiUpdate = 0L
        var bytesSent = 0L
        try {
            while (true) {
                coroutineContext.ensureActive()
                val peak = capture.readStereoFrame(stereo)
                BluetoothPcm16Encoder.encode(stereo, pcm, channels)
                val encoded = when (session.codec) {
                    BluetoothStreamCodec.PCM16 -> payload
                    BluetoothStreamCodec.IMA_ADPCM -> requireNotNull(payload).also {
                        ImaAdpcmEncoder.encode(pcm, it, channels)
                    }
                    BluetoothStreamCodec.AAC -> requireNotNull(aacEncoder).offer(pcm)
                    else -> null
                }
                if (encoded != null) {
                    session.sendFrame(encoded, timestamp)
                    timestamp += BluetoothAudioSession.frameSamples(session.codec)
                    frames++
                    bytesSent += encoded.size + 16L
                }
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
        } finally {
            aacEncoder?.close()
        }
    }

    private suspend fun streamUntilDisconnected(
        client: ListenSphereControlClient,
        capture: FloatAudioCapture,
        sender: UdpAudioSender,
    ) = coroutineScope {
        launch {
            client.heartbeatLoop { roundTrip ->
                mutableStatus.value = mutableStatus.value.copy(
                    roundTripMilliseconds = roundTrip,
                )
            }
        }
        val samples = FloatArray(960)
        var timestamp = 0L
        var lastUiUpdate = 0L
        while (true) {
            coroutineContext.ensureActive()
            val peak = capture.readStereoFrame(samples)
            sender.sendFrame(samples, timestamp)
            timestamp += 480
            val now = android.os.SystemClock.elapsedRealtime()
            if (now - lastUiUpdate >= 100) {
                val stats = sender.statistics
                mutableStatus.value = mutableStatus.value.copy(
                    peak = peak,
                    framesSent = stats.framesSent,
                    datagramsSent = stats.datagramsSent,
                    bytesSent = stats.bytesSent,
                )
                lastUiUpdate = now
            }
        }
    }

    private fun isRetryableNetworkError(error: Throwable): Boolean =
        generateSequence(error) { it.cause }.any {
            it is IOException || it is SocketException
        }

    private fun createCapture(kind: CaptureKind, intent: Intent): FloatAudioCapture = when (kind) {
        CaptureKind.MICROPHONE -> MicrophoneAudioCapture()
        CaptureKind.DEVICE_PLAYBACK -> {
            val resultCode = intent.getIntExtra(EXTRA_PROJECTION_RESULT, Int.MIN_VALUE)
            val data = if (Build.VERSION.SDK_INT >= 33) {
                intent.getParcelableExtra(EXTRA_PROJECTION_DATA, Intent::class.java)
            } else {
                @Suppress("DEPRECATION")
                intent.getParcelableExtra(EXTRA_PROJECTION_DATA)
            } ?: error("缺少系统音频捕获授权。")
            val manager = getSystemService(MediaProjectionManager::class.java)
            projection = manager.getMediaProjection(resultCode, data).apply {
                registerCallback(
                    object : MediaProjection.Callback() {
                        override fun onStop() {
                            streamJob?.cancel()
                        }
                    },
                    Handler(Looper.getMainLooper()),
                )
            }
            PlaybackAudioCapture(requireNotNull(projection))
        }
    }

    private fun stopStreaming() {
        streamJob?.cancel()
        streamJob = null
        projection?.stop()
        projection = null
        mutableStatus.value = StreamStatus()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun startAsForeground(kind: CaptureKind, text: String) {
        val type = when (kind) {
            CaptureKind.MICROPHONE -> ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            CaptureKind.DEVICE_PLAYBACK -> ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
        }
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification(text), type)
        } else {
            startForeground(NOTIFICATION_ID, notification(text))
        }
    }

    private fun updateNotification(text: String) {
        getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, notification(text))
    }

    private fun notification(text: String): Notification = NotificationCompat.Builder(this, CHANNEL_ID)
        .setSmallIcon(R.drawable.ic_launcher_foreground)
        .setContentTitle("聆界 · ListenSphere Mobile")
        .setContentText(text)
        .setOngoing(true)
        .setOnlyAlertOnce(true)
        .build()

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.stream_channel_name),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = getString(R.string.stream_channel_description)
        }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private fun sourceLabel(kind: CaptureKind): String = when (kind) {
        CaptureKind.MICROPHONE -> "手机麦克风"
        CaptureKind.DEVICE_PLAYBACK -> "手机播放声音（仅限系统允许的应用）"
    }

    private fun sourceProtocolKind(kind: CaptureKind): String = when (kind) {
        CaptureKind.MICROPHONE -> "microphone"
        CaptureKind.DEVICE_PLAYBACK -> "device_playback"
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        scope.cancel()
        projection?.stop()
        super.onDestroy()
    }

}
