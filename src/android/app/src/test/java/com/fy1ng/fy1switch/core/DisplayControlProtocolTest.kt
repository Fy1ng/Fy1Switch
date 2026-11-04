package com.fy1ng.fy1switch.core

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class DisplayControlProtocolTest {
    private fun command(name: String = "controls") = RemoteCommand(REQUEST, DEVICE, name, NOW.plusSeconds(30), TARGET,
        operationId = if (name == "controls") null else OPERATION,
        control = if (name == "set-control") DisplayControl.Brightness else null, value = if (name == "set-control") 70 else null)

    @Test fun newEnvelopesKeepPowerWireUnchangedAndDisallowMixedIntent() {
        val legacy = JSONObject(Protocol.command(request()))
        assertFalse(legacy.has("control")); assertFalse(legacy.has("value"))
        val write = JSONObject(Protocol.command(command("set-control")))
        assertEquals("Brightness", write.getString("control")); assertEquals(70, write.getInt("value")); assertFalse(write.has("intent"))
        for (request in listOf(command().copy(targetId = null), command().copy(operationId = OPERATION),
            command("set-control").copy(intent = PowerIntent.On), command("set-control").copy(value = 101),
            command("control-operation").copy(control = DisplayControl.Contrast), request().copy(control = DisplayControl.Brightness))) {
            assertThrows(IllegalArgumentException::class.java) { Protocol.command(request) }
        }
    }
    @Test fun capabilitiesAreBoundedAndMatchRequestedTarget() {
        val request = command()
        val parsed = Protocol.reply(controlReply(Protocol.command(request), controlsJson()), request).controls!!
        assertEquals(TARGET, parsed.targetId); assertEquals(6, parsed.controls.size)
        assertEquals(16, parsed.vcpCodes.single().code)
        assertThrows(IllegalArgumentException::class.java) { Protocol.reply(controlReply(Protocol.command(request), controlsJson(OTHER)), request) }
        val invalid = controlsJson(); invalid.getJSONArray("controls").getJSONObject(0).put("value", 101)
        assertThrows(IllegalArgumentException::class.java) { DisplayControlProtocol.controls(invalid) }
        val duplicate = controlsJson(); duplicate.getJSONArray("controls").getJSONObject(1).put("control", "Brightness")
        assertThrows(IllegalArgumentException::class.java) { DisplayControlProtocol.controls(duplicate) }
    }
    @Test fun operationIdentityControlValueAndConfirmationAreValidated() {
        val request = command("set-control")
        for (operation in listOf(controlOperation().copy(operationId = OTHER), controlOperation().copy(targetId = OTHER),
            controlOperation(control = DisplayControl.Contrast), controlOperation(value = 71),
            controlOperation().copy(result = ControlWriteResult(true, null, true, null)),
            controlOperation().copy(result = ControlWriteResult(false, 70, true, null)))) {
            assertThrows(IllegalArgumentException::class.java) { Protocol.reply(controlReply(Protocol.command(request), operation = operation), request) }
        }
    }
    @Test fun confirmedContinuousReadbackMayBeQuantizedButDiscreteReadbackMustMatch() {
        val quantized = controlOperation().copy(result = ControlWriteResult(true, 69, true, null))
        assertEquals(69, DisplayControlProtocol.operation(DisplayControlProtocol.operationJson(quantized)).result!!.value)
        val discrete = controlOperation(control = DisplayControl.InputSource, value = 17).copy(result = ControlWriteResult(true, 18, true, null))
        assertThrows(IllegalArgumentException::class.java) { DisplayControlProtocol.operation(DisplayControlProtocol.operationJson(discrete)) }
    }
    @Test fun oldRepliesIgnoreAdditiveParameterFieldsAndOldStoresDefaultToNoControls() {
        val request = request(command = "status")
        val json = JSONObject(replyJson(Protocol.command(request), status())).put("controls", "future-shape").put("controlOperation", "future-shape")
        assertNull(Protocol.reply(json.toString(), request).controls)
        val legacy = JSONObject(StateCodec.encode(activeState()))
        legacy.getJSONArray("devices").getJSONObject(0).remove("controlOperation")
        val restored = StateCodec.decode(legacy.toString())
        assertNull(restored.selected!!.controls); assertNull(restored.selected!!.controlOperation)
        assertEquals(profile(), restored.selected!!.profile)
    }
    @Test fun pendingParameterOperationRestoresUnknownWithoutPersistingCapabilitiesOrFreshness() {
        val initial = activeState().let { it.copy(devices = it.devices.map { device -> device.copy(
            controls = DisplayControlProtocol.controls(controlsJson()), controlsReadAtMillis = 123,
            controlOperation = controlOperation(OperationOutcome.Pending)) }) }
        val restored = StateCodec.decode(StateCodec.encode(initial)).selected!!
        assertEquals(OperationOutcome.Unknown, restored.controlOperation!!.outcome)
        assertEquals(OPERATION, restored.controlOperation.operationId)
        assertNull(restored.controls); assertNull(restored.controlsReadAtMillis); assertTrue(restored.controlsRequireRefresh)
        assertNull(restored.operation)
    }
    @Test fun uiPolicyUsesReportedCapabilityAndRealDiscreteLabels() {
        val device = activeState().selected!!.copy(controls = DisplayControlProtocol.controls(controlsJson()))
        val state = activeState().copy(devices = listOf(device))
        assertTrue(DisplayControlPolicy.canApply(state, device, DisplayControl.Brightness, 80, true))
        assertFalse(DisplayControlPolicy.canApply(state, device, DisplayControl.Volume, 80, true))
        assertFalse(DisplayControlPolicy.canApply(state, device, DisplayControl.InputSource, 3, true))
        assertFalse(DisplayControlPolicy.canApply(state, device, DisplayControl.Brightness, 80, false))
        assertTrue(DisplayControlPolicy.valueLabel(DisplayControl.Rotation, 3).contains("270"))
        assertEquals("6500K", DisplayControlPolicy.valueLabel(DisplayControl.ColorTemperature, 5))
        assertEquals("HDMI-1", DisplayControlPolicy.valueLabel(DisplayControl.InputSource, 17))
    }
}
