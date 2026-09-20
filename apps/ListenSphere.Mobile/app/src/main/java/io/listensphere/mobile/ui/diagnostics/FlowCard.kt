package io.listensphere.mobile.ui

import androidx.compose.animation.animateContentSize
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus

@Composable
internal fun FlowCard(status: StreamStatus) {
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
