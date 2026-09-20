package io.listensphere.mobile.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Bluetooth
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.BluetoothPeer

@Composable
internal fun BluetoothPeerRow(
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
