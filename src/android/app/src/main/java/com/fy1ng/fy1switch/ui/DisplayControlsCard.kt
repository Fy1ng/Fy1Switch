package com.fy1ng.fy1switch.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import com.fy1ng.fy1switch.core.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.math.roundToInt

@Composable
internal fun DisplayControlsCard(device: DeviceState, state: AppState, repository: DeviceRepository, scope: CoroutineScope) {
    var expanded by rememberSaveable { mutableStateOf(false) }
    var showVcp by rememberSaveable { mutableStateOf(false) }
    var tick by remember { mutableLongStateOf(0) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val deviceId = device.profile.deviceId
    LaunchedEffect(expanded, deviceId, device.status?.targetId, lifecycle) {
        if (expanded) lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            repository.recoverDisplayControls(deviceId)
            repository.state.map { current -> current.request?.takeIf {
                !current.busy && it.deviceId == deviceId && it.action == RepositoryAction.SetControl &&
                    it.result in setOf(Feedback.Unknown, Feedback.Pending, Feedback.AcceptedUnconfirmed)
            }?.id }.filterNotNull().distinctUntilChanged().collect { repository.queryDisplayControl(deviceId) }
        }
    }
    LaunchedEffect(expanded, lifecycle) {
        if (expanded) lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            while (true) { delay(1_000); tick += 1 }
        }
    }
    val fresh = remember(state, tick) { repository.hasFreshControls(deviceId) }
    OutlinedCard(Modifier.fillMaxWidth(), shape = RoundedCornerShape(20.dp)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            TextButton(onClick = { expanded = !expanded }, modifier = Modifier.fillMaxWidth()) {
                Text("显示器参数", Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
                AppIcon(if (expanded) AppIconType.Up else AppIconType.Down)
            }
            if (expanded) {
                Text("只显示当前绑定显示器报告的能力. 调整数值后点应用, 不会随拖动持续发送.", style = MaterialTheme.typography.bodySmall)
                if (state.busy && state.request?.deviceId == deviceId && state.request.action in
                    setOf(RepositoryAction.Controls, RepositoryAction.SetControl, RepositoryAction.ControlQuery)) LinearProgressIndicator(Modifier.fillMaxWidth())
                Text(device.controlsFeedback.text, style = MaterialTheme.typography.bodySmall)
                val operation = device.controlOperation
                if (operation != null) {
                    Text("最近参数操作: " + DisplayControlPolicy.label(operation.control) + " = " +
                        DisplayControlPolicy.valueLabel(operation.control, operation.requestedValue), style = MaterialTheme.typography.bodySmall)
                    Text(when (operation.outcome) {
                        OperationOutcome.Confirmed -> "读回已确认: " + operation.result?.value?.let { DisplayControlPolicy.valueLabel(operation.control, it) }
                        OperationOutcome.AcceptedUnconfirmed -> "写入已接受, 读回未确认"
                        OperationOutcome.Pending -> "仍在处理, 请查询原请求"
                        OperationOutcome.Unknown -> "结果未知, 不会自动重新应用"
                        OperationOutcome.Failed -> "操作未成功"
                    }, style = MaterialTheme.typography.bodySmall)
                    TextButton(onClick = { scope.launch { repository.queryDisplayControl(deviceId) } }, enabled = !state.busy) {
                        Text("查询最近参数操作")
                    }
                }
                TextButton(onClick = { scope.launch { repository.readDisplayControls(deviceId) } }, enabled = !state.busy) {
                    AppIcon(AppIconType.Refresh); Spacer(Modifier.width(8.dp)); Text("刷新能力和当前参数")
                }
                val snapshot = device.controls?.takeIf { it.targetId == device.status?.targetId }
                if (snapshot != null) {
                    Text("读回时间: " + DateTimeFormatter.ofPattern("HH:mm:ss").withZone(ZoneId.systemDefault()).format(snapshot.observedAt),
                        style = MaterialTheme.typography.bodySmall)
                    if (!fresh || device.controlsRequireRefresh) Text("应用前需要刷新参数或查询原操作", style = MaterialTheme.typography.bodySmall)
                    DisplayControl.entries.forEach { control ->
                        val capability = snapshot.controls.find { it.control == control } ?: DisplayControlState(control, false, null, emptyList())
                        key(deviceId, snapshot.targetId, control) { ControlEditor(capability, !state.busy && !state.storageFailed && fresh &&
                            !device.controlsRequireRefresh && DisplayControlPolicy.bound(device)) { value ->
                            scope.launch { repository.setDisplayControl(deviceId, control, value, expectedTargetId = snapshot.targetId) }
                        } }
                    }
                    TextButton(onClick = { showVcp = !showVcp }) { Text(if (showVcp) "收起 VCP 能力列表" else "查看 VCP 能力列表 (只读)") }
                    if (showVcp) {
                        if (snapshot.vcpCodes.isEmpty()) Text("显示器未提供 VCP 能力列表", style = MaterialTheme.typography.bodySmall)
                        snapshot.vcpCodes.forEach { code ->
                            Text("0x%02X %s".format(code.code, code.name) + if (code.supportedValues.isEmpty()) "" else
                                " : " + code.supportedValues.joinToString { "0x%02X".format(it) }, style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ControlEditor(capability: DisplayControlState, writable: Boolean, apply: (Int) -> Unit) {
    var draft by remember(capability.value) { mutableIntStateOf(capability.value ?: if (DisplayControlPolicy.continuous(capability.control)) 50 else capability.supportedValues.firstOrNull() ?: 0) }
    var edited by remember(capability.value) { mutableStateOf(false) }
    var choosing by remember { mutableStateOf(false) }
    val enabled = writable && capability.supported
    HorizontalDivider()
    Text(DisplayControlPolicy.label(capability.control), style = MaterialTheme.typography.titleSmall)
    Text(if (!capability.supported) "此显示器不支持或未提供该能力" else capability.value?.let {
        "当前读回: " + DisplayControlPolicy.valueLabel(capability.control, it)
    } ?: "当前值未能读回", style = MaterialTheme.typography.bodySmall)
    if (DisplayControlPolicy.continuous(capability.control)) {
        Slider(value = draft.toFloat(), onValueChange = { draft = it.roundToInt(); edited = true }, valueRange = 0f..100f, enabled = enabled)
        Text("待应用: $draft%", style = MaterialTheme.typography.bodySmall)
    } else {
        Box {
            OutlinedButton(onClick = { choosing = true }, enabled = enabled && capability.supportedValues.isNotEmpty()) {
                Text(DisplayControlPolicy.valueLabel(capability.control, draft))
            }
            DropdownMenu(choosing, { choosing = false }) {
                capability.supportedValues.forEach { value -> DropdownMenuItem(
                    text = { Text(DisplayControlPolicy.valueLabel(capability.control, value)) },
                    onClick = { draft = value; edited = true; choosing = false }) }
            }
        }
    }
    val validDraft = DisplayControlPolicy.legal(capability.control, draft) &&
        (DisplayControlPolicy.continuous(capability.control) || draft in capability.supportedValues)
    FilledTonalButton(onClick = { edited = false; apply(draft) }, enabled = enabled && edited && validDraft) {
        Text("应用" + DisplayControlPolicy.label(capability.control))
    }
}
