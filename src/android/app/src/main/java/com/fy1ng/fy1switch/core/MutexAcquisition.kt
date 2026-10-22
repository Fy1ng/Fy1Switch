package com.fy1ng.fy1switch.core

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withTimeoutOrNull

internal suspend fun Mutex.lockWithin(timeoutMillis: Long): Boolean {
    var acquired = false
    var transferred = false
    try {
        val locked = withTimeoutOrNull(timeoutMillis) {
            lock()
            acquired = true
            true
        } == true
        transferred = locked
        return locked
    } finally {
        // Timeout or cancellation can discard the result after lock() has returned.
        if (acquired && !transferred) unlock()
    }
}
