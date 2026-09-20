package io.listensphere.mobile.service

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import io.listensphere.mobile.core.audio.FloatAudioCapture
import io.listensphere.mobile.core.audio.MicrophoneAudioCapture
import io.listensphere.mobile.core.audio.PlaybackAudioCapture
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_PROJECTION_DATA
import io.listensphere.mobile.service.AudioStreamingService.Companion.EXTRA_PROJECTION_RESULT

/** One instance per streaming attempt; a revoked projection cancels only its owning session. */
internal class AudioCaptureCoordinator(
    private val context: Context,
    private val onPermissionRevoked: () -> Unit,
) : AutoCloseable {
    private var projection: MediaProjection? = null
    private val permission = CapturePermissionLease(onPermissionRevoked)
    private var callback: MediaProjection.Callback? = null

    override fun close() {
        permission.release()
        val previous = projection
        projection = null
        callback?.let { previous?.unregisterCallback(it) }
        callback = null
        previous?.stop()
    }
    fun create(kind: CaptureKind, intent: Intent): FloatAudioCapture = when (kind) {
        CaptureKind.MICROPHONE -> MicrophoneAudioCapture()
        CaptureKind.DEVICE_PLAYBACK -> {
            val resultCode = intent.getIntExtra(EXTRA_PROJECTION_RESULT, Int.MIN_VALUE)
            val data = if (Build.VERSION.SDK_INT >= 33) {
                intent.getParcelableExtra(EXTRA_PROJECTION_DATA, Intent::class.java)
            } else {
                @Suppress("DEPRECATION")
                intent.getParcelableExtra(EXTRA_PROJECTION_DATA)
            } ?: error("缺少系统音频捕获授权。")
            val manager = context.getSystemService(MediaProjectionManager::class.java)
            projection = manager.getMediaProjection(resultCode, data).apply {
                registerCallback(
                    object : MediaProjection.Callback() {
                        override fun onStop() {
                            permission.revoke()
                        }
                    }.also { callback = it },
                    Handler(Looper.getMainLooper()),
                )
            }
            PlaybackAudioCapture(requireNotNull(projection))
        }
    }

}
