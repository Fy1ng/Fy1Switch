package com.fy1ng.remotedisplayswitch.pairing

class PairingLimits(private val now: () -> Long = { System.nanoTime() / 1_000_000 }) {
    private val admissions = ArrayDeque<Long>()
    private var active = false
    @Synchronized fun acquire(): Boolean {
        val time = now()
        while (admissions.isNotEmpty() && time - admissions.first() >= 600_000) admissions.removeFirst()
        if (active || admissions.size >= 10) return false
        admissions.addLast(time)
        active = true
        return true
    }
    @Synchronized fun release() { active = false }
    companion object { val process = PairingLimits() }
}

class CodeSession(private val now: () -> Long = { System.nanoTime() / 1_000_000 }) {
    val id = ShortCodeProtocol.session()
    val code = ShortCodeProtocol.newCode()
    private val created = now()
    private var attempts = 0
    private var consumed = false
    @Synchronized fun remainingSeconds(): Long = if (consumed || attempts >= 5) 0 else maxOf(0, (300_000 - (now() - created) + 999) / 1000)
    @Synchronized fun admit(limits: PairingLimits): Boolean {
        if (remainingSeconds() == 0L || !limits.acquire()) return false
        attempts++
        return true
    }
    @Synchronized fun consume() {
        require(!consumed && now() - created < 300_000)
        consumed = true
    }
}
