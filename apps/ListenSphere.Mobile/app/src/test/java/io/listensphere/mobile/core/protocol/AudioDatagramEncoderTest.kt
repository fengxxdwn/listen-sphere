package io.listensphere.mobile.core.protocol

import io.listensphere.mobile.core.model.AudioSession
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class AudioDatagramEncoderTest {
    @Test
    fun frameMatchesProtocolV1HeaderFragmentationAndAead() {
        val session = AudioSession(
            sessionId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff"),
            streamId = 0x12345678,
            key = ByteArray(32) { it.toByte() },
            salt = byteArrayOf(1, 2, 3, 4),
            controllerAddress = InetAddress.getLoopbackAddress(),
            udpPort = 50000,
            sampleRate = 48_000,
            channelCount = 2,
            frameSamples = 480,
        )
        val pcm = ByteArray(3840) { (it % 251).toByte() }

        val datagrams = AudioDatagramEncoder(session).encodeFrame(pcm, timestamp = 960)

        assertEquals(4, datagrams.size)
        assertEquals(listOf(1200, 1200, 1200, 544), datagrams.map(ByteArray::size))
        datagrams.forEachIndexed { index, datagram ->
            val header = ByteBuffer.wrap(datagram, 0, 60).order(ByteOrder.LITTLE_ENDIAN)
            assertArrayEquals("LSPA".toByteArray(), datagram.copyOfRange(0, 4))
            assertEquals(1, header.get(4).toInt())
            assertEquals(1, header.getShort(6).toInt())
            assertEquals(60, header.getShort(8).toInt())
            assertEquals(index, header.get(20).toInt())
            assertEquals(4, header.get(21).toInt())
            assertArrayEquals(GuidWire.toNetworkBytes(session.sessionId), datagram.copyOfRange(22, 38))
            assertEquals(session.streamId, header.getInt(38).toLong() and 0xffff_ffffL)
            assertEquals(index.toLong(), header.getInt(42).toLong() and 0xffff_ffffL)
            assertEquals(0, header.getInt(46))
            assertEquals(960, header.getLong(50))
            assertEquals(datagram.size - 60, header.getShort(58).toInt())
        }

        val first = datagrams.first()
        val nonce = ByteBuffer.allocate(12)
            .order(ByteOrder.LITTLE_ENDIAN)
            .put(session.salt)
            .putInt(session.streamId.toInt())
            .putInt(0)
            .array()
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(
            Cipher.DECRYPT_MODE,
            SecretKeySpec(session.key, "AES"),
            GCMParameterSpec(128, nonce),
        )
        cipher.updateAAD(first, 0, 60)
        val plaintext = cipher.doFinal(first, 60, first.size - 60)
        assertEquals(1124, plaintext.size)
        assertTrue(plaintext.contentEquals(pcm.copyOfRange(0, 1124)))
    }
}
