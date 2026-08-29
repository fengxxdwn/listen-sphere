package io.listensphere.mobile.core.network

import io.listensphere.mobile.core.protocol.GuidWire
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.util.UUID

class BluetoothProbeResponseTest {
    @Test
    fun `extended response includes stable controller id`() {
        val id = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val result = readBluetoothProbeResponse(response(flags = 0x02, controllerId = id))

        assertEquals(id, result.controllerId)
        assertEquals("FENG Controller", result.controllerName)
        assertEquals(0x07, result.capabilities.codecMask)
        assertEquals(0x03, result.capabilities.channelMask)
    }

    @Test
    fun `legacy response remains readable without controller id`() {
        val result = readBluetoothProbeResponse(response(flags = 0x00, controllerId = null))

        assertNull(result.controllerId)
        assertEquals("FENG Controller", result.controllerName)
    }

    private fun response(flags: Int, controllerId: UUID?): DataInputStream {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            output.write(BluetoothHandshakeCodec.magic)
            output.writeByte(1)
            output.writeByte(flags)
            val name = "FENG Controller".toByteArray()
            output.writeShort(name.size)
            output.write(name)
            output.writeShort(0)
            output.writeInt(0x07)
            output.writeByte(0x03)
            controllerId?.let { output.write(GuidWire.toDotNetBytes(it)) }
        }
        return DataInputStream(ByteArrayInputStream(bytes.toByteArray()))
    }
}
