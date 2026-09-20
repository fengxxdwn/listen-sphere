package io.listensphere.mobile.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.Modifier

@Composable
internal fun RefreshButton(onClick: () -> Unit) {
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
