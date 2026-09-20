package io.listensphere.mobile.ui

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Security
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.sp
import io.listensphere.mobile.core.model.RuntimeReadinessState

internal fun LazyListScope.readinessItems(
    runtimeReadiness: RuntimeReadinessState,
    onOpenAppSettings: () -> Unit,
    onOpenBatterySettings: () -> Unit,
) {
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
}
