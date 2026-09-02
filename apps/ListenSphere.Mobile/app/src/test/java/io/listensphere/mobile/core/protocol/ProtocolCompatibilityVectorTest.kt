package io.listensphere.mobile.core.protocol

import io.listensphere.protocol.v1.Envelope
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.MessageDigest
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.UUID

class ProtocolCompatibilityVectorTest {
    @Test
    fun deviceProofPayloadMatchesSharedDotNetGuidVector() {
        val vector = resourceText("device-proof-payload-v1.txt")
            .lineSequence()
            .filter(String::isNotBlank)
            .associate { line -> line.substringBefore('=') to line.substringAfter('=') }
        val deviceIdBytes = GuidWire.toDotNetBytes(UUID.fromString(vector.getValue("device_id")))
        val payload =
            hex(vector.getValue("context")) +
                hex(vector.getValue("controller_fingerprint")) +
                deviceIdBytes +
                hex(vector.getValue("client_nonce"))

        assertEquals(vector.getValue("device_id_dotnet"), deviceIdBytes.toHex())
        assertEquals(vector.getValue("payload"), payload.toHex())
        assertEquals(
            vector.getValue("sha256"),
            MessageDigest.getInstance("SHA-256").digest(payload).toHex(),
        )
        val certificate = CertificateFactory.getInstance("X.509")
            .generateCertificate(hex(vector.getValue("certificate_der")).inputStream()) as X509Certificate
        assertEquals(
            vector.getValue("certificate_fingerprint"),
            MessageDigest.getInstance("SHA-256").digest(certificate.encoded).toHex(),
        )
        val verifier = Signature.getInstance("SHA256withRSA").apply {
            initVerify(certificate.publicKey)
            update(payload)
        }
        assertTrue(verifier.verify(hex(vector.getValue("signature"))))
    }

    @Test
    fun helloEnvelopeMatchesSharedWireVectorAndRetainsUnknownField() {
        val expected = hex(resourceText("hello-envelope-v1.hex").trim())
        val envelope = Envelope.parseFrom(expected)

        assertEquals(1, envelope.version.major)
        assertEquals(1, envelope.version.minor)
        assertEquals(0x0102030405060708L, envelope.requestId)
        assertTrue(envelope.hasHelloRequest())
        assertEquals("android-default", envelope.helloRequest.sourceId)
        assertArrayEquals(expected, envelope.toByteArray())

        val withUnknown = expected + byteArrayOf(0x98.toByte(), 0x06, 0x01)
        val roundTripped = Envelope.parseFrom(withUnknown).toByteArray()
        assertArrayEquals(withUnknown, roundTripped)
    }

    private fun resourceText(name: String): String =
        checkNotNull(javaClass.classLoader?.getResourceAsStream(name)) {
            "Missing shared protocol vector: $name"
        }.bufferedReader().use { it.readText() }

    private fun hex(value: String): ByteArray {
        require(value.length % 2 == 0)
        return value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }

    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}
