package io.listensphere.mobile.core.discovery

import android.Manifest
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import io.listensphere.mobile.core.model.BluetoothPeer
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class AndroidBluetoothDiscovery(private val context: Context) {
    private val adapter = context.getSystemService(BluetoothManager::class.java)?.adapter
    private val mutablePeers = MutableStateFlow<List<BluetoothPeer>>(emptyList())
    private val mutableStatus = MutableStateFlow("正在读取已配对设备…")

    val peers: StateFlow<List<BluetoothPeer>> = mutablePeers.asStateFlow()
    val status: StateFlow<String> = mutableStatus.asStateFlow()

    @Suppress("MissingPermission")
    fun refresh() {
        if (adapter == null) {
            mutablePeers.value = emptyList()
            mutableStatus.value = "此手机不支持蓝牙。"
            return
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.BLUETOOTH_CONNECT,
            ) != PackageManager.PERMISSION_GRANTED
        ) {
            mutablePeers.value = emptyList()
            mutableStatus.value = "需要“附近的设备”权限才能读取已配对电脑。"
            return
        }
        if (!adapter.isEnabled) {
            mutablePeers.value = emptyList()
            mutableStatus.value = "请先开启手机蓝牙。"
            return
        }

        mutablePeers.value = adapter.bondedDevices
            .map { device ->
                BluetoothPeer(
                    address = device.address,
                    displayName = device.name?.takeIf(String::isNotBlank) ?: "未命名蓝牙设备",
                )
            }
            .sortedBy { it.displayName.lowercase() }
        mutableStatus.value = if (mutablePeers.value.isEmpty()) {
            "未找到已配对设备，请先在系统蓝牙设置中与电脑配对。"
        } else {
            "选择已配对电脑，然后测试 ListenSphere RFCOMM 服务。"
        }
    }
}
