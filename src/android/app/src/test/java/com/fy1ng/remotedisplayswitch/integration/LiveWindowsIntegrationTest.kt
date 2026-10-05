package com.fy1ng.remotedisplayswitch.integration

import com.fy1ng.remotedisplayswitch.core.Delivery
import com.fy1ng.remotedisplayswitch.core.DeviceProfile
import com.fy1ng.remotedisplayswitch.core.DisplayPower
import com.fy1ng.remotedisplayswitch.core.Feedback
import com.fy1ng.remotedisplayswitch.core.OperationOutcome
import com.fy1ng.remotedisplayswitch.core.OperationRecord
import com.fy1ng.remotedisplayswitch.core.PowerIntent
import com.fy1ng.remotedisplayswitch.core.Protocol
import com.fy1ng.remotedisplayswitch.core.RemoteCommand
import com.fy1ng.remotedisplayswitch.core.RemoteReply
import com.fy1ng.remotedisplayswitch.core.Route
import com.fy1ng.remotedisplayswitch.core.Router
import com.fy1ng.remotedisplayswitch.network.HttpsTransport
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.json.JSONObject
import org.junit.Test
import java.nio.file.Files
import java.nio.file.Path
import java.time.Duration
import java.time.Instant
import java.util.Base64
import java.util.UUID

class LiveWindowsIntegrationTest {
    @Test(timeout = 90_000)
    fun realHttpsPairControlDeduplicateAndRejectWrongPin() = runBlocking {
        var stage = "load fixture"
        try {
            withTimeout(60_000) {
                val fixturePath = System.getenv("RDS_ANDROID_FIXTURE")
                ensure(!fixturePath.isNullOrBlank(), "RDS_ANDROID_FIXTURE is required")
                val path = Path.of(requireNotNull(fixturePath))
                ensure(Files.isRegularFile(path) && Files.size(path) in 1..Protocol.MAX_BODY.toLong(), "Invalid fixture file")
                val fixture = JSONObject(Files.newBufferedReader(path, Charsets.UTF_8).use { it.readText() }.removePrefix("\uFEFF"))
                val bundle = Protocol.pairBundle(fixture.getJSONObject("bundle").toString(), Instant.now())
                val targetId = Protocol.uuid(fixture.getString("targetId"))
                ensure(bundle.endpoint.toHttpUrl().host in setOf("localhost", "127.0.0.1", "::1"), "Fixture must use loopback HTTPS")
                val transport = HttpsTransport()
                val router = Router(transport)
                stage = "pair"
                val remaining = Duration.between(Instant.now(), bundle.expiresAt).toMillis()
                ensure(remaining > 0, "Pairing fixture expired")
                val pairResponse = transport.pair(bundle, "Android JVM HTTPS fixture", minOf(remaining, 7_000))
                ensure(pairResponse.status in 200..299, "Pairing was rejected")
                val paired = Protocol.pairReply(pairResponse.body, bundle.deviceId)
                val device = DeviceProfile(bundle.deviceId, paired.clientId, "Loopback fixture", bundle.endpoint, bundle.certificatePin, paired.token)
                var confirmedOn = false
                try {
                    stage = "initial refresh On"
                    refreshOn(router, device, targetId)
                    stage = "set Off"
                    val off = command(device, targetId, PowerIntent.Off)
                    val first = confirmedOperation(router, device, off, DisplayPower.Off)
                    stage = "retry Off with same operationId and new requestId"
                    val retry = off.copy(requestId = UUID.randomUUID(), expiresAt = Instant.now().plusSeconds(30))
                    ensure(retry.requestId != off.requestId && retry.operationId == off.operationId, "Retry identity was not preserved")
                    val repeated = confirmedOperation(router, device, retry, DisplayPower.Off)
                    ensure(first.updatedAt == repeated.updatedAt, "Deduplication changed operation updatedAt")
                    stage = "restore On"
                    restoreOn(router, device, targetId)
                    confirmedOn = true
                    stage = "reject wrong pin"
                    val digest = Base64.getDecoder().decode(bundle.certificatePin.substring(7))
                    digest[0] = (digest[0].toInt() xor 1).toByte()
                    val wrongPin = "sha256/" + Base64.getEncoder().encodeToString(digest)
                    val rejected = router.send(device.copy(certificatePin = wrongPin), refreshCommand(device, targetId))
                    ensure(rejected is Delivery.Failure && rejected.feedback == Feedback.Security, "Wrong pin was not rejected by TLS")
                } finally {
                    if (!confirmedOn) {
                        withContext(NonCancellable) {
                            try {
                                withTimeout(15_000) { restoreOn(router, device, targetId) }
                            } catch (_: Throwable) {
                                throw AssertionError("Fixture cleanup could not confirm On; check the simulation before reuse. Details suppressed.")
                            }
                        }
                    }
                }
            }
        } catch (failure: AssertionError) {
            throw failure
        } catch (_: Exception) {
            throw AssertionError("Live Windows HTTPS fixture failed at " + stage + "; sensitive details suppressed.")
        }
    }

    private fun command(device: DeviceProfile, targetId: UUID, intent: PowerIntent) = RemoteCommand(
        UUID.randomUUID(), device.deviceId, "set", Instant.now().plusSeconds(30), targetId, intent, UUID.randomUUID(),
    )

    private fun refreshCommand(device: DeviceProfile, targetId: UUID) = RemoteCommand(
        UUID.randomUUID(), device.deviceId, "refresh", Instant.now().plusSeconds(30), targetId = targetId,
    )

    private suspend fun reply(router: Router, device: DeviceProfile, request: RemoteCommand): RemoteReply {
        val delivery = router.send(device, request)
        ensure(delivery is Delivery.Reply && delivery.route == Route.Direct, "Expected a successful direct HTTPS reply")
        val reply = (delivery as Delivery.Reply).value
        ensure(reply.success, "Windows rejected the fixture command")
        return reply
    }

    private suspend fun confirmedOperation(router: Router, device: DeviceProfile, request: RemoteCommand, power: DisplayPower): OperationRecord {
        val record = reply(router, device, request).operation
        ensure(record != null, "Missing operation record")
        requireNotNull(record)
        ensure(record.operationId == request.operationId && record.targetId == request.targetId, "Operation identity mismatch")
        ensure(record.requested == request.intent && (record.resolved == null || record.resolved == request.intent), "Operation intent mismatch")
        ensure(record.outcome == OperationOutcome.Confirmed && record.observation?.state == power, "Operation state was not confirmed")
        return record
    }

    private suspend fun refreshOn(router: Router, device: DeviceProfile, targetId: UUID) {
        val status = reply(router, device, refreshCommand(device, targetId)).status
        ensure(status != null && status.deviceId == device.deviceId && status.targetId == targetId, "Unexpected fixture target")
        ensure(status?.available == true && !status.busy && status.observation.state == DisplayPower.On, "Fixture refresh did not confirm On")
    }

    private suspend fun restoreOn(router: Router, device: DeviceProfile, targetId: UUID) {
        confirmedOperation(router, device, command(device, targetId, PowerIntent.On), DisplayPower.On)
        refreshOn(router, device, targetId)
    }

    private fun ensure(condition: Boolean, message: String) {
        if (!condition) throw AssertionError(message)
    }
}
