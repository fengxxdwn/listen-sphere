package io.listensphere.mobile.service

import android.app.Service
import android.content.Intent
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.describeConnectionError
import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus
import io.listensphere.mobile.core.model.TransportMode
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.cancel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.withContext

/** Owns the job lifetime; Android Service only dispatches lifecycle events. */
internal class StreamingSession(
    private val service: Service,
    private val status: MutableStateFlow<StreamStatus>,
) : AutoCloseable {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lifetime = SessionLifetime()
    private val notification = StreamingNotificationCoordinator(service)
    private var streamJob: Job? = null

    init { notification.createChannel() }

    fun start(intent: Intent) {
        val token = lifetime.begin()
        streamJob?.cancel()
        val kind = CaptureKind.valueOf(
            intent.getStringExtra(AudioStreamingService.EXTRA_CAPTURE_KIND)
                ?: CaptureKind.MICROPHONE.name,
        )
        notification.start(kind, "正在连接聆界主控端…")
        val sessionStatus = SessionStatus(status, lifetime, token)
        val publishNotification: (String) -> Unit = { text ->
            lifetime.publish(token) { notification.update(text) }
        }
        val job = scope.launch(start = CoroutineStart.LAZY) {
            val owningJob = coroutineContext[Job]
            val capture = AudioCaptureCoordinator(service.applicationContext) { owningJob?.cancel() }
            try {
                TransportSession(service.applicationContext, sessionStatus, capture, publishNotification)
                    .run(intent, kind)
            } catch (error: Throwable) {
                if (error !is CancellationException) {
                    val mode = runCatching {
                        TransportMode.valueOf(
                            intent.getStringExtra(AudioStreamingService.EXTRA_TRANSPORT_MODE)
                                ?: TransportMode.WIFI.name,
                        )
                    }.getOrDefault(TransportMode.WIFI)
                    val presentation = describeConnectionError(mode, error)
                    sessionStatus.value = StreamStatus(
                        phase = StreamPhase.ERROR,
                        title = presentation.title,
                        detail = presentation.detail,
                    )
                    publishNotification(presentation.title)
                }
            } finally {
                try {
                    capture.close()
                } finally {
                    withContext(NonCancellable + Dispatchers.Main.immediate) {
                        lifetime.finish(token) {
                            streamJob = null
                            notification.stop()
                            service.stopSelf()
                        }
                    }
                }
            }
        }
        streamJob = job
        job.start()
    }

    fun stop() {
        lifetime.invalidate()
        streamJob?.cancel()
        streamJob = null
        status.value = StreamStatus()
        notification.stop()
        service.stopSelf()
    }

    override fun close() {
        lifetime.invalidate()
        scope.cancel()
        streamJob = null
    }
}
