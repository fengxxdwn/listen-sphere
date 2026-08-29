package io.listensphere.mobile.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.expandVertically
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.Bluetooth
import androidx.compose.material.icons.outlined.GraphicEq
import androidx.compose.material.icons.outlined.Headphones
import androidx.compose.material.icons.outlined.Mic
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Security
import androidx.compose.material.icons.outlined.Stop
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material.icons.outlined.Usb
import androidx.compose.material3.Button
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothCodecChoice
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.model.ControllerEndpoint
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.service.AudioStreamingService

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ListenSphereScreen(
    viewModel: MobileViewModel,
    onStart: (CaptureKind, String?) -> Unit,
    onStop: () -> Unit,
    onBluetoothProbe: () -> Unit,
    onOpenAppSettings: () -> Unit,
    onOpenBatterySettings: () -> Unit,
) {
    val controllers by viewModel.controllers.collectAsState()
    val selectedKey by viewModel.selectedKey.collectAsState()
    val pairingCode by viewModel.pairingCode.collectAsState()
    val manualMode by viewModel.manualMode.collectAsState()
    val manualHost by viewModel.manualHost.collectAsState()
    val manualPort by viewModel.manualPort.collectAsState()
    val captureKind by viewModel.captureKind.collectAsState()
    val transportMode by viewModel.transportMode.collectAsState()
    val bluetoothPeers by viewModel.bluetoothPeers.collectAsState()
    val selectedBluetoothKey by viewModel.selectedBluetoothKey.collectAsState()
    val bluetoothDiscoveryStatus by viewModel.bluetoothDiscoveryStatus.collectAsState()
    val bluetoothProbeStatus by viewModel.bluetoothProbeStatus.collectAsState()
    val bluetoothProbeRunning by viewModel.bluetoothProbeRunning.collectAsState()
    val bluetoothCodecChoices by viewModel.bluetoothCodecChoices.collectAsState()
    val bluetoothChannelModes by viewModel.bluetoothChannelModes.collectAsState()
    val selectedBluetoothCodec by viewModel.selectedBluetoothCodec.collectAsState()
    val selectedBluetoothChannelMode by viewModel.selectedBluetoothChannelMode.collectAsState()
    val usbCompatibilityMode by viewModel.usbCompatibilityMode.collectAsState()
    val usbAccessoryState by viewModel.usbAccessoryState.collectAsState()
    val recentConnections by viewModel.recentConnections.collectAsState()
    val runtimeReadiness by viewModel.runtimeReadiness.collectAsState()
    val streamStatus by AudioStreamingService.status.collectAsState()
    val streaming = streamStatus.phase == StreamPhase.STREAMING ||
        streamStatus.phase == StreamPhase.CONNECTING

    ListenSphereTheme {
        Scaffold(
            containerColor = ListenSphereBackground,
            topBar = {
                TopAppBar(
                    colors = TopAppBarDefaults.topAppBarColors(
                        containerColor = ListenSphereBackground,
                    ),
                    title = {
                        Column {
                            Text("聆界", fontWeight = FontWeight.Bold, fontSize = 24.sp)
                            Text(
                                "ListenSphere Mobile",
                                color = ListenSphereSecondary,
                                fontSize = 12.sp,
                            )
                        }
                    },
                    actions = {
                        StatusDot(streamStatus.phase)
                        Text(
                            streamStatus.phase.displayName,
                            modifier = Modifier.padding(end = 16.dp),
                            color = ListenSphereSecondary,
                            fontSize = 12.sp,
                        )
                    },
                )
            },
        ) { padding ->
            LazyColumn(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(padding)
                    .padding(horizontal = 18.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                item {
                    FlowCard(streamStatus)
                }
                item {
                    SectionTitle(Icons.Outlined.Wifi, "连接方式")
                }
                item {
                    TransportModeSelector(
                        selected = transportMode,
                        recent = recentConnections.firstOrNull()?.transportMode,
                        onSelected = viewModel::selectTransportMode,
                    )
                }
                if (recentConnections.isNotEmpty()) {
                    item {
                        QuietCard {
                            Text("最近成功连接", fontWeight = FontWeight.SemiBold)
                            recentConnections.forEach { connection ->
                                Text(
                                    "${connection.transportMode.displayName} · ${connection.targetName}",
                                    color = if (connection == recentConnections.first()) {
                                        ListenSphereSuccess
                                    } else {
                                        ListenSphereSecondary
                                    },
                                    fontSize = 12.sp,
                                )
                            }
                        }
                    }
                }
                if (transportMode == TransportMode.WIFI) {
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
                } else if (transportMode == TransportMode.BLUETOOTH) {
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
                } else {
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
                item {
                    SectionTitle(Icons.Outlined.GraphicEq, "声音来源")
                }
                item {
                    CaptureSourceRow(
                        icon = Icons.Outlined.Mic,
                        title = "手机麦克风",
                        detail = "稳定、兼容性最好，适合语音和环境声",
                        selected = captureKind == CaptureKind.MICROPHONE,
                        onClick = { viewModel.captureKind.value = CaptureKind.MICROPHONE },
                    )
                }
                item {
                    CaptureSourceRow(
                        icon = Icons.Outlined.PhoneAndroid,
                        title = "手机播放声音",
                        detail = "需要系统录屏授权；部分应用和受保护内容无法捕获",
                        selected = captureKind == CaptureKind.DEVICE_PLAYBACK,
                        onClick = { viewModel.captureKind.value = CaptureKind.DEVICE_PLAYBACK },
                    )
                }
                item {
                    SectionTitle(Icons.Outlined.Security, "后台与权限")
                }
                item {
                    QuietCard {
                        PermissionStatusLine(
                            "麦克风与音频采集",
                            runtimeReadiness.microphonePermissionGranted,
                        )
                        PermissionStatusLine(
                            "附近的蓝牙设备",
                            runtimeReadiness.bluetoothPermissionGranted,
                        )
                        PermissionStatusLine(
                            "前台服务通知",
                            runtimeReadiness.notificationPermissionGranted,
                        )
                        PermissionStatusLine(
                            "后台电池限制",
                            runtimeReadiness.batteryOptimizationDisabled,
                            grantedLabel = "不受限制",
                            missingLabel = "可能中断",
                        )
                        if (runtimeReadiness.hasMissingPermission) {
                            Button(
                                onClick = onOpenAppSettings,
                                modifier = Modifier.fillMaxWidth(),
                            ) {
                                Text("打开应用权限设置")
                            }
                        }
                        if (!runtimeReadiness.batteryOptimizationDisabled) {
                            TextButton(
                                onClick = onOpenBatterySettings,
                                modifier = Modifier.fillMaxWidth(),
                            ) {
                                Text("查看电池优化设置")
                            }
                        }
                        Text(
                            "系统回收进程后不会自动恢复录音；请重新打开聆界并手动开始发送。",
                            color = ListenSphereSecondary,
                            fontSize = 11.sp,
                        )
                    }
                }
                item {
                    Button(
                        onClick = if (streaming) onStop else { { onStart(captureKind, null) } },
                        enabled = streaming || transportMode.available,
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(52.dp),
                        shape = RoundedCornerShape(10.dp),
                    ) {
                        Icon(
                            if (streaming) Icons.Outlined.Stop else Icons.Outlined.Headphones,
                            contentDescription = null,
                        )
                        Spacer(Modifier.size(8.dp))
                        Text(if (streaming) "停止发送" else "开始发送", fontWeight = FontWeight.Bold)
                    }
                }
                item { Spacer(Modifier.height(20.dp)) }
            }
        }
        if (streamStatus.phase == StreamPhase.PAIRING_REQUIRED) {
            AlertDialog(
                onDismissRequest = {
                    viewModel.clearPairingCode()
                    onStop()
                },
                icon = {
                    Icon(
                        Icons.Outlined.Security,
                        contentDescription = null,
                        tint = ListenSphereAccent,
                    )
                },
                title = { Text(streamStatus.title.ifBlank { "首次配对" }) },
                text = {
                    Column {
                        Text(
                            streamStatus.detail.ifBlank {
                                "请在主控端生成配对码，然后输入六位数字。"
                            },
                            color = ListenSphereSecondary,
                        )
                        Spacer(Modifier.height(16.dp))
                        OutlinedTextField(
                            value = pairingCode,
                            onValueChange = viewModel::updatePairingCode,
                            modifier = Modifier.fillMaxWidth(),
                            singleLine = true,
                            label = { Text("六位配对码") },
                            leadingIcon = {
                                Icon(Icons.Outlined.Security, contentDescription = null)
                            },
                            keyboardOptions = KeyboardOptions(
                                keyboardType = KeyboardType.NumberPassword,
                            ),
                        )
                    }
                },
                confirmButton = {
                    TextButton(
                        enabled = pairingCode.length == 6,
                        onClick = {
                            val submittedCode = pairingCode
                            viewModel.clearPairingCode()
                            onStart(captureKind, submittedCode)
                        },
                    ) {
                        Text("确认配对")
                    }
                },
                dismissButton = {
                    TextButton(
                        onClick = {
                            viewModel.clearPairingCode()
                            onStop()
                        },
                    ) {
                        Text("取消")
                    }
                },
                containerColor = ListenSphereSurface,
            )
        }
    }
}

@Composable
private fun PermissionStatusLine(
    label: String,
    granted: Boolean,
    grantedLabel: String = "已允许",
    missingLabel: String = "未允许",
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(label, fontSize = 12.sp)
        Text(
            if (granted) grantedLabel else missingLabel,
            color = if (granted) ListenSphereSuccess else Color(0xFFE6B85C),
            fontSize = 12.sp,
        )
    }
}

@Composable
private fun BluetoothAudioFormatSelector(
    codecChoices: List<BluetoothCodecChoice>,
    selectedCodec: BluetoothStreamCodec,
    channelModes: List<BluetoothChannelMode>,
    selectedChannelMode: BluetoothChannelMode,
    onCodecSelected: (BluetoothStreamCodec) -> Unit,
    onChannelSelected: (BluetoothChannelMode) -> Unit,
) {
    QuietCard {
        Text("声道", fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(8.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            BluetoothChannelMode.entries.forEach { mode ->
                val enabled = channelModes.contains(mode)
                val selected = enabled && mode == selectedChannelMode
                Card(
                    modifier = Modifier
                        .weight(1f)
                        .clickable(enabled = enabled) { onChannelSelected(mode) },
                    colors = CardDefaults.cardColors(
                        containerColor = if (selected) Color(0xFF203154) else ListenSphereBackground,
                        disabledContainerColor = ListenSphereBackground,
                    ),
                    border = BorderStroke(
                        1.dp,
                        if (selected) ListenSphereAccent else ListenSphereStroke,
                    ),
                    shape = RoundedCornerShape(10.dp),
                ) {
                    Column(
                        Modifier.fillMaxWidth().padding(12.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Text(
                            mode.displayName,
                            color = if (enabled) Color.Unspecified else ListenSphereSecondary,
                            fontWeight = FontWeight.SemiBold,
                        )
                        Text(
                            if (enabled) "主控支持" else "等待检测或不支持",
                            color = if (enabled) ListenSphereSuccess else ListenSphereSecondary,
                            fontSize = 10.sp,
                        )
                    }
                }
            }
        }
        Spacer(Modifier.height(16.dp))
        Text("编解码格式", fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(6.dp))
        codecChoices.forEach { choice ->
            val selected = choice.enabled && choice.codec == selectedCodec
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(enabled = choice.enabled) { onCodecSelected(choice.codec) }
                    .padding(vertical = 9.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f)) {
                    Text(
                        choice.codec.displayName,
                        color = if (choice.enabled) Color.Unspecified else ListenSphereSecondary,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Text(
                        "${choice.codec.description} · ${choice.status}",
                        color = if (choice.enabled) ListenSphereSuccess else ListenSphereSecondary,
                        fontSize = 11.sp,
                    )
                }
                Checkbox(checked = selected, onCheckedChange = null, enabled = choice.enabled)
            }
        }
    }
}

@Composable
private fun BluetoothPeerRow(
    peer: BluetoothPeer,
    linkedControllerName: String?,
    selected: Boolean,
    onClick: () -> Unit,
) {
    Card(
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick),
        colors = CardDefaults.cardColors(
            containerColor = if (selected) Color(0xFF1B2A49) else ListenSphereSurface,
        ),
        border = BorderStroke(1.dp, if (selected) ListenSphereAccent else ListenSphereStroke),
        shape = RoundedCornerShape(10.dp),
    ) {
        Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.Bluetooth, contentDescription = null, tint = ListenSphereAccent)
            Column(Modifier.padding(start = 12.dp).weight(1f)) {
                Text(peer.displayName, fontWeight = FontWeight.SemiBold)
                Text(
                    linkedControllerName?.let { "${peer.address} · 已关联 $it" } ?: peer.address,
                    color = if (linkedControllerName == null) {
                        ListenSphereSecondary
                    } else {
                        ListenSphereSuccess
                    },
                    fontSize = 12.sp,
                )
            }
            Checkbox(checked = selected, onCheckedChange = null)
        }
    }
}

