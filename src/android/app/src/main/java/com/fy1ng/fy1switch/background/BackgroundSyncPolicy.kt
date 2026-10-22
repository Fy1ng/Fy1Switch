package com.fy1ng.fy1switch.background

import com.fy1ng.fy1switch.core.Feedback

internal object BackgroundSyncPolicy {
    const val INTERVAL_MILLIS = 10_000L

    fun mustStop(feedback: Feedback) = feedback in setOf(
        Feedback.Security, Feedback.Unauthorized, Feedback.Protocol, Feedback.Storage,
        Feedback.InvalidInput, Feedback.Expired, Feedback.DeviceMissing,
    )

    fun delayMillis(feedback: Feedback, consecutiveFailures: Int): Long = when (feedback) {
        Feedback.Updated, Feedback.Busy -> INTERVAL_MILLIS
        else -> (INTERVAL_MILLIS * (1L shl (consecutiveFailures - 1).coerceIn(0, 3))).coerceAtMost(60_000L)
    }
}
