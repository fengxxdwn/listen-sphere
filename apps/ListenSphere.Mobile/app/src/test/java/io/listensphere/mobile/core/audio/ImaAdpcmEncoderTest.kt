package io.listensphere.mobile.core.audio

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ImaAdpcmEncoderTest {
    @Test
    fun encodesIndependentFixedSizeFrame() {
        val pcm = ByteArray(ImaAdpcmEncoder.SAMPLES_PER_FRAME * 2)
        for (sample in 0 until ImaAdpcmEncoder.SAMPLES_PER_FRAME) {
            val value = (12_000f * kotlin.math.sin(2f * Math.PI.toFloat() * 440f * sample / 48_000f)).toInt()
            pcm[sample * 2] = value.toByte()
            pcm[sample * 2 + 1] = (value shr 8).toByte()
        }
        val encoded = ByteArray(ImaAdpcmEncoder.ENCODED_BYTES_PER_FRAME)

        ImaAdpcmEncoder.encode(pcm, encoded)

        assertEquals(244, encoded.size)
        assertEquals(pcm[0], encoded[0])
        assertEquals(pcm[1], encoded[1])
        assertEquals(0, encoded[2].toInt())
        assertTrue(encoded.drop(4).any { it.toInt() != 0 })
    }

    @Test
    fun encodesStereoAsTwoIndependentChannelBlocks() {
        val pcm = ByteArray(ImaAdpcmEncoder.SAMPLES_PER_FRAME * 2 * 2)
        pcm[0] = 0x34
        pcm[1] = 0x12
        pcm[2] = 0x78
        pcm[3] = 0x56
        val encoded = ByteArray(ImaAdpcmEncoder.encodedBytes(2))

        ImaAdpcmEncoder.encode(pcm, encoded, 2)

        assertEquals(488, encoded.size)
        assertEquals(0x34, encoded[0].toInt() and 0xff)
        assertEquals(0x12, encoded[1].toInt() and 0xff)
        assertEquals(0x78, encoded[244].toInt() and 0xff)
        assertEquals(0x56, encoded[245].toInt() and 0xff)
    }
}
