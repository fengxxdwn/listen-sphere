package io.listensphere.mobile.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Bluetooth
import androidx.compose.material.icons.outlined.Usb
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.TransportMode

@Composable
internal fun TransportModeSelector(
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
