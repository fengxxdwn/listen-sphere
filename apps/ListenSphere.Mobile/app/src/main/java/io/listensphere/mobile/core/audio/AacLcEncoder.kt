package io.listensphere.mobile.core.audio

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import java.io.Closeable

/** Encodes 48 kHz PCM16 into one ADTS-wrapped AAC-LC access unit per 1024 samples. */
class AacLcEncoder(private val channelCount: Int) : Closeable {
    private val codec = MediaCodec.createEncoderByType(MIME_TYPE)
    private val accumulator = ByteArray(SAMPLES_PER_ACCESS_UNIT * channelCount * 2)
    private var accumulatedBytes = 0
    private var presentationSamples = 0L

    init {
        require(channelCount in 1..2)
        val format = MediaFormat.createAudioFormat(MIME_TYPE, SAMPLE_RATE, channelCount).apply {
            setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            setInteger(MediaFormat.KEY_BIT_RATE, if (channelCount == 2) 160_000 else 96_000)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, accumulator.size)
        }
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
    }

    /**
     * Adds an interleaved PCM16 chunk. A 480-sample capture chunk produces zero or one AAC unit.
     */
    fun offer(pcm: ByteArray): ByteArray? {
        require(pcm.size % (channelCount * 2) == 0)
        var sourceOffset = 0
        var encoded: ByteArray? = null
        while (sourceOffset < pcm.size) {
            val copied = minOf(accumulator.size - accumulatedBytes, pcm.size - sourceOffset)
            pcm.copyInto(accumulator, accumulatedBytes, sourceOffset, sourceOffset + copied)
            accumulatedBytes += copied
            sourceOffset += copied
            if (accumulatedBytes == accumulator.size) {
                check(encoded == null) { "单次采集块包含了多个 AAC 访问单元。" }
                encoded = encodeAccessUnit(accumulator)
                accumulatedBytes = 0
            }
        }
        return encoded
    }

    private fun encodeAccessUnit(pcm: ByteArray): ByteArray? {
        val inputIndex = codec.dequeueInputBuffer(INPUT_TIMEOUT_US)
        check(inputIndex >= 0) { "AAC 编码器没有可用输入缓冲区。" }
        val input = requireNotNull(codec.getInputBuffer(inputIndex)).apply { clear() }
        input.put(pcm)
        val presentationUs = presentationSamples * 1_000_000L / SAMPLE_RATE
        presentationSamples += SAMPLES_PER_ACCESS_UNIT
        codec.queueInputBuffer(inputIndex, 0, pcm.size, presentationUs, 0)

        val info = MediaCodec.BufferInfo()
        repeat(MAX_OUTPUT_POLLS) {
            val outputIndex = codec.dequeueOutputBuffer(info, OUTPUT_TIMEOUT_US)
            when {
                outputIndex >= 0 -> {
                    try {
                        if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) return@repeat
                        check(info.size > 0) { "AAC 编码器返回了空访问单元。" }
                        val output = requireNotNull(codec.getOutputBuffer(outputIndex)).duplicate().apply {
                            position(info.offset)
                            limit(info.offset + info.size)
                        }
                        val packet = ByteArray(ADTS_HEADER_BYTES + info.size)
                        writeAdtsHeader(packet, packet.size, channelCount)
                        output.get(packet, ADTS_HEADER_BYTES, info.size)
                        return packet
                    } finally {
                        codec.releaseOutputBuffer(outputIndex, false)
                    }
                }
                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> Unit
            }
        }
        // Hardware encoders may retain one access unit internally. The next
        // input submission drains it, so no immediate output is not an error.
        return null
    }

    override fun close() {
        runCatching { codec.stop() }
        codec.release()
    }

    companion object {
        const val SAMPLE_RATE = 48_000
        const val SAMPLES_PER_ACCESS_UNIT = 1024
        private const val MIME_TYPE = MediaFormat.MIMETYPE_AUDIO_AAC
        private const val ADTS_HEADER_BYTES = 7
        private const val INPUT_TIMEOUT_US = 20_000L
        private const val OUTPUT_TIMEOUT_US = 5_000L
        private const val MAX_OUTPUT_POLLS = 8

        internal fun writeAdtsHeader(destination: ByteArray, packetLength: Int, channels: Int) {
            require(destination.size >= ADTS_HEADER_BYTES)
            require(packetLength in ADTS_HEADER_BYTES..0x1fff)
            require(channels in 1..2)
            val frequencyIndex = 3 // 48 kHz
            val profile = 1 // AAC LC object type (2) minus one
            destination[0] = 0xff.toByte()
            destination[1] = 0xf1.toByte() // MPEG-4, no CRC
            destination[2] = ((profile shl 6) or (frequencyIndex shl 2) or (channels ushr 2)).toByte()
            destination[3] = (((channels and 3) shl 6) or (packetLength ushr 11)).toByte()
            destination[4] = ((packetLength ushr 3) and 0xff).toByte()
            destination[5] = (((packetLength and 7) shl 5) or 0x1f).toByte()
            destination[6] = 0xfc.toByte()
        }
    }
}
