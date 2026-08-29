package io.listensphere.mobile.core.audio

import org.junit.Assert.assertEquals
import org.junit.Test

class BluetoothPcm16EncoderTest {
    @Test
    fun downmixesStereoAndWritesLittleEndianPcm16() {
        val stereo = FloatArray(BluetoothPcm16Encoder.INPUT_FLOATS)
        stereo[0] = 1f
        stereo[1] = 1f
        stereo[2] = -1f
        stereo[3] = -1f
        stereo[4] = 1f
        stereo[5] = -1f
        val pcm = ByteArray(BluetoothPcm16Encoder.OUTPUT_BYTES)

        BluetoothPcm16Encoder.encodeStereoToMono(stereo, pcm)

        assertEquals(0xff, pcm[0].toInt() and 0xff)
        assertEquals(0x7f, pcm[1].toInt() and 0xff)
        assertEquals(0x01, pcm[2].toInt() and 0xff)
        assertEquals(0x80, pcm[3].toInt() and 0xff)
        assertEquals(0x00, pcm[4].toInt() and 0xff)
        assertEquals(0x00, pcm[5].toInt() and 0xff)
    }

    @Test
    fun preservesLeftAndRightChannelsInStereoMode() {
        val stereo = FloatArray(BluetoothPcm16Encoder.INPUT_FLOATS)
        stereo[0] = 1f
        stereo[1] = -1f
        val pcm = ByteArray(BluetoothPcm16Encoder.outputBytes(2))

        BluetoothPcm16Encoder.encode(stereo, pcm, 2)

        assertEquals(0xff, pcm[0].toInt() and 0xff)
        assertEquals(0x7f, pcm[1].toInt() and 0xff)
        assertEquals(0x01, pcm[2].toInt() and 0xff)
        assertEquals(0x80, pcm[3].toInt() and 0xff)
    }
}
