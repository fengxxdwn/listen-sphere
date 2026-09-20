package io.listensphere.mobile.ui

import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import io.listensphere.mobile.core.model.StreamPhase

@Composable
internal fun StatusDot(phase: StreamPhase) {
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
