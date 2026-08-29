package io.listensphere.mobile.core.model

import android.content.Context
import java.util.UUID

data class MobilePreferenceSnapshot(
    val transportMode: TransportMode = TransportMode.WIFI,
    val captureKind: CaptureKind = CaptureKind.DEVICE_PLAYBACK,
    val selectedControllerKey: String? = null,
    val selectedBluetoothKey: String? = null,
    val manualMode: Boolean = false,
    val manualHost: String = "",
    val manualPort: String = "",
    val usbCompatibilityMode: Boolean = false,
    val bluetoothCodec: BluetoothStreamCodec = BluetoothStreamCodec.IMA_ADPCM,
    val bluetoothChannelMode: BluetoothChannelMode = BluetoothChannelMode.STEREO,
    val bluetoothControllerIds: Map<String, UUID> = emptyMap(),
)

class MobilePreferences(context: Context) {
    companion object {
        internal const val CURRENT_SCHEMA_VERSION = 2
        private const val FILE_NAME = "listensphere-mobile"
    }

    private val preferences = context.getSharedPreferences(FILE_NAME, Context.MODE_PRIVATE)

    fun load(): MobilePreferenceSnapshot {
        val schemaVersion = preferences.getInt("schema_version", 0)
        if (schemaVersion > CURRENT_SCHEMA_VERSION) {
            return MobilePreferenceSnapshot()
        }

        val snapshot = MobilePreferenceSnapshot(
            transportMode = enumOrDefault(
                preferences.getString("transport_mode", null),
                TransportMode.WIFI,
            ),
            captureKind = enumOrDefault(
                preferences.getString("capture_kind", null),
                CaptureKind.DEVICE_PLAYBACK,
            ),
            selectedControllerKey = preferences.getString("selected_controller_key", null),
            selectedBluetoothKey = preferences.getString("selected_bluetooth_key", null),
            manualMode = preferences.getBoolean("manual_mode", false),
            manualHost = preferences.getString("manual_host", "").orEmpty(),
            manualPort = preferences.getString("manual_port", "").orEmpty()
                .filter(Char::isDigit)
                .take(5),
            usbCompatibilityMode = preferences.getBoolean("usb_compatibility_mode", false),
            bluetoothCodec = enumOrDefault(
                preferences.getString("bluetooth_codec", null),
                BluetoothStreamCodec.IMA_ADPCM,
            ),
            bluetoothChannelMode = enumOrDefault(
                preferences.getString("bluetooth_channel_mode", null),
                BluetoothChannelMode.STEREO,
            ),
            bluetoothControllerIds = preferences
                .getStringSet("bluetooth_controller_ids", emptySet())
                .orEmpty()
                .mapNotNull(::parseBluetoothControllerAssociation)
                .toMap(),
        )
        if (schemaVersion < CURRENT_SCHEMA_VERSION) {
            preferences.edit().putInt("schema_version", CURRENT_SCHEMA_VERSION).apply()
        }
        return snapshot
    }

    fun save(snapshot: MobilePreferenceSnapshot) {
        preferences.edit()
            .putInt("schema_version", CURRENT_SCHEMA_VERSION)
            .putString("transport_mode", snapshot.transportMode.name)
            .putString("capture_kind", snapshot.captureKind.name)
            .putString("selected_controller_key", snapshot.selectedControllerKey)
            .putString("selected_bluetooth_key", snapshot.selectedBluetoothKey)
            .putBoolean("manual_mode", snapshot.manualMode)
            .putString("manual_host", snapshot.manualHost.trim())
            .putString("manual_port", snapshot.manualPort.filter(Char::isDigit).take(5))
            .putBoolean("usb_compatibility_mode", snapshot.usbCompatibilityMode)
            .putString("bluetooth_codec", snapshot.bluetoothCodec.name)
            .putString("bluetooth_channel_mode", snapshot.bluetoothChannelMode.name)
            .putStringSet(
                "bluetooth_controller_ids",
                snapshot.bluetoothControllerIds.mapTo(mutableSetOf()) { (key, id) ->
                    "${key.uppercase()}|$id"
                },
            )
            .apply()
    }

    fun loadRecentConnections(): List<RecentConnection> = TransportMode.entries
        .mapNotNull { mode ->
            val name = preferences.getString("recent_name_${mode.name}", null)
                ?.takeIf(String::isNotBlank) ?: return@mapNotNull null
            val connectedAt = preferences.getLong("recent_at_${mode.name}", 0L)
            if (connectedAt <= 0L) return@mapNotNull null
            RecentConnection(mode, name, connectedAt)
        }
        .sortedByDescending(RecentConnection::connectedAtEpochMilliseconds)

    fun recordSuccessfulConnection(mode: TransportMode, targetName: String) {
        val safeName = targetName.trim().ifBlank { "ListenSphere Controller" }.take(120)
        preferences.edit()
            .putString("recent_name_${mode.name}", safeName)
            .putLong("recent_at_${mode.name}", System.currentTimeMillis())
            .putString("last_successful_transport", mode.name)
            .apply()
    }
}

internal fun parseBluetoothControllerAssociation(value: String): Pair<String, UUID>? {
    val separator = value.indexOf('|')
    if (separator <= 0 || separator == value.lastIndex) return null
    val key = value.substring(0, separator).trim().uppercase()
    val controllerId = runCatching { UUID.fromString(value.substring(separator + 1)) }.getOrNull()
        ?: return null
    return key to controllerId
}

internal inline fun <reified T : Enum<T>> enumOrDefault(value: String?, fallback: T): T =
    enumValues<T>().firstOrNull { it.name == value } ?: fallback