@Composable
private fun FlowCard(status: StreamStatus) {
    QuietCard(modifier = Modifier.animateContentSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(
                Icons.Outlined.PhoneAndroid,
                contentDescription = null,
                tint = ListenSphereAccent,
            )
            Text("手机", modifier = Modifier.padding(start = 8.dp), fontWeight = FontWeight.SemiBold)
            Text("  →  ", color = ListenSphereSecondary)
            Text("聆界", color = ListenSphereSuccess, fontWeight = FontWeight.Bold)
            Text("  →  ", color = ListenSphereSecondary)
            Icon(Icons.Outlined.Computer, contentDescription = null)
            Text("主控端", modifier = Modifier.padding(start = 8.dp))
        }
        Spacer(Modifier.height(14.dp))
        Text(status.title, fontWeight = FontWeight.Bold, fontSize = 18.sp)
        Text(status.detail, color = ListenSphereSecondary, fontSize = 13.sp)
        AnimatedVisibility(
            visible = status.phase == StreamPhase.STREAMING,
            enter = fadeIn(tween(240)) + expandVertically(tween(280)),
            exit = fadeOut(tween(160)) + shrinkVertically(tween(200)),
        ) {
            val animatedPeak by animateFloatAsState(
                targetValue = status.peak.coerceIn(0f, 1f),
                animationSpec = tween(90, easing = FastOutSlowInEasing),
                label = "audioPeak",
            )
            Column {
            Spacer(Modifier.height(14.dp))
            LinearProgressIndicator(
                progress = { animatedPeak },
                modifier = Modifier.fillMaxWidth(),
                color = ListenSphereSuccess,
                trackColor = ListenSphereStroke,
            )
            Spacer(Modifier.height(8.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text("已发送 ${status.framesSent} 帧", color = ListenSphereSecondary, fontSize = 12.sp)
                Text(
                    status.roundTripMilliseconds?.let { "${it.toInt()} ms" } ?: "测量中",
                    color = ListenSphereSecondary,
                    fontSize = 12.sp,
                )
            }
            }
        }
    }
}

