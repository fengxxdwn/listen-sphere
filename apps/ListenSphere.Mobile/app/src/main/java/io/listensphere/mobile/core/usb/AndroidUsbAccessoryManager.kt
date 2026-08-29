package io.listensphere.mobile.core.usb

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager

data class UsbAccessoryState(
    val attached: Boolean,
    val permissionGranted: Boolean,
    val displayName: String,
    val detail: String,
)

class AndroidUsbAccessoryManager(context: Context) {
    private val applicationContext = context.applicationContext
    private val usbManager = applicationContext.getSystemService(UsbManager::class.java)

    fun selectedAccessory(): UsbAccessory? = usbManager.accessoryList
        ?.firstOrNull { accessory ->
            accessory.manufacturer.equals("ListenSphere", ignoreCase = true) ||
                accessory.model.contains("ListenSphere", ignoreCase = true)
        }

    fun state(): UsbAccessoryState {
        val accessory = selectedAccessory() ?: return UsbAccessoryState(
            attached = false,
            permissionGranted = false,
            displayName = "尚未检测到主控端",
            detail = "请连接 USB 数据线，并保持电脑端 ListenSphere Controller 运行。",
        )
        val granted = usbManager.hasPermission(accessory)
        return UsbAccessoryState(
            attached = true,
            permissionGranted = granted,
            displayName = accessory.description ?: accessory.model,
            detail = if (granted) "原生 USB 通道已就绪，不会改变网络路由。" else "需要授权聆界访问 USB 设备。",
        )
    }

    fun requestPermission(accessory: UsbAccessory, action: String) {
        val permissionIntent = PendingIntent.getBroadcast(
            applicationContext,
            1702,
            Intent(action).setPackage(applicationContext.packageName),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
        )
        usbManager.requestPermission(accessory, permissionIntent)
    }

    fun open(): OpenUsbAccessory {
        val accessory = selectedAccessory() ?: error("尚未检测到 ListenSphere USB 主控端。")
        check(usbManager.hasPermission(accessory)) { "尚未获得 USB 设备访问权限。" }
        val descriptor = usbManager.openAccessory(accessory)
            ?: error("无法打开 ListenSphere USB 通道。")
        return OpenUsbAccessory(accessory, descriptor)
    }
}

class OpenUsbAccessory(
    val accessory: UsbAccessory,
    val descriptor: android.os.ParcelFileDescriptor,
) : AutoCloseable {
    override fun close() = descriptor.close()
}
