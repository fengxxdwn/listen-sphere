package io.listensphere.mobile.core.audio

import org.junit.Assert.assertEquals
import org.junit.Test

class AacLcEncoderTest {
    @Test
    fun adtsHeaderCarriesPacketLengthRateAndStereoConfiguration() {
        val header = ByteArray(7)
        AacLcEncoder.writeAdtsHeader(header, 321, 2)

        assertEquals(0xff, header[0].toInt() and 0xff)
        assertEquals(0xf1, header[1].toInt() and 0xff)
        assertEquals(3, (header[2].toInt() ushr 2) and 0x0f)
        assertEquals(2, ((header[2].toInt() and 1) shl 2) or
            ((header[3].toInt() ushr 6) and 3))
        val length = ((header[3].toInt() and 3) shl 11) or
            ((header[4].toInt() and 0xff) shl 3) or
            ((header[5].toInt() ushr 5) and 7)
        assertEquals(321, length)
    }
}
