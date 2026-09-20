package io.listensphere.mobile.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Headphones
import androidx.compose.material.icons.outlined.Stop
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.TransportMode

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
    val streamStatus by viewModel.streamStatus.collectAsState()
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
                    wifiConnectionItems(
                        viewModel,
                        controllers,
                        selectedKey,
                        manualMode,
                        manualHost,
                        manualPort,
                    )
                } else if (transportMode == TransportMode.BLUETOOTH) {
                    bluetoothConnectionItems(
                        viewModel,
                        controllers,
                        bluetoothPeers,
                        selectedBluetoothKey,
                        bluetoothDiscoveryStatus,
                        bluetoothProbeStatus,
                        bluetoothProbeRunning,
                        bluetoothCodecChoices,
                        bluetoothChannelModes,
                        selectedBluetoothCodec,
                        selectedBluetoothChannelMode,
                        onBluetoothProbe,
                    )
                } else {
                    usbConnectionItems(
                        viewModel,
                        manualHost,
                        manualPort,
                        usbCompatibilityMode,
                        usbAccessoryState,
                    )
                }
                captureItems(viewModel, captureKind)
                readinessItems(runtimeReadiness, onOpenAppSettings, onOpenBatterySettings)
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
        PairingDialog(viewModel, streamStatus, pairingCode, captureKind, onStart, onStop)
    }
}
