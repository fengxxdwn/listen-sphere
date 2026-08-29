package io.listensphere.mobile.core.network

import io.listensphere.mobile.core.model.TransportMode
import org.junit.Assert.assertArrayEquals
import org.junit.Test

class ControlTransportPrefaceTest {
    @Test
    fun encodesStableTransportCodes() {
        assertArrayEquals(byteArrayOf(76, 83, 84, 72, 1), ControlTransportPreface.encode(TransportMode.WIFI))
        assertArrayEquals(byteArrayOf(76, 83, 84, 72, 2), ControlTransportPreface.encode(TransportMode.BLUETOOTH))
        assertArrayEquals(byteArrayOf(76, 83, 84, 72, 3), ControlTransportPreface.encode(TransportMode.USB))
    }
}
