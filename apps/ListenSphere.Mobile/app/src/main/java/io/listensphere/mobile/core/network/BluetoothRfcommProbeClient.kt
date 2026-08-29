package io.listensphere.mobile.core.network

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.content.Context
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.BluetoothControllerCapabilities
import io.listensphere.mobile.core.protocol.GuidWire
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.nio.charset.StandardCharsets
import java.util.UUID

data class BluetoothProbeResult(
    val controllerId: UUID?,
    val controllerName: String,
    val capabilities: BluetoothControllerCapabilities,
)

class BluetoothRfcommProbeClient(context: Context) {
    companion object {
        val SERVICE_ID: UUID = UUID.fromString("d8409f76-1c4f-4d6c-a34b-77d80e71f12a")
    }

    private val adapter = context.getSystemService(BluetoothManager::class.java)?.adapter

    @SuppressLint("MissingPermission")
    suspend fun probe(peer: BluetoothPeer): BluetoothProbeResult = withContext(Dispatchers.IO) {
        val activeAdapter = adapter ?: error("此手机不支持蓝牙。")
        val socket = activeAdapter
            .getRemoteDevice(peer.address)
            .createRfcommSocketToServiceRecord(SERVICE_ID)
        socket.use { activeSocket ->
            activeSocket.connect()
            val output = DataOutputStream(activeSocket.outputStream)
            BluetoothHandshakeCodec.writeRequest(output, BluetoothHandshakeCodec.MODE_PROBE)
            output.flush()

            readBluetoothProbeResponse(DataInputStream(activeSocket.inputStream))
        }
    }
}

internal fun readBluetoothProbeResponse(input: DataInputStream): BluetoothProbeResult {
    val responseMagic = ByteArray(4)
    input.readFully(responseMagic)
    if (!responseMagic.contentEquals(BluetoothHandshakeCodec.magic)) {
        throw IOException("主控端返回了无效的蓝牙握手响应。")
    }
    val major = input.readUnsignedByte()
    val flags = input.readUnsignedByte()
    if (major != 1) {
        throw IOException("主控端蓝牙协议版本不兼容：$major。")
    }
    val nameLength = input.readUnsignedShort()
    if (nameLength !in 1..512) {
        throw IOException("主控端名称长度无效。")
    }
    val name = ByteArray(nameLength)
    input.readFully(name)
    val certificateLength = input.readUnsignedShort()
    if (certificateLength !in 0..8192) throw IOException("主控端身份凭据长度无效。")
    if (certificateLength > 0) input.readFully(ByteArray(certificateLength))
    val codecMask = input.readInt()
    val channelMask = input.readUnsignedByte()
    val controllerId = if (flags and 0x02 != 0) {
        GuidWire.fromDotNetBytes(ByteArray(16).also(input::readFully))
    } else {
        null
    }
    return BluetoothProbeResult(
        controllerId,
        String(name, StandardCharsets.UTF_8),
        BluetoothControllerCapabilities(codecMask, channelMask),
    )
}
