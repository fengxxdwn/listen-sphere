package io.listensphere.mobile.core.usb

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException

enum class UsbFrameKind(val wireId: Int) {
    CONTROL(1),
    AUDIO(2),
    STATUS(3),
    KEEP_ALIVE(4);

    companion object {
        fun fromWireId(value: Int): UsbFrameKind? = entries.firstOrNull { it.wireId == value }
    }
}

data class UsbFrame(
    val kind: UsbFrameKind,
    val flags: Int,
    val sequence: Long,
    val timestamp: Long,
    val payload: ByteArray,
)

object UsbTransportFrameCodec {
    const val HEADER_LENGTH = 24
    const val PROTOCOL_VERSION = 1
    const val MAXIMUM_PAYLOAD_LENGTH = 64 * 1024
    private val magic = byteArrayOf('L'.code.toByte(), 'S'.code.toByte(), 'U'.code.toByte(), 'B'.code.toByte())

    fun encode(frame: UsbFrame): ByteArray {
        require(frame.flags in 0..0xffff)
        require(frame.sequence in 0..0xffff_ffffL)
        require(frame.payload.size <= MAXIMUM_PAYLOAD_LENGTH)
        return java.io.ByteArrayOutputStream(HEADER_LENGTH + frame.payload.size).use { bytes ->
            DataOutputStream(bytes).use { output ->
                output.write(magic)
                output.writeByte(PROTOCOL_VERSION)
                output.writeByte(frame.kind.wireId)
                output.writeShort(frame.flags)
                output.writeInt(frame.payload.size)
                output.writeInt(frame.sequence.toInt())
                output.writeLong(frame.timestamp)
                output.write(frame.payload)
            }
            bytes.toByteArray()
        }
    }

    fun read(input: DataInputStream): UsbFrame {
        val headerMagic = ByteArray(4).also(input::readFully)
        if (!headerMagic.contentEquals(magic)) throw IOException("USB 帧 Magic 无效。")
        val version = input.readUnsignedByte()
        if (version != PROTOCOL_VERSION) throw IOException("USB 帧版本不兼容：$version。")
        val kind = UsbFrameKind.fromWireId(input.readUnsignedByte())
            ?: throw IOException("USB 帧类型无效。")
        val flags = input.readUnsignedShort()
        val payloadLength = input.readInt()
        if (payloadLength !in 0..MAXIMUM_PAYLOAD_LENGTH) {
            throw IOException("USB 帧负载长度无效：$payloadLength。")
        }
        val sequence = input.readInt().toLong() and 0xffff_ffffL
        val timestamp = input.readLong()
        val payload = ByteArray(payloadLength).also(input::readFully)
        return UsbFrame(kind, flags, sequence, timestamp, payload)
    }

    fun write(output: DataOutputStream, frame: UsbFrame) {
        output.write(encode(frame))
        output.flush()
    }
}
