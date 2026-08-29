package io.listensphere.mobile.core.protocol

import io.listensphere.protocol.v1.Envelope
import io.listensphere.protocol.v1.Heartbeat
import io.listensphere.protocol.v1.ProtocolVersion
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

class ControlFrameCodecTest {
    @Test
    fun frameUsesBigEndianLengthAndRoundTripsUnknownFields() {
        val envelope = Envelope.newBuilder()
            .setVersion(ProtocolVersion.newBuilder().setMajor(1).setMinor(0))
            .setRequestId(42)
            .setHeartbeat(Heartbeat.newBuilder().setMonotonicMilliseconds(1234))
            .build()
        val output = ByteArrayOutputStream()

        ControlFrameCodec.write(output, envelope)

        val framed = output.toByteArray()
        val encodedLength = ByteBuffer.wrap(framed, 0, 4)
            .order(ByteOrder.BIG_ENDIAN)
            .int
        assertEquals(envelope.serializedSize, encodedLength)
        val decoded = ControlFrameCodec.read(ByteArrayInputStream(framed))
        assertEquals(42L, decoded?.requestId)
        assertTrue(decoded?.hasHeartbeat() == true)
        assertEquals(1234L, decoded?.heartbeat?.monotonicMilliseconds)
    }
}
