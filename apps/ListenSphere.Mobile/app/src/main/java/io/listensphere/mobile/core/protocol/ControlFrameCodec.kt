package io.listensphere.mobile.core.protocol

import io.listensphere.protocol.v1.Envelope
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

object ControlFrameCodec {
    const val MAXIMUM_MESSAGE_LENGTH = 1024 * 1024

    @Synchronized
    fun write(output: OutputStream, envelope: Envelope) {
        val payload = envelope.toByteArray()
        require(payload.isNotEmpty() && payload.size <= MAXIMUM_MESSAGE_LENGTH) {
            "Control message length ${payload.size} is outside the allowed range."
        }
        DataOutputStream(output).apply {
            writeInt(payload.size)
            write(payload)
            flush()
        }
    }

    fun read(input: InputStream): Envelope? {
        val data = DataInputStream(input)
        val length = try {
            data.readInt()
        } catch (_: EOFException) {
            return null
        }
        require(length in 1..MAXIMUM_MESSAGE_LENGTH) {
            "Control message length $length is outside the allowed range."
        }
        val payload = ByteArray(length)
        data.readFully(payload)
        return Envelope.parseFrom(payload)
    }
}
