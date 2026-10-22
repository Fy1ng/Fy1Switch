package com.fy1ng.remotedisplayswitch.background

import com.fy1ng.remotedisplayswitch.core.Feedback
import org.junit.Assert.*
import org.junit.Test

class BackgroundSyncPolicyTest {
    @Test fun normalReadAndUserPreemptionKeepTenSecondCadence() {
        for (feedback in listOf(Feedback.Updated, Feedback.Busy))
            assertEquals(10_000L, BackgroundSyncPolicy.delayMillis(feedback, 0))
    }

    @Test fun networkFailuresBackOffAndNeverRetryMoreOftenThanNormal() {
        assertEquals(listOf(10_000L, 20_000L, 40_000L, 60_000L, 60_000L),
            (1..5).map { BackgroundSyncPolicy.delayMillis(Feedback.Offline, it) })
    }

    @Test fun identityAuthorizationProtocolAndStorageFailuresStopTheLoop() {
        for (feedback in listOf(Feedback.Security, Feedback.Unauthorized, Feedback.Protocol, Feedback.Storage))
            assertTrue(BackgroundSyncPolicy.mustStop(feedback))
        for (feedback in listOf(Feedback.Updated, Feedback.Busy, Feedback.Offline, Feedback.Failed))
            assertFalse(BackgroundSyncPolicy.mustStop(feedback))
    }
}
