package io.listensphere.mobile.core.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

/** Converts UUIDs to the byte layout used by System.Guid.ToByteArray(). */
object GuidWire {
    fun toDotNetBytes(uuid: UUID): ByteArray = toNetworkBytes(uuid).also(::swapGuidFields)

    fun fromDotNetBytes(bytes: ByteArray): UUID {
        require(bytes.size == 16) { "A ListenSphere UUID must contain 16 bytes." }
        return fromNetworkBytes(bytes.copyOf().also(::swapGuidFields))
    }

    fun toNetworkBytes(uuid: UUID): ByteArray =
        ByteBuffer.allocate(16)
            .order(ByteOrder.BIG_ENDIAN)
            .putLong(uuid.mostSignificantBits)
            .putLong(uuid.leastSignificantBits)
            .array()

    fun fromNetworkBytes(bytes: ByteArray): UUID {
        require(bytes.size == 16) { "A UUID must contain 16 bytes." }
        val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        return UUID(buffer.long, buffer.long)
    }

    private fun swapGuidFields(bytes: ByteArray) {
        bytes.swap(0, 3)
        bytes.swap(1, 2)
        bytes.swap(4, 5)
        bytes.swap(6, 7)
    }

    private fun ByteArray.swap(first: Int, second: Int) {
        val value = this[first]
        this[first] = this[second]
        this[second] = value
    }
}
