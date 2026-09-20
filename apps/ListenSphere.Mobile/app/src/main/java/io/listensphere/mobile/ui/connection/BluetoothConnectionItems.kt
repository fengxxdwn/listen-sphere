package io.listensphere.mobile.ui

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Bluetooth
import androidx.compose.material.icons.outlined.GraphicEq
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothCodecChoice
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.model.ControllerEndpoint

internal fun LazyListScope.bluetoothConnectionItems(
    viewModel: MobileViewModel,
    controllers: List<ControllerEndpoint>,
    bluetoothPeers: List<BluetoothPeer>,
    selectedBluetoothKey: String?,
    bluetoothDiscoveryStatus: String,
    bluetoothProbeStatus: String,
    bluetoothProbeRunning: Boolean,
    bluetoothCodecChoices: List<BluetoothCodecChoice>,
    bluetoothChannelModes: List<BluetoothChannelMode>,
    selectedBluetoothCodec: BluetoothStreamCodec,
    selectedBluetoothChannelMode: BluetoothChannelMode,
    onBluetoothProbe: () -> Unit,
) {
    item {
        SectionTitle(
            icon = Icons.Outlined.Bluetooth,
            title = "选择已配对电脑",
            action = {
                RefreshButton(onClick = viewModel::refreshBluetoothPeers)
            },
        )
    }
    if (bluetoothPeers.isEmpty()) {
        item {
            QuietCard {
                Text("尚未发现已配对电脑", fontWeight = FontWeight.SemiBold)
                Text(
                    bluetoothDiscoveryStatus,
                    color = ListenSphereSecondary,
                    fontSize = 13.sp,
                )
                Button(
                    onClick = onBluetoothProbe,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Icon(Icons.Outlined.Bluetooth, contentDescription = null)
                    Spacer(Modifier.size(8.dp))
                    Text("授权并读取设备")
                }
            }
        }
    } else {
        items(bluetoothPeers, key = BluetoothPeer::stableKey) { peer ->
            BluetoothPeerRow(
                peer = peer,
                linkedControllerName = controllers.firstOrNull {
                    it.deviceId != null && it.deviceId == peer.controllerId
                }?.displayName,
                selected = selectedBluetoothKey == peer.stableKey,
                onClick = { viewModel.selectBluetoothPeer(peer) },
            )
        }
    }
    item {
        QuietCard {
            Text("蓝牙连接检查", fontWeight = FontWeight.Bold)
            Text(
                bluetoothProbeStatus,
                color = ListenSphereSecondary,
                fontSize = 13.sp,
            )
            Button(
                onClick = onBluetoothProbe,
                enabled = selectedBluetoothKey != null && !bluetoothProbeRunning,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Icon(Icons.Outlined.Bluetooth, contentDescription = null)
                Spacer(Modifier.size(8.dp))
                Text(if (bluetoothProbeRunning) "正在测试…" else "测试蓝牙连接")
            }
        }
    }
    item {
        SectionTitle(Icons.Outlined.GraphicEq, "蓝牙音频格式")
    }
    item {
        BluetoothAudioFormatSelector(
            codecChoices = bluetoothCodecChoices,
            selectedCodec = selectedBluetoothCodec,
            channelModes = bluetoothChannelModes,
            selectedChannelMode = selectedBluetoothChannelMode,
            onCodecSelected = viewModel::selectBluetoothCodec,
            onChannelSelected = viewModel::selectBluetoothChannelMode,
        )
    }
    item {
        Text(
            "可选项来自“手机编码能力 ∩ 主控解码能力 ∩ 聆界已实现格式”。" +
                "系统 A2DP 支持不等于应用可直接调用。",
            color = ListenSphereSecondary,
            fontSize = 12.sp,
        )
    }
}
