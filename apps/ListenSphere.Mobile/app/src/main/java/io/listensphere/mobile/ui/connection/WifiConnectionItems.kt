package io.listensphere.mobile.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.ControllerEndpoint

internal fun LazyListScope.wifiConnectionItems(
    viewModel: MobileViewModel,
    controllers: List<ControllerEndpoint>,
    selectedKey: String?,
    manualMode: Boolean,
    manualHost: String,
    manualPort: String,
) {
    item {
        SectionTitle(
            icon = Icons.Outlined.Wifi,
            title = "选择主控端",
            action = {
                RefreshButton(onClick = viewModel::refreshDiscovery)
            },
        )
    }
    if (!manualMode) {
        if (controllers.isEmpty()) {
            item {
                QuietCard {
                    Text("正在局域网中寻找聆界主控端…", fontWeight = FontWeight.SemiBold)
                    Text(
                        "请确保手机与电脑连接到同一个局域网，并已启动 Controller。",
                        color = ListenSphereSecondary,
                        fontSize = 13.sp,
                    )
                }
            }
        } else {
            items(controllers, key = ControllerEndpoint::stableKey) { controller ->
                ControllerRow(
                    endpoint = controller,
                    selected = selectedKey == controller.stableKey,
                    onClick = { viewModel.selectController(controller) },
                )
            }
        }
    }
    item {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f)) {
                Text("手动地址", fontWeight = FontWeight.SemiBold)
                Text(
                    "仅在自动发现不可用时使用",
                    color = ListenSphereSecondary,
                    fontSize = 12.sp,
                )
            }
            Switch(
                checked = manualMode,
                onCheckedChange = { viewModel.manualMode.value = it },
            )
        }
    }
    if (manualMode) {
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = manualHost,
                    onValueChange = { viewModel.manualHost.value = it },
                    modifier = Modifier.weight(1f),
                    singleLine = true,
                    label = { Text("电脑 IP 地址") },
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
