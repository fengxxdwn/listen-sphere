package io.listensphere.mobile.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothCodecChoice
import io.listensphere.mobile.core.model.BluetoothStreamCodec

@Composable
internal fun BluetoothAudioFormatSelector(
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
