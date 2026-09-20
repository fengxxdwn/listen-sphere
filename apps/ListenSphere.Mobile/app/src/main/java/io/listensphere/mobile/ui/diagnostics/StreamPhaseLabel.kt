package io.listensphere.mobile.ui

import io.listensphere.mobile.core.model.StreamPhase

internal val StreamPhase.displayName: String
    get() = when (this) {
        StreamPhase.IDLE -> "未连接"
        StreamPhase.DISCOVERING -> "正在发现"
        StreamPhase.CONNECTING -> "正在连接"
        StreamPhase.PAIRING_REQUIRED -> "等待配对"
        StreamPhase.STREAMING -> "正在发送"
        StreamPhase.ERROR -> "需要处理"
    }
