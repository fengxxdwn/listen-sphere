package io.listensphere.mobile.core.protocol

import io.listensphere.mobile.core.model.AudioSession
import java.nio.ByteBuffer
import java.nio.ByteOrder
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class AudioDatagramEncoder(private val session: AudioSession) {
    companion object {
        const val HEADER_SIZE = 60
        const val MAXIMUM_DATAGRAM_SIZE = 1200
        const val TAG_SIZE = 16
        const val MAXIMUM_PLAINTEXT_SIZE = MAXIMUM_DATAGRAM_SIZE - HEADER_SIZE - TAG_SIZE
    }

    private var packetSequence = 0L
    private var frameSequence = 0L

    init {
        require(session.key.size == 32) { "Protocol v1 requires an AES-256 session key." }
        require(session.salt.size == 4) { "Protocol v1 requires a four-byte session salt." }
        require(session.sampleRate == 48_000 && session.channelCount == 2 && session.frameSamples == 480) {
            "Protocol v1 requires 48 kHz Float32 stereo frames of 10 ms."
        }
    }

    fun encodeFrame(pcm: ByteArray, timestamp: Long): List<ByteArray> {
        require(pcm.size == session.frameSamples * session.channelCount * Float.SIZE_BYTES) {
            "A Protocol v1 frame must contain exactly 3840 bytes."
        }
        val count = (pcm.size + MAXIMUM_PLAINTEXT_SIZE - 1) / MAXIMUM_PLAINTEXT_SIZE
        check(packetSequence + count - 1 <= 0xffff_ffffL) {
            "UDP packet sequence exhausted; a new session key is required."
        }

        val output = ArrayList<ByteArray>(count)
        var offset = 0
        repeat(count) { fragmentIndex ->
            val length = minOf(MAXIMUM_PLAINTEXT_SIZE, pcm.size - offset)
            val sequence = packetSequence + fragmentIndex
            val header = createHeader(
                payloadLength = length + TAG_SIZE,
                fragmentIndex = fragmentIndex,
                fragmentCount = count,
                packetSequence = sequence,
                frameSequence = frameSequence,
                timestamp = timestamp,
            )
            val nonce = ByteBuffer.allocate(12)
                .order(ByteOrder.LITTLE_ENDIAN)
                .put(session.salt)
                .putInt(session.streamId.toInt())
                .putInt(sequence.toInt())
                .array()
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(
                Cipher.ENCRYPT_MODE,
                SecretKeySpec(session.key, "AES"),
                GCMParameterSpec(TAG_SIZE * 8, nonce),
            )
            cipher.updateAAD(header)
            val encrypted = cipher.doFinal(pcm, offset, length)
            output += header + encrypted
            offset += length
        }

        packetSequence += count
        frameSequence = (frameSequence + 1) and 0xffff_ffffL
        return output
    }

    private fun createHeader(
        payloadLength: Int,
        fragmentIndex: Int,
        fragmentCount: Int,
        packetSequence: Long,
        frameSequence: Long,
        timestamp: Long,
    ): ByteArray = ByteBuffer.allocate(HEADER_SIZE)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put(byteArrayOf('L'.code.toByte(), 'S'.code.toByte(), 'P'.code.toByte(), 'A'.code.toByte()))
        .put(1.toByte())
        .put(0.toByte())
        .putShort(1.toShort()) // Encrypted
        .putShort(HEADER_SIZE.toShort())
        .put(1.toByte()) // PCM Float32
        .put(1.toByte()) // Float32 little endian
        .put(session.channelCount.toByte())
        .put(0.toByte()) // reserved
        .putInt(session.sampleRate)
        .putShort(session.frameSamples.toShort())
        .put(fragmentIndex.toByte())
        .put(fragmentCount.toByte())
        .put(GuidWire.toNetworkBytes(session.sessionId))
        .putInt(session.streamId.toInt())
        .putInt(packetSequence.toInt())
        .putInt(frameSequence.toInt())
        .putLong(timestamp)
        .putShort(payloadLength.toShort())
        .array()
}
