package io.listensphere.mobile.service

import io.listensphere.mobile.core.network.ControllerDisconnectedException
import java.io.IOException
import kotlinx.coroutines.CancellationException

internal object TransportRetryPolicy {
    fun isRetryable(error: Throwable): Boolean {
        if (error is CancellationException || error is ControllerDisconnectedException) return false
        return generateSequence(error) { it.cause }.any { it is IOException }
    }
}
