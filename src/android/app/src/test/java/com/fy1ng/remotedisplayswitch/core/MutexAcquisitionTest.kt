package com.fy1ng.remotedisplayswitch.core

import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test

class MutexAcquisitionTest {
    @Test fun cancellationAfterAcquisitionReleasesLockBeforeItCanBeTransferred() = runBlocking {
        val mutex = Mutex()
        val cancelledAtAcquisition = object : Mutex by mutex {
            override suspend fun lock(owner: Any?) {
                mutex.lock(owner)
                currentCoroutineContext().cancel()
            }
        }
        val cancelled = launch { cancelledAtAcquisition.lockWithin(30_000) }
        cancelled.join()
        assertTrue(cancelled.isCancelled)
        assertFalse(mutex.isLocked)
        withTimeout(1_000) { mutex.withLock { assertTrue(mutex.isLocked) } }
    }

    @Test fun timeoutWhileQueuedDoesNotReleaseAnotherRequestsLock() = runBlocking {
        val mutex = Mutex(locked = true)
        assertFalse(mutex.lockWithin(25))
        assertTrue(mutex.isLocked)
        mutex.unlock()
        withTimeout(1_000) { mutex.withLock { assertTrue(mutex.isLocked) } }
    }

    @Test fun successfulAcquisitionTransfersLockUntilCallerReleasesIt() = runBlocking {
        val mutex = Mutex()
        assertTrue(mutex.lockWithin(1_000))
        assertTrue(mutex.isLocked)
        mutex.unlock()
        assertFalse(mutex.isLocked)
    }
}
