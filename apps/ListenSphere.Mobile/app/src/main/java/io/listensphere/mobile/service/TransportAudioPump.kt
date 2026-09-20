package io.listensphere.mobile.service

import io.listensphere.mobile.core.audio.AacLcEncoder
import io.listensphere.mobile.core.audio.BluetoothPcm16Encoder
import io.listensphere.mobile.core.audio.FloatAudioCapture
import io.listensphere.mobile.core.audio.ImaAdpcmEncoder
import io.listensphere.mobile.core.audio.UdpAudioSender
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.network.BluetoothAudioSession
import io.listensphere.mobile.core.network.ListenSphereControlClient
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.select

internal suspend fun TransportSession.streamBluetooth(
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

internal suspend fun TransportSession.sendBluetoothAudio(
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

internal suspend fun TransportSession.streamUntilDisconnected(
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
