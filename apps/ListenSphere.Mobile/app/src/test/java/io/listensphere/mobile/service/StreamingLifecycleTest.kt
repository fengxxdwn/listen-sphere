package io.listensphere.mobile.service

import io.listensphere.mobile.core.model.StreamPhase
import io.listensphere.mobile.core.model.StreamStatus
import io.listensphere.mobile.core.network.ControllerDisconnectedException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import org.junit.Assert.*
import org.junit.Test
import java.io.IOException

class StreamingLifecycleTest {
    @Test fun `replacement rejects old status notification and completion`() {
        val lifetime = SessionLifetime()
        val state = MutableStateFlow(StreamStatus())
        val old = lifetime.begin()
        val oldStatus = SessionStatus(state, lifetime, old)
        val current = lifetime.begin()
        val status = SessionStatus(state, lifetime, current)
        status.value = StreamStatus(phase = StreamPhase.STREAMING)
        oldStatus.value = StreamStatus(phase = StreamPhase.ERROR)
        lifetime.publish(old) { fail("old notification") }
        lifetime.finish(old) { fail("old completion stopped new session") }
        assertEquals(StreamPhase.STREAMING, state.value.phase)
        var completed = 0
        lifetime.finish(current) { completed++ }
        lifetime.finish(current) { completed++ }
        assertEquals(1, completed)
    }

    @Test fun `explicit stop rejects late audio and reconnect status`() {
        val lifetime = SessionLifetime()
        val state = MutableStateFlow(StreamStatus())
        val token = lifetime.begin()
        val status = SessionStatus(state, lifetime, token)
        lifetime.invalidate()
        status.value = StreamStatus(phase = StreamPhase.CONNECTING)
        lifetime.finish(token) { fail("stopped session completed twice") }
        assertEquals(StreamPhase.IDLE, state.value.phase)
    }

    @Test fun `service recreation has no active session until explicit start`() {
        val previous = SessionLifetime()
        val token = previous.begin()
        previous.invalidate()
        val recreated = SessionLifetime()
        recreated.publish(token) { fail("recreated service resumed capture") }
        var started = false
        recreated.publish(recreated.begin()) { started = true }
        assertTrue(started)
    }

    @Test fun `projection revocation cancels only owning job once`() {
        val oldJob = Job()
        val newJob = Job()
        var callbacks = 0
        val lease = CapturePermissionLease { callbacks++; oldJob.cancel() }
        lease.revoke()
        lease.revoke()
        assertTrue(oldJob.isCancelled)
        assertTrue(newJob.isActive)
        assertEquals(1, callbacks)
        newJob.cancel()
    }

    @Test fun `normal release suppresses delayed framework callback`() {
        val lease = CapturePermissionLease { fail("release invoked permission cancellation") }
        lease.release()
        lease.revoke()
        lease.release()
    }

    @Test fun `controller disconnect and user cancellation never retry`() {
        assertFalse(TransportRetryPolicy.isRetryable(ControllerDisconnectedException("user disconnected")))
        assertFalse(TransportRetryPolicy.isRetryable(CancellationException("stopped")))
    }

    @Test fun `network faults still retry but permission faults do not`() {
        assertTrue(TransportRetryPolicy.isRetryable(IOException("network lost")))
        assertTrue(TransportRetryPolicy.isRetryable(IllegalStateException(IOException("wrapped"))))
        assertFalse(TransportRetryPolicy.isRetryable(SecurityException("permission revoked")))
    }
}
