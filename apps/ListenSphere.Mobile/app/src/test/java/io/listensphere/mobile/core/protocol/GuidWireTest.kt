package io.listensphere.mobile.core.protocol

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.util.UUID

class GuidWireTest {
    @Test
    fun dotNetGuidLayoutMatchesSystemGuidByteArray() {
        val uuid = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val expected = byteArrayOf(
            0x33, 0x22, 0x11, 0x00,
            0x55, 0x44,
            0x77, 0x66,
            0x88.toByte(), 0x99.toByte(), 0xaa.toByte(), 0xbb.toByte(),
            0xcc.toByte(), 0xdd.toByte(), 0xee.toByte(), 0xff.toByte(),
        )

        assertArrayEquals(expected, GuidWire.toDotNetBytes(uuid))
        assertEquals(uuid, GuidWire.fromDotNetBytes(expected))
    }
}
