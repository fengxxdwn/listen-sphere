package io.listensphere.mobile.core.usb

import java.io.ByteArrayInputStream
import java.io.DataInputStream
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class UsbTransportFrameTest {
    @Test
    fun frameRoundTripsWithBigEndianHeader() {
        val encoded = UsbTransportFrameCodec.encode(
            UsbFrame(
                kind = UsbFrameKind.AUDIO,
                flags = 0x1020,
                sequence = 0xfedc_ba98L,
                timestamp = 0x0102_0304_0506_0708L,
                payload = byteArrayOf(1, 2, 3),
            ),
        )

        assertEquals(UsbTransportFrameCodec.HEADER_LENGTH + 3, encoded.size)
        assertArrayEquals(byteArrayOf(0x4c, 0x53, 0x55, 0x42), encoded.copyOfRange(0, 4))
        assertEquals(3, encoded[11].toInt())

        val decoded = UsbTransportFrameCodec.read(DataInputStream(ByteArrayInputStream(encoded)))
        assertEquals(UsbFrameKind.AUDIO, decoded.kind)
        assertEquals(0x1020, decoded.flags)
        assertEquals(0xfedc_ba98L, decoded.sequence)
        assertEquals(0x0102_0304_0506_0708L, decoded.timestamp)
        assertArrayEquals(byteArrayOf(1, 2, 3), decoded.payload)
    }
}
