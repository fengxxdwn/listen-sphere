package io.listensphere.mobile.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.ControllerEndpoint

@Composable
internal fun ControllerRow(endpoint: ControllerEndpoint, selected: Boolean, onClick: () -> Unit) {
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
