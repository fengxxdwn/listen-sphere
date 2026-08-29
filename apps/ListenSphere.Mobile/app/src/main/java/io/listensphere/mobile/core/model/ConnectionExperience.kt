package io.listensphere.mobile.core.model

data class ConnectionErrorPresentation(
    val title: String,
    val detail: String,
)

data class RecentConnection(
    val transportMode: TransportMode,
    val targetName: String,
    val connectedAtEpochMilliseconds: Long,
)

data class RuntimeReadinessState(
    val microphonePermissionGranted: Boolean = false,
    val bluetoothPermissionGranted: Boolean = false,
    val notificationPermissionGranted: Boolean = false,
    val batteryOptimizationDisabled: Boolean = false,
) {
    val hasMissingPermission: Boolean
        get() = !microphonePermissionGranted || !bluetoothPermissionGranted ||
            !notificationPermissionGranted
}

fun describeConnectionError(
    mode: TransportMode,
    error: Throwable,
): ConnectionErrorPresentation {
    val details = generateSequence(error) { it.cause }
        .mapNotNull { it.message }
        .joinToString(" ")
    val classNames = generateSequence(error) { it.cause }
        .joinToString(" ") { it.javaClass.simpleName }
    return when {
        classNames.contains("ControllerDisconnectedException") -> ConnectionErrorPresentation(
            "已由主控端断开",
            "本次连接已停止；需要继续时请手动重新连接。",
        )
        details.contains("permission", true) || details.contains("权限", true) ->
            ConnectionErrorPresentation(
                "需要系统权限",
                when (mode) {
                    TransportMode.BLUETOOTH -> "请允许“附近的设备”权限，然后返回聆界重试。"
                    TransportMode.USB -> "请允许聆界访问当前 USB 设备，然后重试。"
                    TransportMode.WIFI -> "请允许网络和音频相关权限，然后重试。"
                },
            )
        details.contains("certificate", true) || details.contains("证书", true) ||
            details.contains("fingerprint", true) || details.contains("指纹", true) ->
            ConnectionErrorPresentation(
                "设备身份验证失败",
                "主控端身份可能已变化，请在两端删除旧信任后重新配对。",
            )
        details.contains("timed out", true) || details.contains("timeout", true) ||
            details.contains("超时", true) -> ConnectionErrorPresentation(
                "连接主控端超时",
                transportRecoveryHint(mode),
            )
        details.contains("refused", true) || details.contains("unreachable", true) ||
            details.contains("read failed", true) || details.contains("socket closed", true) ||
            details.contains("read return: -1", true) -> ConnectionErrorPresentation(
                "主控端没有响应",
                transportRecoveryHint(mode),
            )
        details.contains("RSA routines", true) || details.contains("native_crypto", true) ->
            ConnectionErrorPresentation(
                "手机安全组件不兼容",
                "请安装最新版本的聆界，然后重新连接。",
            )
        else -> ConnectionErrorPresentation(
            "连接发生问题",
            transportRecoveryHint(mode),
        )
    }
}

private fun transportRecoveryHint(mode: TransportMode): String = when (mode) {
    TransportMode.WIFI -> "请确认手机与电脑位于同一网络、Controller 正在运行，并检查 IP 和端口。"
    TransportMode.BLUETOOTH -> "请确认电脑 Controller 和蓝牙均已开启、设备已在系统中配对，然后重试。"
    TransportMode.USB -> "请重新插拔数据线，确认 USB 授权和电脑 Controller 状态后重试。"
}
