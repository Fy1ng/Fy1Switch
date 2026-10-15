package com.fy1ng.remotedisplayswitch.core

import java.util.UUID

class TileClickCoordinator(private val timeoutMillis: Long = 30_000) {
    class Click internal constructor(
        val generation: Long,
        val requestId: UUID,
        val deviceId: UUID,
        val targetId: UUID?,
        val startedAt: Long,
    )

    private data class Pending(val click: Click, val authorized: Boolean = false)
    private var generation = 0L
    private var listening = false
    private var pending: Pending? = null
    val hasPending: Boolean get() = pending != null

    fun startListening() {
        generation += 1
        listening = true
        pending = null
    }

    fun stopListening() {
        generation += 1
        listening = false
        pending = null
    }

    fun queue(state: AppState, now: Long): Click? {
        val device = state.selected ?: return null
        if (!listening || !StatePolicy.refreshing(state) || StatePolicy.unresolved(device.operation)) return null
        val request = requireNotNull(state.request)
        pending?.click?.takeIf { it.requestId == request.id && it.deviceId == device.profile.deviceId }?.let { return it }
        val click = Click(generation, request.id, device.profile.deviceId, device.status?.targetId, now)
        pending = Pending(click)
        return click
    }

    fun authorize(click: Click): Boolean {
        val current = pending ?: return false
        if (!listening || click.generation != generation || current.click != click) return false
        pending = current.copy(authorized = true)
        return true
    }

    fun takeReady(state: AppState, now: Long, locked: Boolean): UUID? {
        val current = pending ?: return null
        val click = current.click
        val elapsed = now - click.startedAt
        val device = state.selected
        val request = state.request
        if (!listening || elapsed < 0 || elapsed >= timeoutMillis || !state.loaded || state.storageFailed ||
            device == null || request == null || device.profile.deviceId != click.deviceId || request.id != click.requestId ||
            request.action != RepositoryAction.Refresh || request.deviceId != click.deviceId ||
            StatePolicy.unresolved(device.operation) || (locked && current.authorized)) {
            pending = null
            return null
        }
        if (state.busy) return null
        val status = device.status
        if (request.result != Feedback.Updated || !StatePolicy.canSet(state, PowerIntent.Toggle) ||
            status == null || status.deviceId != click.deviceId ||
            (click.targetId != null && status.targetId != click.targetId)) {
            pending = null
            return null
        }
        // A successful refresh may finish while the initial unlock prompt is still open.
        if (!current.authorized || locked) return null
        pending = null
        return click.deviceId
    }
}
