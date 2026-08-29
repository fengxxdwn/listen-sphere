package io.listensphere.mobile.core.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test
import java.io.IOException

class ConnectionExperienceTest {
    @Test
    fun `runtime readiness reports any missing permission`() {
        val ready = RuntimeReadinessState(true, true, true, true)
        val missingNotification = ready.copy(notificationPermissionGranted = false)

        assertFalse(ready.hasMissingPermission)
        assertEquals(true, missingNotification.hasMissingPermission)
    }

    @Test
    fun `timeout uses transport specific recovery hint`() {
        val wifi = describeConnectionError(TransportMode.WIFI, IOException("connect timed out"))
        val bluetooth = describeConnectionError(
            TransportMode.BLUETOOTH,
            IOException("read failed, socket closed"),
        )

        assertEquals("连接主控端超时", wifi.title)
        assertFalse(wifi.detail.contains("timed out"))
        assertEquals("主控端没有响应", bluetooth.title)
        assertFalse(bluetooth.detail.contains("socket"))
    }

    @Test
    fun `permission guidance follows selected transport`() {
        val usb = describeConnectionError(
            TransportMode.USB,
            SecurityException("permission denied"),
        )

        assertEquals("需要系统权限", usb.title)
        assertEquals("请允许聆界访问当前 USB 设备，然后重试。", usb.detail)
    }

    @Test
    fun `unknown errors do not expose technical details`() {
        val result = describeConnectionError(
            TransportMode.WIFI,
            IllegalStateException("vendor stack internal code 734"),
        )

        assertEquals("连接发生问题", result.title)
        assertFalse(result.detail.contains("734"))
    }
}
