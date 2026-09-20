package io.listensphere.mobile.ui

import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.GraphicEq
import androidx.compose.material.icons.outlined.Mic
import androidx.compose.material.icons.outlined.PhoneAndroid
import io.listensphere.mobile.core.model.CaptureKind

internal fun LazyListScope.captureItems(
    viewModel: MobileViewModel,
    captureKind: CaptureKind,
) {
    item {
        SectionTitle(Icons.Outlined.GraphicEq, "声音来源")
    }
    item {
        CaptureSourceRow(
            icon = Icons.Outlined.Mic,
            title = "手机麦克风",
            detail = "稳定、兼容性最好，适合语音和环境声",
            selected = captureKind == CaptureKind.MICROPHONE,
            onClick = { viewModel.captureKind.value = CaptureKind.MICROPHONE },
        )
    }
    item {
        CaptureSourceRow(
            icon = Icons.Outlined.PhoneAndroid,
            title = "手机播放声音",
            detail = "需要系统录屏授权；部分应用和受保护内容无法捕获",
            selected = captureKind == CaptureKind.DEVICE_PLAYBACK,
            onClick = { viewModel.captureKind.value = CaptureKind.DEVICE_PLAYBACK },
        )
    }
}
