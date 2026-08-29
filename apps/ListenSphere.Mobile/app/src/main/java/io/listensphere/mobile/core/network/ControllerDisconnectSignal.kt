package io.listensphere.mobile.core.network

import io.listensphere.mobile.core.protocol.ControlFrameCodec
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException

internal object ControllerDisconnectSignal {
    fun await(input: DataInputStream): Nothing {
        val message = ControlFrameCodec.read(input)
            ?: throw EOFException("主控端关闭了连接。")
        if (message.hasDisconnect()) {
            throw ControllerDisconnectedException(
                message.disconnect.reason.ifBlank { "主控端已断开本次连接。" },
            )
        }
        throw IOException("音频会话收到未预期的控制消息。")
    }
}
