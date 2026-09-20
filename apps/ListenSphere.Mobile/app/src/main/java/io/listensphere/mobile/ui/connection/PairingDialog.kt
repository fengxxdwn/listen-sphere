package io.listensphere.mobile.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Security
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus

@Composable
internal fun PairingDialog(
    viewModel: MobileViewModel,
    streamStatus: StreamStatus,
    pairingCode: String,
    captureKind: CaptureKind,
    onStart: (CaptureKind, String?) -> Unit,
    onStop: () -> Unit,
) {
    if (streamStatus.phase == StreamPhase.PAIRING_REQUIRED) {
        AlertDialog(
            onDismissRequest = {
                viewModel.clearPairingCode()
                onStop()
            },
            icon = {
                Icon(
                    Icons.Outlined.Security,
                    contentDescription = null,
                    tint = ListenSphereAccent,
                )
            },
            title = { Text(streamStatus.title.ifBlank { "首次配对" }) },
            text = {
                Column {
                    Text(
                        streamStatus.detail.ifBlank {
                            "请在主控端生成配对码，然后输入六位数字。"
                        },
                        color = ListenSphereSecondary,
                    )
                    Spacer(Modifier.height(16.dp))
                    OutlinedTextField(
                        value = pairingCode,
                        onValueChange = viewModel::updatePairingCode,
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        label = { Text("六位配对码") },
                        leadingIcon = {
                            Icon(Icons.Outlined.Security, contentDescription = null)
                        },
                        keyboardOptions = KeyboardOptions(
                            keyboardType = KeyboardType.NumberPassword,
                        ),
                    )
                }
            },
            confirmButton = {
                TextButton(
                    enabled = pairingCode.length == 6,
                    onClick = {
                        val submittedCode = pairingCode
                        viewModel.clearPairingCode()
                        onStart(captureKind, submittedCode)
                    },
                ) {
                    Text("确认配对")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = {
                        viewModel.clearPairingCode()
                        onStop()
                    },
                ) {
                    Text("取消")
                }
            },
            containerColor = ListenSphereSurface,
        )
    }
}
