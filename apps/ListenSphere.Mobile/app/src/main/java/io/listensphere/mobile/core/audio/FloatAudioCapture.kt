package io.listensphere.mobile.core.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.projection.MediaProjection

interface FloatAudioCapture : AutoCloseable {
    val formatLabel: String
    fun start()
    fun readStereoFrame(destination: FloatArray): Float
}

class MicrophoneAudioCapture : FloatAudioCapture {
    private val recorder: AudioRecord
    private val usesFloat: Boolean
    private val floatMono = FloatArray(480)
    private val shortMono = ShortArray(480)

    override val formatLabel: String
        get() = if (usesFloat) "48 kHz · Float32" else "48 kHz · PCM16 回退"

    init {
        val floatRecorder = createMicrophoneRecorder(AudioFormat.ENCODING_PCM_FLOAT)
        usesFloat = floatRecorder != null
        recorder = floatRecorder
            ?: createMicrophoneRecorder(AudioFormat.ENCODING_PCM_16BIT)
            ?: error("手机麦克风无法初始化为 48 kHz Float32 或 PCM16。")
    }

    override fun start() = recorder.startRecording()

    override fun readStereoFrame(destination: FloatArray): Float {
        require(destination.size >= 960)
        if (usesFloat) readFully(recorder, floatMono, 480) else readFully(recorder, shortMono, 480)
        var peak = 0f
        repeat(480) { index ->
            val sample = if (usesFloat) {
                floatMono[index].coerceIn(-1f, 1f)
            } else {
                pcm16ToFloat(shortMono[index])
            }
            destination[index * 2] = sample
            destination[index * 2 + 1] = sample
            peak = maxOf(peak, kotlin.math.abs(sample))
        }
        return peak
    }

    override fun close() {
        runCatching { recorder.stop() }
        recorder.release()
    }
}

class PlaybackAudioCapture(mediaProjection: MediaProjection) : FloatAudioCapture {
    private val recorder: AudioRecord
    private val usesFloat: Boolean
    private val floatStereo = FloatArray(960)
    private val shortStereo = ShortArray(960)

    override val formatLabel: String
        get() = if (usesFloat) "48 kHz · Float32" else "48 kHz · PCM16 回退"

    init {
        val configuration = AudioPlaybackCaptureConfiguration.Builder(mediaProjection)
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
            .build()
        val floatRecorder = createPlaybackRecorder(
            configuration,
            AudioFormat.ENCODING_PCM_FLOAT,
        )
        usesFloat = floatRecorder != null
        recorder = floatRecorder
            ?: createPlaybackRecorder(configuration, AudioFormat.ENCODING_PCM_16BIT)
            ?: error("系统播放音频捕获无法初始化为 48 kHz Float32 或 PCM16。")
    }

    override fun start() = recorder.startRecording()

    override fun readStereoFrame(destination: FloatArray): Float {
        require(destination.size >= 960)
        if (usesFloat) readFully(recorder, floatStereo, 960) else readFully(recorder, shortStereo, 960)
        var peak = 0f
        repeat(960) { index ->
            val sample = if (usesFloat) {
                floatStereo[index].coerceIn(-1f, 1f)
            } else {
                pcm16ToFloat(shortStereo[index])
            }
            destination[index] = sample
            peak = maxOf(peak, kotlin.math.abs(sample))
        }
        return peak
    }

    override fun close() {
        runCatching { recorder.stop() }
        recorder.release()
    }
}

private fun createMicrophoneRecorder(encoding: Int): AudioRecord? = createRecorder {
    val channelMask = AudioFormat.CHANNEL_IN_MONO
    val bytesPerSample = if (encoding == AudioFormat.ENCODING_PCM_FLOAT) 4 else 2
    AudioRecord.Builder()
        .setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
        .setAudioFormat(audioFormat(encoding, channelMask))
        .setBufferSizeInBytes(maxOf(
            AudioRecord.getMinBufferSize(48_000, channelMask, encoding),
            480 * bytesPerSample * 12,
        ))
        .build()
}

private fun createPlaybackRecorder(
    configuration: AudioPlaybackCaptureConfiguration,
    encoding: Int,
): AudioRecord? = createRecorder {
    val channelMask = AudioFormat.CHANNEL_IN_STEREO
    val bytesPerSample = if (encoding == AudioFormat.ENCODING_PCM_FLOAT) 4 else 2
    AudioRecord.Builder()
        .setAudioPlaybackCaptureConfig(configuration)
        .setAudioFormat(audioFormat(encoding, channelMask))
        .setBufferSizeInBytes(maxOf(
            AudioRecord.getMinBufferSize(48_000, channelMask, encoding),
            960 * bytesPerSample * 12,
        ))
        .build()
}

private fun audioFormat(encoding: Int, channelMask: Int): AudioFormat =
    AudioFormat.Builder()
        .setSampleRate(48_000)
        .setEncoding(encoding)
        .setChannelMask(channelMask)
        .build()

private inline fun createRecorder(factory: () -> AudioRecord): AudioRecord? =
    runCatching { factory() }.getOrNull()?.let { candidate ->
        if (candidate.state == AudioRecord.STATE_INITIALIZED) {
            candidate
        } else {
            candidate.release()
            null
        }
    }

internal fun pcm16ToFloat(sample: Short): Float = sample.toInt() / 32768f

private fun readFully(recorder: AudioRecord, destination: FloatArray, length: Int) {
    var offset = 0
    while (offset < length) {
        val count = recorder.read(
            destination,
            offset,
            length - offset,
            AudioRecord.READ_BLOCKING,
        )
        check(count > 0) { "AudioRecord 读取失败：$count" }
        offset += count
    }
}

private fun readFully(recorder: AudioRecord, destination: ShortArray, length: Int) {
    var offset = 0
    while (offset < length) {
        val count = recorder.read(
            destination,
            offset,
            length - offset,
            AudioRecord.READ_BLOCKING,
        )
        check(count > 0) { "AudioRecord PCM16 读取失败：$count" }
        offset += count
    }
}
