package io.listensphere.mobile.core.network

import java.io.DataOutputStream

internal object BluetoothHandshakeCodec {
    val magic = byteArrayOf(
        'L'.code.toByte(),
        'S'.code.toByte(),
        'P'.code.toByte(),
        'B'.code.toByte(),
    )

    const val PROTOCOL_MAJOR = 1
    const val MODE_PROBE = 2
    const val MODE_STREAM_WITH_FORMAT_REQUEST = 3

    fun writeRequest(output: DataOutputStream, mode: Int) {
        output.write(magic)
        output.writeByte(PROTOCOL_MAJOR)
        output.writeByte(mode)
    }
}
