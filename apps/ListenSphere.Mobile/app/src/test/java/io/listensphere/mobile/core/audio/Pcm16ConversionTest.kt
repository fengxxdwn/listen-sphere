package io.listensphere.mobile.core.audio

import org.junit.Assert.assertEquals
import org.junit.Test

class Pcm16ConversionTest {
    @Test
    fun convertsFullPcm16RangeToFloat() {
        assertEquals(-1f, pcm16ToFloat(Short.MIN_VALUE), 0f)
        assertEquals(0f, pcm16ToFloat(0), 0f)
        assertEquals(32767f / 32768f, pcm16ToFloat(Short.MAX_VALUE), 0f)
    }
}
