package com.fy1ng.remotedisplayswitch.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.fy1ng.remotedisplayswitch.core.AppState
import com.fy1ng.remotedisplayswitch.core.DeviceRepository
import com.fy1ng.remotedisplayswitch.core.DeviceState
import com.fy1ng.remotedisplayswitch.core.DisplayPower
import com.fy1ng.remotedisplayswitch.core.Feedback
import com.fy1ng.remotedisplayswitch.core.PowerIntent
import com.fy1ng.remotedisplayswitch.core.StatePolicy
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.first
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun RemoteDisplayScreen(
    repository: DeviceRepository, scope: CoroutineScope, onAddTile: () -> Unit,
    tileMessage: String?, showTileGuide: Boolean, onDismissTileGuide: () -> Unit, onPaired: () -> Unit,
) {
    val state by repository.state.collectAsState()
    var pairing by rememberSaveable { mutableStateOf(false) }
    var editing by remember { mutableStateOf(false) }
    var choosing by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        repository.initialize()
        repository.state.first { !it.busy }.selected?.let { repository.refresh(it.profile.deviceId) }
    }
    val dark = androidx.compose.foundation.isSystemInDarkTheme()
    val context = androidx.compose.ui.platform.LocalContext.current
    val colors = if (android.os.Build.VERSION.SDK_INT >= 31) {
        if (dark) androidx.compose.material3.dynamicDarkColorScheme(context)
        else androidx.compose.material3.dynamicLightColorScheme(context)
    } else if (dark) androidx.compose.material3.darkColorScheme() else androidx.compose.material3.lightColorScheme()
    MaterialTheme(colorScheme = colors) {
        Scaffold { padding ->
            Column(
                Modifier.fillMaxSize().padding(padding).imePadding().verticalScroll(rememberScrollState()).padding(20.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    TextButton(onClick = { choosing = !choosing }, enabled = !state.busy,
                        modifier = Modifier.weight(1f)) { Text((state.selected?.profile?.name ?: "选择设备") + " ▾") }
                    TextButton(onClick = { editing = !editing }, enabled = !state.busy) { Text("设置") }
                }
                if (choosing) {
                    Text("当前设备也用于快捷开关", style = MaterialTheme.typography.bodySmall)
                    state.devices.forEach { device ->
                        OutlinedButton(onClick = {
                            choosing = false
                            editing = false
                            if (device.profile.deviceId != state.defaultDeviceId) scope.launch {
                                if (repository.select(device.profile.deviceId) == Feedback.Saved) repository.refresh(device.profile.deviceId)
                            }
                        }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
                            Text((if (device.profile.deviceId == state.defaultDeviceId) "✓ " else "") + device.profile.name)
                        }
                    }
                    TextButton(onClick = { pairing = true; choosing = false }, enabled = !state.busy) { Text("添加设备") }
                }
                if (!state.loaded || state.busy) {
                    LinearProgressIndicator(Modifier.fillMaxWidth())
                    Text(if (!state.loaded) "正在读取设备…" else "正在处理请求，请稍候…")
                }
                state.notice?.let { Text(it.text, color = if (state.storageFailed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant) }
                if (state.loaded && !state.storageFailed) {
                    state.selected?.let { device ->
                        key(device.profile.deviceId) { DeviceCard(device, state, repository, scope) }
                    }
                    if (editing) {
                        Text("设备设置", style = MaterialTheme.typography.titleLarge)
                        state.selected?.let { device ->
                            key(device.profile.deviceId) { DeviceEditor(device, state.busy, repository, scope) }
                        }
                        OutlinedButton(onClick = { pairing = !pairing }, enabled = !state.busy,
                            modifier = Modifier.fillMaxWidth()) { Text("添加 / 重新配对设备") }
                        OutlinedButton(onClick = onAddTile, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("添加快捷设置开关") }
                        Text("快捷开关控制当前设备；长按可打开此页。", style = MaterialTheme.typography.bodySmall)
                    }
                    if (pairing || state.devices.isEmpty()) {
                        PairEditor(repository, scope, state.busy) { pairing = false; onPaired() }
                        if (state.devices.isNotEmpty()) TextButton(onClick = { pairing = false }, enabled = !state.busy) { Text("收起配对") }
                    }
                    if (showTileGuide) Card(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text("配对已保存。添加快捷开关，下次无需打开应用。")
                            Button(onClick = { onDismissTileGuide(); onAddTile() }, enabled = !state.busy) { Text("添加快捷开关") }
                            TextButton(onClick = onDismissTileGuide) { Text("以后再说") }
                        }
                    }
                }
                tileMessage?.let { Text(it) }
            }
        }
    }
}

