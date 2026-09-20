package io.listensphere.mobile.service

import io.listensphere.mobile.core.model.StreamStatus
import kotlinx.coroutines.flow.MutableStateFlow

/** Serializes status/notification publication with replacement and explicit stop. */
internal class SessionLifetime {
    private var generation = 0L
    private var active: Long? = null

    @Synchronized fun begin(): Long = (++generation).also { active = it }
    @Synchronized fun invalidate() { active = null }
    @Synchronized fun publish(token: Long, action: () -> Unit) {
        if (active == token) action()
    }
    @Synchronized fun finish(token: Long, action: () -> Unit) {
        if (active == token) {
            active = null
            action()
        }
    }
}

internal class SessionStatus(
    private val status: MutableStateFlow<StreamStatus>,
    private val lifetime: SessionLifetime,
    private val token: Long,
) {
    var value: StreamStatus
        get() = status.value
        set(value) = lifetime.publish(token) { status.value = value }
}
