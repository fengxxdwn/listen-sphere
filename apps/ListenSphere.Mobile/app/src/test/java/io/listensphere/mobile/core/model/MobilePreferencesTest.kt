package io.listensphere.mobile.core.model

import org.junit.Assert.assertEquals
import org.junit.Test
import java.util.UUID

class MobilePreferencesTest {
    @Test
    fun `stage five schema advances without discarding known values`() {
        assertEquals(2, MobilePreferences.CURRENT_SCHEMA_VERSION)
    }

    @Test
    fun `enum migration retains known values`() {
        assertEquals(TransportMode.BLUETOOTH, enumOrDefault("BLUETOOTH", TransportMode.WIFI))
        assertEquals(
            CaptureKind.DEVICE_PLAYBACK,
            enumOrDefault("DEVICE_PLAYBACK", CaptureKind.MICROPHONE),
        )
    }

    @Test
    fun `enum migration falls back for removed or corrupt values`() {
        assertEquals(TransportMode.WIFI, enumOrDefault("INFRARED", TransportMode.WIFI))
        assertEquals(
            BluetoothChannelMode.STEREO,
            enumOrDefault(null, BluetoothChannelMode.STEREO),
        )
    }

    @Test
    fun `bluetooth association migration validates address and uuid`() {
        val id = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")

        assertEquals(
            "F4:6D:3F:6F:DF:B9" to id,
            parseBluetoothControllerAssociation("f4:6d:3f:6f:df:b9|$id"),
        )
        assertEquals(null, parseBluetoothControllerAssociation("invalid"))
        assertEquals(null, parseBluetoothControllerAssociation("F4:6D|not-a-uuid"))
    }
}
