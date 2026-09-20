package io.listensphere.mobile.service

import java.util.concurrent.atomic.AtomicBoolean

/** A projection callback can cancel only its owning session, before release. */
internal class CapturePermissionLease(private val cancelOwner: () -> Unit) {
    private val active = AtomicBoolean(true)
    fun revoke() { if (active.compareAndSet(true, false)) cancelOwner() }
    fun release() { active.set(false) }
}
