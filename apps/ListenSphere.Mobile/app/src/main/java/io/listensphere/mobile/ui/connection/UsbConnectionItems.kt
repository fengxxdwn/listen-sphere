package io.listensphere.mobile.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Usb
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.usb.UsbAccessoryState

internal fun LazyListScope.usbConnectionItems(
    viewModel: MobileViewModel,
    manualHost: String,
    manualPort: String,
    usbCompatibilityMode: Boolean,
    usbAccessoryState: UsbAccessoryState,
) {
    item {
        SectionTitle(Icons.Outlined.Usb, "USB 有线连接")
    }
    item {
        QuietCard {
            Text(
                if (usbCompatibilityMode) "USB 网络兼容模式" else "原生 USB 直连",
                fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(5.dp))
            Text(
                if (usbCompatibilityMode) {
                    "仅在原生 USB 不受设备支持时使用；该模式可能改变电脑网络路由。"
                } else {
                    "${usbAccessoryState.displayName}\n${usbAccessoryState.detail}"
                },
                color = ListenSphereSecondary,
                fontSize = 13.sp,
            )
            Spacer(Modifier.height(8.dp))
            TextButton(onClick = viewModel::refreshUsbAccessoryState) {
                Icon(Icons.Outlined.Refresh, contentDescription = null)
                Spacer(Modifier.width(6.dp))
                Text("重新检测 USB")
            }
        }
    }
    item {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Column(Modifier.weight(1f)) {
                Text("使用 USB 网络兼容模式")
                Text(
                    "开启后需要 USB 网络共享、电脑 IP 和端口",
                    color = ListenSphereSecondary,
                    fontSize = 12.sp,
                )
            }
            Switch(
                checked = usbCompatibilityMode,
                onCheckedChange = { viewModel.usbCompatibilityMode.value = it },
            )
        }
    }
    if (usbCompatibilityMode) {
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = manualHost,
                    onValueChange = { viewModel.manualHost.value = it },
                    modifier = Modifier.weight(1f),
                    singleLine = true,
                    label = { Text("电脑 USB 网卡 IPv4") },
                )
                OutlinedTextField(
                    value = manualPort,
                    onValueChange = {
                        viewModel.manualPort.value = it.filter(Char::isDigit).take(5)
                    },
                    modifier = Modifier.weight(0.55f),
                    singleLine = true,
                    label = { Text("端口") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                )
            }
        }
    }
}
