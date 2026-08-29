package io.listensphere.mobile.core.audio

import io.listensphere.mobile.core.model.AudioSession
import io.listensphere.mobile.core.protocol.AudioDatagramEncoder
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder

data class UdpSenderStatistics(
    val framesSent: Long,
    val datagramsSent: Long,
    val bytesSent: Long,
)

class UdpAudioSender(session: AudioSession) : AutoCloseable {
    private val encoder = AudioDatagramEncoder(session)
    private val socket = DatagramSocket().apply {
        connect(InetSocketAddress(session.controllerAddress, session.udpPort))
        sendBufferSize = maxOf(sendBufferSize, 256 * 1024)
    }
    private var framesSent = 0L
    private var datagramsSent = 0L
    private var bytesSent = 0L

    val statistics: UdpSenderStatistics
        get() = UdpSenderStatistics(framesSent, datagramsSent, bytesSent)

    fun sendFrame(samples: FloatArray, timestamp: Long) {
        require(samples.size == 960)
        val pcm = ByteBuffer.allocate(960 * Float.SIZE_BYTES)
            .order(ByteOrder.LITTLE_ENDIAN)
            .apply { samples.forEach(::putFloat) }
            .array()
        encoder.encodeFrame(pcm, timestamp).forEach { datagram ->
            socket.send(DatagramPacket(datagram, datagram.size))
            datagramsSent++
            bytesSent += datagram.size
        }
        framesSent++
    }

    override fun close() = socket.close()
}