@Composable
private fun TransportModeSelector(
    selected: TransportMode,
    recent: TransportMode?,
    onSelected: (TransportMode) -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        TransportMode.entries.forEach { mode ->
            val active = selected == mode
            val background by animateColorAsState(
                if (active) Color(0xFF203154) else ListenSphereSurface,
                tween(220),
                label = "transportBackground",
            )
            val icon = when (mode) {
                TransportMode.WIFI -> Icons.Outlined.Wifi
                TransportMode.BLUETOOTH -> Icons.Outlined.Bluetooth
                TransportMode.USB -> Icons.Outlined.Usb
            }
            Card(
                modifier = Modifier
                    .weight(1f)
                    .clickable { onSelected(mode) },
                colors = CardDefaults.cardColors(containerColor = background),
                border = BorderStroke(
                    1.dp,
                    if (active) ListenSphereAccent else ListenSphereStroke,
                ),
                shape = RoundedCornerShape(12.dp),
            ) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 12.dp, horizontal = 7.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Icon(
                        icon,
                        contentDescription = null,
                        tint = if (active) ListenSphereAccent else ListenSphereSecondary,
                    )
                    Spacer(Modifier.height(6.dp))
                    Text(mode.displayName, fontWeight = FontWeight.SemiBold, fontSize = 12.sp)
                    Text(
                        when {
                            mode == recent -> "最近使用"
                            mode.available -> "可用"
                            mode == TransportMode.BLUETOOTH -> "通道测试"
                            else -> "开发中"
                        },
                        color = when {
                            mode == recent -> ListenSphereAccent
                            mode.available -> ListenSphereSuccess
                            mode == TransportMode.BLUETOOTH -> ListenSphereAccent
                            else -> ListenSphereSecondary
                        },
                        fontSize = 10.sp,
                    )
                }
            }
        }
    }
}

