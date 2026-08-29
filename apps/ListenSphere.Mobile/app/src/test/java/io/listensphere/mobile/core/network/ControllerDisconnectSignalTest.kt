package io.listensphere.mobile.core.network

import io.listensphere.mobile.core.protocol.ControlFrameCodec
import io.listensphere.protocol.v1.Disconnect
import io.listensphere.protocol.v1.Envelope
import io.listensphere.protocol.v1.ProtocolVersion
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ControllerDisconnectSignalTest {
    @Test
    fun framedDisconnectIsClassifiedAsIntentional() {
        val bytes = ByteArrayOutputStream()
        ControlFrameCodec.write(
            bytes,
            Envelope.newBuilder()
                .setVersion(ProtocolVersion.newBuilder().setMajor(1).setMinor(0))
                .setDisconnect(Disconnect.newBuilder().setReason("用户从主控端断开"))
                .build(),
        )

        val error = assertThrows(ControllerDisconnectedException::class.java) {
            ControllerDisconnectSignal.await(
                DataInputStream(ByteArrayInputStream(bytes.toByteArray())),
            )
        }
        assertEquals("用户从主控端断开", error.message)
    }
}
