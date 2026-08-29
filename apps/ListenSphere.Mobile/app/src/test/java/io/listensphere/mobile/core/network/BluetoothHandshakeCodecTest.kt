package io.listensphere.mobile.core.network

import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import org.junit.Assert.assertArrayEquals
import org.junit.Test

class BluetoothHandshakeCodecTest {
    @Test
    fun `stream request writes version before mode`() {
        val bytes = ByteArrayOutputStream().use { buffer ->
            DataOutputStream(buffer).use { output ->
                BluetoothHandshakeCodec.writeRequest(
                    output,
                    BluetoothHandshakeCodec.MODE_STREAM_WITH_FORMAT_REQUEST,
                )
            }
            buffer.toByteArray()
        }

        assertArrayEquals(
            byteArrayOf('L'.code.toByte(), 'S'.code.toByte(), 'P'.code.toByte(), 'B'.code.toByte(), 1, 3),
            bytes,
        )
    }
}
