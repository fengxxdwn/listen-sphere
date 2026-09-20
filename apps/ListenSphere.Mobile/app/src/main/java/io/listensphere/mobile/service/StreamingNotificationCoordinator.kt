package io.listensphere.mobile.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.R

/** Owns the foreground notification; no capture or transport resources. */
internal class StreamingNotificationCoordinator(private val service: Service) {
    companion object {
        private const val CHANNEL_ID = "listensphere_stream"
        private const val NOTIFICATION_ID = 1701
    }
    fun stop() = service.stopForeground(Service.STOP_FOREGROUND_REMOVE)
    fun start(kind: CaptureKind, text: String) {
        val type = when (kind) {
            CaptureKind.MICROPHONE -> ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            CaptureKind.DEVICE_PLAYBACK -> ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
        }
        if (Build.VERSION.SDK_INT >= 29) {
            service.startForeground(NOTIFICATION_ID, notification(text), type)
        } else {
            service.startForeground(NOTIFICATION_ID, notification(text))
        }
    }

    fun update(text: String) {
        service.getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, notification(text))
    }

    private fun notification(text: String): Notification = NotificationCompat.Builder(service, CHANNEL_ID)
        .setSmallIcon(R.drawable.ic_launcher_foreground)
        .setContentTitle("聆界 · ListenSphere Mobile")
        .setContentText(text)
        .setOngoing(true)
        .setOnlyAlertOnce(true)
        .build()

    fun createChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            service.getString(R.string.stream_channel_name),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = service.getString(R.string.stream_channel_description)
        }
        service.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

}
