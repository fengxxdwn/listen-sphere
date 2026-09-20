package io.listensphere.mobile.service

import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import io.listensphere.mobile.core.model.StreamStatus
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

class AudioStreamingService : Service() {
    companion object {
        const val ACTION_START = "io.listensphere.mobile.START_STREAM"
        const val ACTION_STOP = "io.listensphere.mobile.STOP_STREAM"
        const val EXTRA_DEVICE_ID = "device_id"
        const val EXTRA_CONTROLLER_NAME = "controller_name"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_PAIRING_CODE = "pairing_code"
        const val EXTRA_CAPTURE_KIND = "capture_kind"
        const val EXTRA_TRANSPORT_MODE = "transport_mode"
        const val EXTRA_USB_NATIVE = "usb_native"
        const val EXTRA_BLUETOOTH_ADDRESS = "bluetooth_address"
        const val EXTRA_BLUETOOTH_CODEC = "bluetooth_codec"
        const val EXTRA_BLUETOOTH_CHANNEL_MODE = "bluetooth_channel_mode"
        const val EXTRA_PROJECTION_RESULT = "projection_result"
        const val EXTRA_PROJECTION_DATA = "projection_data"

        private val mutableStatus = MutableStateFlow(StreamStatus())
        val status: StateFlow<StreamStatus> = mutableStatus.asStateFlow()

        fun stopIntent(context: Context): Intent = Intent(
            context,
            AudioStreamingService::class.java,
        ).setAction(ACTION_STOP)
    }

    private lateinit var session: StreamingSession

    override fun onCreate() {
        super.onCreate()
        session = StreamingSession(this, mutableStatus)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> session.start(requireNotNull(intent))
            ACTION_STOP -> session.stop()
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        session.close()
        super.onDestroy()
    }
}
