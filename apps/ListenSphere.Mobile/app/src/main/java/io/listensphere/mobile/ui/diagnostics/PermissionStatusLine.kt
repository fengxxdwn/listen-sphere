package io.listensphere.mobile.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.Row
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.sp

@Composable
internal fun PermissionStatusLine(
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