@Composable
private fun DeviceCard(device: DeviceState, state: AppState, repository: DeviceRepository, scope: CoroutineScope) {
    val deviceId = device.profile.deviceId
    val power = StatePolicy.confirmedPower(device)
    val action = HomePolicy.action(state)
    var details by remember { mutableStateOf(false) }
    val abnormal = device.operation?.outcome?.let { it != com.fy1ng.remotedisplayswitch.core.OperationOutcome.Confirmed } == true
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Text(device.status?.displayName?.filterNot(Char::isISOControl)?.take(80) ?: "显示器",
                    style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                androidx.compose.material3.FilledTonalIconButton(
                    onClick = { scope.launch { repository.refresh(deviceId) } }, enabled = !state.busy,
                    modifier = Modifier.semantics { contentDescription = "刷新连接与显示器状态" },
                ) { Text("↻") }
            }
            Text(HomePolicy.status(state), style = MaterialTheme.typography.displaySmall)
            if (power != DisplayPower.Unknown) Text("最近一次确认的状态", style = MaterialTheme.typography.bodySmall)
            Text(device.feedback.text)
            androidx.compose.foundation.layout.Spacer(Modifier.height(16.dp))
            Button(onClick = {
                scope.launch {
                    if (action.intent == null) repository.refresh(deviceId) else repository.set(deviceId, action.intent)
                }
            }, enabled = action.enabled, modifier = Modifier.fillMaxWidth().heightIn(min = 64.dp)) {
                Text(action.label, style = MaterialTheme.typography.titleLarge)
            }
            TextButton(onClick = { details = !details }, modifier = Modifier.fillMaxWidth()) {
                Text(if (details) "收起详情" else "状态与操作详情")
            }
            if (details) {
                device.status?.observation?.let {
                    Text("观测时间：" + DateTimeFormatter.ofPattern("MM-dd HH:mm:ss").withZone(ZoneId.systemDefault()).format(it.observedAt))
                }
                if (power == DisplayPower.Unknown) {
                    Text("不自动切换未知状态；仅在连接与安全条件允许时指定开启或关闭。")
                    OutlinedButton(onClick = { scope.launch { repository.set(deviceId, PowerIntent.On) } },
                        enabled = StatePolicy.canSet(state), modifier = Modifier.fillMaxWidth()) { Text("明确开启（ON）") }
                    OutlinedButton(onClick = { scope.launch { repository.set(deviceId, PowerIntent.Off) } },
                        enabled = StatePolicy.canSet(state), modifier = Modifier.fillMaxWidth()) { Text("明确关闭（OFF）") }
                }
            }
            if (details || abnormal) device.operation?.let { operation ->
                HorizontalDivider()
                Text("最近操作：" + when (operation.outcome) {
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Confirmed -> "已确认"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.AcceptedUnconfirmed -> "已接受，未确认"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Pending -> "处理中"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Unknown -> "结果未知"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Failed -> "失败"
                }, color = if (abnormal) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                Text("操作编号：" + operation.operationId.toString().take(8), style = MaterialTheme.typography.bodySmall)
                if (abnormal) {
                    Text("查询只读取这次操作，不会重新发送电源命令。")
                    OutlinedButton(onClick = { scope.launch { repository.queryOperation(deviceId) } },
                        enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("查询这次操作") }
                }
            }
        }
    }
}

@Composable
private fun DeviceEditor(device: DeviceState, busy: Boolean, repository: DeviceRepository, scope: CoroutineScope) {
    var name by remember(device.profile.name) { mutableStateOf(device.profile.name) }
    var endpoint by remember(device.profile.relay?.endpoint) { mutableStateOf(device.profile.relay?.endpoint.orEmpty()) }
    var token by remember { mutableStateOf("") }
    var confirmRemoval by remember { mutableStateOf(false) }
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("直连地址：" + device.profile.endpoint, style = MaterialTheme.typography.bodySmall)
        Text("地址或证书改变时请重新配对。")
        OutlinedTextField(value = name, onValueChange = { if (it.length <= 80) name = it }, label = { Text("设备名称") }, enabled = !busy, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(value = endpoint, onValueChange = { if (it.length <= 2048) endpoint = it }, label = { Text("Relay HTTPS 地址（可选，不带路径）") }, enabled = !busy, modifier = Modifier.fillMaxWidth(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
        OutlinedTextField(value = token, onValueChange = { if (it.length <= 4096) token = it }, label = { Text("Relay 控制端凭据") }, placeholder = { Text(if (device.profile.relay != null) "留空保留已存凭据" else "与设备配对 token 不同") }, visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), enabled = !busy, modifier = Modifier.fillMaxWidth())
        Text("Relay 使用系统信任的 TLS 证书，不使用 Windows pin。服务端能接触转发所需设备凭据，请只配置可信 Relay。")
        Button(onClick = {
            val submitted = token
            token = ""
            scope.launch { repository.configure(device.profile.deviceId, name, endpoint, submitted, clearRelay = false) }
        }, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("保存配置") }
        if (device.profile.relay != null) TextButton(onClick = {
            token = ""
            endpoint = ""
            scope.launch { repository.configure(device.profile.deviceId, name, "", "", clearRelay = true) }
        }, enabled = !busy) { Text("停用并删除 Relay 凭据") }
        TextButton(onClick = { confirmRemoval = true }, enabled = !busy) { Text("移除此设备") }
    }
    if (confirmRemoval) AlertDialog(
        onDismissRequest = { confirmRemoval = false }, title = { Text("移除本机配对？") },
        text = { Text("删除本手机保存的设备凭据和最近操作记录。不会撤销 Windows 端授权；如需撤销，请在 Windows 端处理。") },
        confirmButton = { TextButton(onClick = { confirmRemoval = false; scope.launch { repository.remove(device.profile.deviceId) } }, enabled = !busy) { Text("移除") } },
        dismissButton = { TextButton(onClick = { confirmRemoval = false }) { Text("取消") } },
    )
}