@Composable
private fun RefreshButton(onClick: () -> Unit) {
    var turns by remember { mutableIntStateOf(0) }
    val rotation by animateFloatAsState(
        targetValue = turns * 360f,
        animationSpec = tween(520, easing = FastOutSlowInEasing),
        label = "refreshRotation",
    )
    IconButton(onClick = { turns++; onClick() }) {
        Icon(
            Icons.Outlined.Refresh,
            contentDescription = "刷新主控端",
            modifier = Modifier.graphicsLayer { rotationZ = rotation },
        )
    }
}

@Composable
private fun ControllerRow(endpoint: ControllerEndpoint, selected: Boolean, onClick: () -> Unit) {
    val background by animateColorAsState(
        if (selected) Color(0xFF1B2A49) else ListenSphereSurface,
        tween(220),
        label = "controllerBackground",
    )
    val scale by animateFloatAsState(
        if (selected) 1f else 0.985f,
        tween(220, easing = FastOutSlowInEasing),
        label = "controllerScale",
    )
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clickable(onClick = onClick),
        colors = CardDefaults.cardColors(
            containerColor = background,
        ),
        border = BorderStroke(1.dp, if (selected) ListenSphereAccent else ListenSphereStroke),
        shape = RoundedCornerShape(10.dp),
    ) {
        Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.Computer, contentDescription = null, tint = ListenSphereAccent)
            Column(Modifier.padding(start = 12.dp).weight(1f)) {
                Text(endpoint.displayName, fontWeight = FontWeight.SemiBold)
                Text(endpoint.addressLabel, color = ListenSphereSecondary, fontSize = 12.sp)
            }
            Checkbox(checked = selected, onCheckedChange = null)
        }
    }
}

