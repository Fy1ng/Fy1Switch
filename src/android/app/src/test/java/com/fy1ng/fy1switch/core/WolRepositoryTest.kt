package com.fy1ng.fy1switch.core

import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.net.SocketTimeoutException

class WolRepositoryTest {
    @Test fun pendingIsDurableBeforeSendingAndSuccessDoesNotChangeDisplayOrHostState() = runBlocking {
        val store = MemoryStore(wolState())
        val transport = FakeWolTransport()
        val normal = transport.handler
        transport.handler = { call ->
            assertEquals(WolStatus.Pending, store.value.selected!!.wolOperation!!.status)
            assertEquals(store.value.selected!!.wolOperation!!.operationId.toString(), JSONObject(call.body!!).getString("operationId"))
            normal(call)
        }
        val repo = DeviceRepository(store, FakeTransport(), MutableClock(), wolTransport = transport)
        assertTrue(repo.wol.wake(DEVICE))
        assertEquals(WolStatus.PacketSent, store.value.selected!!.wolOperation!!.status)
        assertFalse(repo.hasFreshHost(DEVICE)); assertFalse(store.value.selected!!.connected)
        assertNull(store.value.selected!!.operation)
        assertEquals(WOL_TOKEN, transport.calls.single().token)
    }
    @Test fun failedStorageNeverSendsWake() = runBlocking {
        val transport = FakeWolTransport()
        val repo = DeviceRepository(MemoryStore(wolState()).apply { failWrite = true }, FakeTransport(), MutableClock(), wolTransport = transport)
        assertFalse(repo.wol.wake(DEVICE)); assertTrue(transport.calls.isEmpty())
        assertTrue(repo.state.value.storageFailed)
    }
    @Test fun lostResponseAndProcessRecoveryOnlyQuerySameOperation() = runBlocking {
        val store = MemoryStore(wolState())
        val transport = FakeWolTransport().apply { handler = { throw SocketTimeoutException() } }
        val repo = DeviceRepository(store, FakeTransport(), MutableClock(), wolTransport = transport)
        assertFalse(repo.wol.wake(DEVICE))
        val operation = store.value.selected!!.wolOperation!!
        assertEquals(WolStatus.Unknown, operation.status)
        val restored = MemoryStore(StateCodec.decode(StateCodec.encode(store.value)))
        val next = FakeWolTransport()
        val restarted = DeviceRepository(restored, FakeTransport(), MutableClock(), wolTransport = next)
        assertTrue(restarted.wol.wake(DEVICE))
        assertEquals("GET", next.calls.single().method)
        assertTrue(next.calls.single().path.endsWith(operation.operationId.toString()))
        assertEquals(operation.operationId, restored.value.selected!!.wolOperation!!.operationId)
    }
    @Test fun concurrentAppTileRequestsAndFastRepeatSendOnlyOnce() = runBlocking {
        val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        val transport = FakeWolTransport(); val normal = transport.handler
        transport.handler = { entered.complete(Unit); release.await(); normal(it) }
        val repo = DeviceRepository(MemoryStore(wolState()), FakeTransport(), MutableClock(), wolTransport = transport)
        val first = async { repo.wol.wake(DEVICE) }
        entered.await()
        assertFalse(repo.wol.wake(DEVICE))
        release.complete(Unit); assertTrue(first.await())
        assertFalse(repo.wol.wake(DEVICE)); assertEquals(1, transport.calls.size)
    }
    @Test fun cancelAfterSendPersistsUnknownAndDoesNotRetry() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val store = MemoryStore(wolState())
        val transport = FakeWolTransport().apply { handler = { entered.complete(Unit); awaitCancellation() } }
        val repo = DeviceRepository(store, FakeTransport(), MutableClock(), wolTransport = transport)
        val job = launch { repo.wol.wake(DEVICE) }; entered.await(); job.cancelAndJoin()
        assertEquals(WolStatus.Unknown, store.value.selected!!.wolOperation!!.status)
        assertFalse(repo.wol.ui.value.busy); assertEquals(1, transport.calls.size)
    }
    @Test fun registrationDoesNotUseWindowsCredentialsAndStoresOneGatewayAuthorization() = runBlocking {
        val store = MemoryStore(activeState())
        val transport = FakeWolTransport()
        val repo = DeviceRepository(store, FakeTransport(), MutableClock(), wolTransport = transport)
        assertTrue(repo.wol.register(DEVICE, WOL_ENDPOINT, "12345678", "Phone"))
        assertNull(transport.calls.single().token)
        assertFalse(transport.calls.single().body!!.contains("fixture-device-token"))
        assertEquals(wolAuth().copy(devices = listOf(WolTarget(DEVICE, "Desktop"))), store.value.wolAuthorizations.single())
        assertTrue(repo.wol.configure(DEVICE, WOL_ENDPOINT, DEVICE, true))
        assertFalse(repo.wol.register(DEVICE, WOL_ENDPOINT, "12345678", "Phone"))
        assertEquals(1, transport.calls.size)
    }
    @Test fun grantCannotBeBoundToAnotherWindowsIdentity() = runBlocking {
        val repo = DeviceRepository(MemoryStore(wolState()), FakeTransport(), MutableClock(), wolTransport = FakeWolTransport())
        assertFalse(repo.wol.configure(DEVICE, WOL_ENDPOINT, OTHER, true))
        assertEquals(DEVICE, repo.state.value.selected!!.profile.wol!!.targetId)
    }
    @Test fun revokeClearsAllBindingsAtGatewayButKeepsDisplayPairings() = runBlocking {
        val state = wolState().let { it.copy(devices = it.devices + DeviceState(profile(deviceId = OTHER).copy(wol = wolBinding(OTHER)))) }
        val store = MemoryStore(state); val transport = FakeWolTransport()
        val repo = DeviceRepository(store, FakeTransport(), MutableClock(), wolTransport = transport)
        assertTrue(repo.wol.clear(DEVICE, WOL_ENDPOINT, true))
        assertEquals("DELETE", transport.calls.single().method)
        assertTrue(store.value.wolAuthorizations.isEmpty()); assertTrue(store.value.devices.all { it.profile.wol == null })
        assertEquals(state.devices.map { it.profile.deviceToken }, store.value.devices.map { it.profile.deviceToken })
    }
    @Test fun localClearDoesNotClaimOrSendRemoteRevocation() = runBlocking {
        val transport = FakeWolTransport()
        val repo = DeviceRepository(MemoryStore(wolState()), FakeTransport(), MutableClock(), wolTransport = transport)
        assertTrue(repo.wol.clear(DEVICE, WOL_ENDPOINT, false)); assertTrue(transport.calls.isEmpty())
        assertTrue(repo.wol.ui.value.message!!.contains("服务端权限仍存在"))
    }
    @Test fun explicitSshOrTunnelFailureStaysSeparateFromPacketSuccess() = runBlocking {
        for ((httpStatus, code, expected) in listOf(Triple(502, "ssh_auth_failed", WolStatus.Unknown),
            Triple(503, "tunnel_unavailable", WolStatus.Unknown), Triple(403, "device_forbidden", WolStatus.Failed),
            Triple(429, "rate_limited", WolStatus.Failed))) {
            val transport = FakeWolTransport().apply { handler = { HttpResult(httpStatus, JSONObject().put("version", 1).put("errorCode", code).toString()) } }
            val repo = DeviceRepository(MemoryStore(wolState()), FakeTransport(), MutableClock(), wolTransport = transport)
            assertFalse(repo.wol.wake(DEVICE))
            assertEquals(expected, repo.state.value.selected!!.wolOperation!!.status)
            assertEquals(code, repo.state.value.selected!!.wolOperation!!.errorCode)
            assertFalse(repo.hasFreshHost(DEVICE))
        }
    }

    @Test fun gatewayResultPersistenceFailurePreservesUnknownAndNextClickQueriesOriginalOperation() = runBlocking {
        val store = MemoryStore(wolState())
        val transport = FakeWolTransport(); val normal = transport.handler
        transport.handler = { call -> if (call.path.endsWith("/wake")) HttpResult(503,
            JSONObject().put("version", 1).put("errorCode", "storage_unavailable").toString()) else normal(call) }
        val repo = DeviceRepository(store, FakeTransport(), MutableClock(), wolTransport = transport)
        assertFalse(repo.wol.wake(DEVICE))
        val operation = store.value.selected!!.wolOperation!!
        assertEquals(WolStatus.Unknown, operation.status)
        assertEquals("storage_unavailable", operation.errorCode)
        assertTrue(repo.wol.ui.value.message!!.contains("发送结果未知"))
        assertTrue(WolPolicy.result(operation, false, NOW).contains("Gateway 无法保存执行结果"))
        assertTrue(repo.wol.wake(DEVICE))
        assertEquals(listOf("POST", "GET"), transport.calls.map { it.method })
        assertTrue(transport.calls.last().path.endsWith("/operations/${operation.operationId}"))
        assertEquals(operation.operationId, store.value.selected!!.wolOperation!!.operationId)
    }

    @Test fun proxyErrorsAndMalformedRejectionsRemainUnknownAcrossRestartAndNeverResend() = runBlocking {
        val cases = listOf(HttpResult(502, "<html>Bad Gateway</html>"), HttpResult(504, ""),
            HttpResult(400, "{\"version\":1}"), HttpResult(403, "{\"version\":1,\"errorCode\":\"new_error\"}"),
            HttpResult(503, "{\"version\":1,\"errorCode\":\"device_forbidden\"}"),
            HttpResult(409, "{\"version\":1,\"errorCode\":\"request_replayed\"}"),
            HttpResult(409, "{\"version\":1,\"errorCode\":\"operation_conflict\"}"))
        for (response in cases) {
            val store = MemoryStore(wolState())
            val transport = FakeWolTransport().apply { handler = { response } }
            val repo = DeviceRepository(store, FakeTransport(), MutableClock(), wolTransport = transport)
            assertFalse(repo.wol.wake(DEVICE))
            val original = store.value.selected!!.wolOperation!!
            assertEquals(WolStatus.Unknown, original.status)
            val next = FakeWolTransport()
            val restored = DeviceRepository(MemoryStore(StateCodec.decode(StateCodec.encode(store.value))), FakeTransport(), MutableClock(), wolTransport = next)
            assertTrue(restored.wol.wake(DEVICE))
            assertEquals("GET", next.calls.single().method)
            assertTrue(next.calls.single().path.endsWith("/operations/${original.operationId}"))
        }
    }

    @Test fun onlyKnownPreExecutionRejectionsBecomeFailedWhileAcceptedResultsKeepTheirStatus() = runBlocking {
        for ((httpStatus, code) in listOf(400 to "invalid_request", 400 to "request_expired", 401 to "unauthorized",
            403 to "device_forbidden", 409 to "device_busy", 429 to "rate_limited", 429 to "device_cooldown")) {
            val transport = FakeWolTransport().apply { handler = { HttpResult(httpStatus,
                JSONObject().put("version", 1).put("errorCode", code).toString()) } }
            val repo = DeviceRepository(MemoryStore(wolState()), FakeTransport(), MutableClock(), wolTransport = transport)
            assertFalse(repo.wol.wake(DEVICE))
            assertEquals(WolStatus.Failed, repo.state.value.selected!!.wolOperation!!.status)
        }
        for (status in listOf(WolStatus.Failed, WolStatus.Unknown)) {
            val transport = FakeWolTransport().apply { handler = { call -> HttpResult(200, wolReply(wolOperation(status).copy(
                operationId = Protocol.uuid(JSONObject(call.body!!).getString("operationId")), errorCode = "execution_timeout"))) } }
            val repo = DeviceRepository(MemoryStore(wolState()), FakeTransport(), MutableClock(), wolTransport = transport)
            assertTrue(repo.wol.wake(DEVICE))
            assertEquals(status, repo.state.value.selected!!.wolOperation!!.status)
        }
    }
    @Test fun checkDoesNotSendWakeOrCreateOperation() = runBlocking {
        val transport = FakeWolTransport()
        val repo = DeviceRepository(MemoryStore(wolState()), FakeTransport(), MutableClock(), wolTransport = transport)
        assertTrue(repo.wol.check(DEVICE)); assertTrue(transport.calls.single().path.endsWith("/check"))
        assertNull(repo.state.value.selected!!.wolOperation)
    }
    @Test fun hostOnlineWithoutAvailableMonitorIsDistinctFromDisplayPower() = runBlocking {
        val display = FakeTransport().apply { handler = { HttpResult(200, replyJson(it.body, status().copy(available = false))) } }
        val repo = DeviceRepository(MemoryStore(wolState()), display, MutableClock(), wolTransport = FakeWolTransport())
        assertEquals(Feedback.Updated, repo.refresh(DEVICE, false))
        assertTrue(repo.hasFreshHost(DEVICE)); assertFalse(repo.hasFreshPower(DEVICE))
    }
    @Test fun expiredObservationAndHiddenViewNeverIssueNetworkCalls() = runBlocking {
        val initial = wolState().let { it.copy(devices = it.devices.map { d -> d.copy(wolOperation = wolOperation()) }) }
        val display = FakeTransport(); val transport = FakeWolTransport()
        val repo = DeviceRepository(MemoryStore(initial), display, MutableClock(NOW.plusSeconds(91)), wolTransport = transport)
        repo.initialize(); repo.wol.observeHost(DEVICE, OPERATION) { true }
        assertTrue(display.attempts.isEmpty()); assertTrue(transport.calls.isEmpty())
        val fresh = DeviceRepository(MemoryStore(initial), display, MutableClock(), wolTransport = transport)
        fresh.initialize(); fresh.wol.observeHost(DEVICE, OPERATION) { false }
        assertTrue(display.attempts.isEmpty())
    }
    @Test fun windowsRepairRetainsWolBindingAndOperation() = runBlocking {
        val initial = wolState().let { it.copy(devices = it.devices.map { d -> d.copy(wolOperation = wolOperation()) }) }
        val repo = DeviceRepository(MemoryStore(initial), FakeTransport(), MutableClock(), wolTransport = FakeWolTransport())
        repo.pair(bundleJson(), "Phone")
        assertEquals(wolBinding(), repo.state.value.selected!!.profile.wol)
        assertEquals(wolOperation(), repo.state.value.selected!!.wolOperation)
        assertEquals(listOf(wolAuth()), repo.state.value.wolAuthorizations)
    }

    @Test fun packetSentButComputerOfflineStopsAtDeadlineWithoutRepeatingWake() = runBlocking {
        val initial = wolState().let { it.copy(devices = it.devices.map { d -> d.copy(wolOperation = wolOperation()) }) }
        val clock = MutableClock(); var elapsed = 0L
        val display = FakeTransport().apply { handler = { elapsed += 15_000; clock.now = clock.now.plusSeconds(15); throw SocketTimeoutException() } }
        val transport = FakeWolTransport()
        val repo = DeviceRepository(MemoryStore(initial), display, clock, monotonicMillis = { elapsed }, wolTransport = transport)
        repo.initialize()
        val watcher = WolRepository(repo, transport, clock, { elapsed }, { millis -> elapsed += millis; clock.now = clock.now.plusMillis(millis) })
        watcher.observeHost(DEVICE, OPERATION) { true }
        assertTrue(elapsed in 90_000..110_000)
        assertTrue(display.attempts.size in 1..6)
        assertTrue(transport.calls.isEmpty())
        assertEquals(WolStatus.PacketSent, repo.state.value.selected!!.wolOperation!!.status)
        assertFalse(repo.hasFreshHost(DEVICE))
    }

    @Test fun switchingVisibleTargetStopsPollingWithoutChangingWakeTarget() = runBlocking {
        val initial = wolState().let { it.copy(devices = it.devices.map { d -> d.copy(wolOperation = wolOperation()) } +
            DeviceState(profile(deviceId = OTHER).copy(wol = wolBinding(OTHER)))) }
        val clock = MutableClock(); var elapsed = 0L
        val display = FakeTransport().apply { handler = { throw SocketTimeoutException() } }
        val repo = DeviceRepository(MemoryStore(initial), display, clock, monotonicMillis = { elapsed }, wolTransport = FakeWolTransport())
        repo.initialize()
        val watcher = WolRepository(repo, FakeWolTransport(), clock, { elapsed }, { elapsed += it; repo.select(OTHER) })
        watcher.observeHost(DEVICE, OPERATION) { repo.state.value.defaultDeviceId == DEVICE }
        assertEquals(1, display.attempts.size)
        assertEquals(DEVICE, display.attempts.single().device.deviceId)
        assertEquals(OTHER, repo.state.value.defaultDeviceId)
    }
}
