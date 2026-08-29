package io.listensphere.mobile.core.security

import android.content.Context
import android.util.Base64
import java.security.MessageDigest
import java.util.UUID

class TrustedControllerStore(context: Context) {
    private val preferences = context.getSharedPreferences(
        "listensphere_trusted_controllers",
        Context.MODE_PRIVATE,
    )

    fun fingerprint(deviceId: UUID): ByteArray? = preferences
        .getString(deviceId.toString(), null)
        ?.let { Base64.decode(it, Base64.NO_WRAP) }

    fun trust(deviceId: UUID, fingerprint: ByteArray) {
        preferences.edit()
            .putString(deviceId.toString(), Base64.encodeToString(fingerprint, Base64.NO_WRAP))
            .apply()
    }

    fun remove(deviceId: UUID) {
        preferences.edit().remove(deviceId.toString()).apply()
    }

    fun matches(deviceId: UUID, fingerprint: ByteArray): Boolean =
        this.fingerprint(deviceId)?.let { MessageDigest.isEqual(it, fingerprint) } == true
}