@Composable
private fun CaptureSourceRow(
    icon: ImageVector,
    title: String,
    detail: String,
    selected: Boolean,
    onClick: () -> Unit,
) {
    Card(
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick),
        colors = CardDefaults.cardColors(
            containerColor = if (selected) Color(0xFF1B2A49) else ListenSphereSurface,
        ),
        border = BorderStroke(1.dp, if (selected) ListenSphereAccent else ListenSphereStroke),
        shape = RoundedCornerShape(10.dp),
    ) {
        Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, contentDescription = null, tint = if (selected) ListenSphereAccent else ListenSphereSecondary)
            Column(Modifier.padding(start = 12.dp).weight(1f)) {
                Text(title, fontWeight = FontWeight.SemiBold)
                Text(detail, color = ListenSphereSecondary, fontSize = 12.sp)
            }
            Checkbox(checked = selected, onCheckedChange = null)
        }
    }
}

@Composable
private fun SectionTitle(icon: ImageVector, title: String, action: (@Composable () -> Unit)? = null) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = ListenSphereAccent)
        Text(title, modifier = Modifier.padding(start = 8.dp).weight(1f), fontWeight = FontWeight.Bold)
        action?.invoke()
    }
}

@Composable
private fun QuietCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = ListenSphereSurface),
        border = BorderStroke(1.dp, ListenSphereStroke),
        shape = RoundedCornerShape(12.dp),
    ) {
        Column(Modifier.padding(16.dp), content = content)
    }
}

@Composable
private fun StatusDot(phase: StreamPhase) {
    val color = when (phase) {
        StreamPhase.STREAMING -> ListenSphereSuccess
        StreamPhase.CONNECTING, StreamPhase.DISCOVERING -> ListenSphereWarning
        StreamPhase.ERROR -> ListenSphereError
        else -> ListenSphereSecondary
    }
    val transition = rememberInfiniteTransition(label = "statusPulse")
    val pulse by transition.animateFloat(
        initialValue = 0.75f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(900, easing = FastOutSlowInEasing),
            repeatMode = RepeatMode.Reverse,
        ),
        label = "statusPulseValue",
    )
    Box(
        Modifier
            .padding(end = 7.dp)
            .size(8.dp)
            .graphicsLayer {
                alpha = if (phase == StreamPhase.STREAMING) pulse else 1f
                scaleX = if (phase == StreamPhase.STREAMING) pulse else 1f
                scaleY = if (phase == StreamPhase.STREAMING) pulse else 1f
            }
            .background(color, CircleShape),
    )
}

private val StreamPhase.displayName: String
    get() = when (this) {
        StreamPhase.IDLE -> "未连接"
        StreamPhase.DISCOVERING -> "正在发现"
        StreamPhase.CONNECTING -> "正在连接"
        StreamPhase.PAIRING_REQUIRED -> "等待配对"
        StreamPhase.STREAMING -> "正在发送"
        StreamPhase.ERROR -> "需要处理"
    }
