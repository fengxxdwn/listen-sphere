package io.listensphere.mobile.core.model

import android.content.SharedPreferences
import org.junit.Assert.*
import org.junit.Test
import java.lang.reflect.Proxy
import java.util.UUID

/** Exercises the production loader/editor without an Android process or real user settings. */
class MobilePreferencesStorageTest {
    @Test fun `legacy preferences migrate without losing capture and connection choices`() {
        for (version in 0..1) {
            val values = mutableMapOf<String, Any?>(
                "schema_version" to version,
                "transport_mode" to "USB",
                "capture_kind" to "MICROPHONE",
                "manual_host" to "192.168.1.8",
                "manual_port" to "52x1429",
                "usb_compatibility_mode" to true,
            )
            val preferences = MobilePreferences(memoryPreferences(values))
            val snapshot = preferences.load()
            assertEquals(TransportMode.USB, snapshot.transportMode)
            assertEquals(CaptureKind.MICROPHONE, snapshot.captureKind)
            assertEquals("52142", snapshot.manualPort)
            assertEquals("192.168.1.8", snapshot.manualHost)
            assertTrue(snapshot.usbCompatibilityMode)
            assertEquals(2, values["schema_version"])
            assertEquals(snapshot, preferences.load())
        }
    }

    @Test fun `all snapshot fields survive save load and service recreation`() {
        val values = mutableMapOf<String, Any?>()
        val expected = MobilePreferenceSnapshot(
            TransportMode.BLUETOOTH, CaptureKind.MICROPHONE, "controller-key", "AA:BB:CC:DD:EE:FF",
            true, "192.168.1.10", "51493", true, BluetoothStreamCodec.PCM16,
            BluetoothChannelMode.MONO, mapOf("AA:BB:CC:DD:EE:FF" to UUID.randomUUID()),
        )
        MobilePreferences(memoryPreferences(values)).save(expected)
        assertEquals(expected, MobilePreferences(memoryPreferences(values)).load())
    }

    @Test fun `future schema load does not rewrite stored values`() {
        val values = mutableMapOf<String, Any?>("schema_version" to 99, "transport_mode" to "FUTURE")
        val before = values.toMap()
        assertEquals(MobilePreferenceSnapshot(), MobilePreferences(memoryPreferences(values)).load())
        assertEquals(before, values)
    }

    @Test fun `corrupt enums preserve existing fallback defaults`() {
        val values = mutableMapOf<String, Any?>("capture_kind" to "REMOVED", "bluetooth_codec" to "UNKNOWN")
        val loaded = MobilePreferences(memoryPreferences(values)).load()
        assertEquals(CaptureKind.DEVICE_PLAYBACK, loaded.captureKind)
        assertEquals(BluetoothStreamCodec.IMA_ADPCM, loaded.bluetoothCodec)
        assertEquals(BluetoothChannelMode.STEREO, loaded.bluetoothChannelMode)
    }

    private fun memoryPreferences(values: MutableMap<String, Any?>): SharedPreferences {
        val pending = mutableMapOf<String, Any?>()
        lateinit var editor: SharedPreferences.Editor
        editor = Proxy.newProxyInstance(
            SharedPreferences.Editor::class.java.classLoader,
            arrayOf(SharedPreferences.Editor::class.java),
        ) { _, method, args ->
            when {
                method.name.startsWith("put") -> { pending[args!![0] as String] = args[1]; editor }
                method.name == "apply" || method.name == "commit" -> { values.putAll(pending); pending.clear(); true }
                else -> error("Unexpected editor call ${method.name}")
            }
        } as SharedPreferences.Editor
        return Proxy.newProxyInstance(
            SharedPreferences::class.java.classLoader,
            arrayOf(SharedPreferences::class.java),
        ) { _, method, args ->
            when {
                method.name == "edit" -> editor
                method.name.startsWith("get") -> values[args!![0] as String] ?: args[1]
                else -> error("Unexpected preferences call ${method.name}")
            }
        } as SharedPreferences
    }
}
