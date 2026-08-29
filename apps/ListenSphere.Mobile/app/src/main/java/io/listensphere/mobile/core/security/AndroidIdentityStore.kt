package io.listensphere.mobile.core.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import io.listensphere.mobile.core.protocol.GuidWire
import io.listensphere.protocol.v1.DeviceIdentity
import com.google.protobuf.ByteString
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.security.cert.X509Certificate
import java.util.Calendar
import java.util.UUID
import javax.security.auth.x500.X500Principal

data class LocalIdentity(
    val deviceId: UUID,
    val alias: String,
    val certificate: X509Certificate,
    val privateKey: PrivateKey,
    val fingerprint: ByteArray,
) {
    fun toProtocol(displayName: String): DeviceIdentity = DeviceIdentity.newBuilder()
        .setDeviceId(ByteString.copyFrom(GuidWire.toDotNetBytes(deviceId)))
        .setDisplayName(displayName)
        .setPlatform("Android")
        .setCapabilities(2L) // DeviceCapabilities.AudioSend
        .setCertificateFingerprint(ByteString.copyFrom(fingerprint))
        .build()

    fun createProof(controllerFingerprint: ByteArray): DeviceIdentityProof {
        require(controllerFingerprint.size == 32)
        val nonce = ByteArray(32).also(SecureRandom()::nextBytes)
        val deviceIdBytes = GuidWire.toDotNetBytes(deviceId)
        val context = "ListenSphere-Device-Proof-v1\u0000".toByteArray(Charsets.US_ASCII)
        val payload = context + controllerFingerprint + deviceIdBytes + nonce
        val signature = Signature.getInstance("SHA256withECDSA").run {
            initSign(privateKey)
            update(payload)
            sign()
        }
        return DeviceIdentityProof(certificate.encoded, nonce, signature)
    }
}

data class DeviceIdentityProof(
    val certificate: ByteArray,
    val nonce: ByteArray,
    val signature: ByteArray,
)

class AndroidIdentityStore(private val context: Context) {
    companion object {
        private const val PREFERENCES = "listensphere_identity"
        private const val DEVICE_ID = "device_id"
        // Some vendor Keystore/Conscrypt combinations still fail RSA-PSS private-key
        // operations during TLS 1.3. P-256 ECDSA is mandatory for this v3 identity.
        private const val KEY_ALIAS = "ListenSphere-Mobile-Identity-v3-ec"
    }

    fun loadOrCreate(): LocalIdentity {
        val preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        val deviceId = preferences.getString(DEVICE_ID, null)?.let(UUID::fromString)
            ?: UUID.randomUUID().also {
                preferences.edit().putString(DEVICE_ID, it.toString()).apply()
            }
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        if (!keyStore.containsAlias(KEY_ALIAS)) {
            createKeyPair(deviceId)
        }
        val certificate = keyStore.getCertificate(KEY_ALIAS) as? X509Certificate
            ?: error("ListenSphere identity certificate is unavailable.")
        val privateKey = keyStore.getKey(KEY_ALIAS, null) as? PrivateKey
            ?: error("ListenSphere identity private key is unavailable.")
        return LocalIdentity(
            deviceId = deviceId,
            alias = KEY_ALIAS,
            certificate = certificate,
            privateKey = privateKey,
            fingerprint = MessageDigest.getInstance("SHA-256").digest(certificate.encoded),
        )
    }

    private fun createKeyPair(deviceId: UUID) {
        val now = Calendar.getInstance()
        val expires = Calendar.getInstance().apply { add(Calendar.YEAR, 5) }
        val serial = BigInteger(63, SecureRandom()).max(BigInteger.ONE)
        val specification = KeyGenParameterSpec.Builder(
            KEY_ALIAS,
            KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY,
        )
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA512)
            .setCertificateSubject(X500Principal("CN=ListenSphere-${deviceId.toString().replace("-", "")}"))
            .setCertificateSerialNumber(serial)
            .setCertificateNotBefore(now.time)
            .setCertificateNotAfter(expires.time)
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
            .apply { initialize(specification) }
            .generateKeyPair()
    }
}
