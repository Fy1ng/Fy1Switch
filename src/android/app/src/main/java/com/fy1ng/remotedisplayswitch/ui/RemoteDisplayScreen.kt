package com.fy1ng.remotedisplayswitch.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
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
import androidx.compose.ui.Modifier
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
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun RemoteDisplayScreen(repository: DeviceRepository, scope: CoroutineScope, onAddTile: (() -> Unit)?) {
    val state by repository.state.collectAsState()
    var pairing by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        repository.initialize()
        repository.state.value.selected?.let { device ->
            scope.launch { repository.refresh(device.profile.deviceId, live = false) }
        }
    }
    MaterialTheme {
        Scaffold { padding ->
            Column(
                Modifier.fillMaxSize().padding(padding).imePadding().verticalScroll(rememberScrollState()).padding(20.dp),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Text("远程显示器", style = MaterialTheme.typography.headlineLarge)
                Text("优先局域网直连 · 可选 Relay 回退", style = MaterialTheme.typography.bodyMedium)
                if (!state.loaded || state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                state.notice?.let { Text(it.text, color = if (state.storageFailed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant) }
                if (state.loaded && !state.storageFailed) {
                    if (state.devices.isNotEmpty()) {
                        Text("默认设备（同时用于快捷开关）", style = MaterialTheme.typography.titleMedium)
                        state.devices.forEach { device ->
                            OutlinedButton(
                                onClick = {
                                    editing = false
                                    scope.launch {
                                        if (repository.select(device.profile.deviceId) == Feedback.Saved) repository.refresh(device.profile.deviceId, live = false)
                                    }
                                }, enabled = !state.busy, modifier = Modifier.fillMaxWidth(),
                            ) {
                                Text((if (device.profile.deviceId == state.defaultDeviceId) "✓ " else "") + device.profile.name)
                            }
                        }
                        state.selected?.let { device ->
                            DeviceCard(device, state, repository, scope)
                            OutlinedButton(onClick = { editing = !editing }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
                                Text(if (editing) "收起设备配置" else "设备名称 / Relay / 移除")
                            }
                            if (editing) key(device.profile.deviceId) { DeviceEditor(device, state.busy, repository, scope) }
                        }
                    } else Text("在 Windows 导出一次性配对 JSON，粘贴后安全连接。请只使用你信任的 Windows 端导出内容。")
                    OutlinedButton(onClick = { pairing = !pairing }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
                        Text(if (pairing) "收起配对" else "添加 / 重新配对设备")
                    }
                    if (pairing || state.devices.isEmpty()) PairEditor(repository, scope, state.busy)
                    if (onAddTile != null) OutlinedButton(onClick = onAddTile, modifier = Modifier.fillMaxWidth()) { Text("添加快捷设置开关") }
                    Text("快捷开关只控制默认设备。长按打开本页；状态未知时可重新展开快捷设置或在此实时刷新。解锁成功才会发送锁屏操作。")
                    Text("“已接受 / 未确认”不等于成功。结果未知时先查询已有操作，不要反复点击切换。", style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
}

@Composable
private fun DeviceCard(device: DeviceState, state: AppState, repository: DeviceRepository, scope: CoroutineScope) {
    val deviceId = device.profile.deviceId
    val allowed = StatePolicy.canSet(state)
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(device.status?.displayName?.filterNot(Char::isISOControl)?.take(80) ?: device.profile.name, style = MaterialTheme.typography.titleLarge)
            Text(when (StatePolicy.confirmedPower(device)) {
                DisplayPower.On -> "已确认开启"
                DisplayPower.Off -> "已确认关闭"
                DisplayPower.Unknown -> if (!device.connected) "未连接 / 状态未知" else "显示器状态未知"
            }, style = MaterialTheme.typography.headlineSmall)
            Text(device.feedback.text)
            if (device.status?.busy == true) Text("Windows 正在处理操作，请稍后查询或刷新")
            device.status?.observation?.let {
                Text("观测时间：" + DateTimeFormatter.ofPattern("MM-dd HH:mm:ss").withZone(ZoneId.systemDefault()).format(it.observedAt), style = MaterialTheme.typography.bodySmall)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Button(onClick = { scope.launch { repository.set(deviceId, PowerIntent.On) } }, enabled = allowed, modifier = Modifier.weight(1f)) { Text("开启") }
                Button(onClick = { scope.launch { repository.set(deviceId, PowerIntent.Off) } }, enabled = allowed, modifier = Modifier.weight(1f)) { Text("关闭") }
            }
            OutlinedButton(onClick = { scope.launch { repository.set(deviceId, PowerIntent.Toggle) } }, enabled = allowed && StatePolicy.confirmedPower(device) != DisplayPower.Unknown, modifier = Modifier.fillMaxWidth()) { Text("切换开 / 关") }
            OutlinedButton(onClick = { scope.launch { repository.refresh(deviceId, live = true) } }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("实时读取显示器") }
            TextButton(onClick = { scope.launch { repository.refresh(deviceId, live = false) } }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("连接 / 获取主机已知状态") }
            device.operation?.let { operation ->
                HorizontalDivider()
                Text("最近操作：" + operation.operationId.toString().take(8) + " · " + when (operation.outcome) {
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Confirmed -> "已确认"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.AcceptedUnconfirmed -> "已接受，未确认"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Pending -> "处理中"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Unknown -> "结果未知"
                    com.fy1ng.remotedisplayswitch.core.OperationOutcome.Failed -> "失败"
                }, style = MaterialTheme.typography.bodySmall)
                OutlinedButton(onClick = { scope.launch { repository.queryOperation(deviceId) } }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("查询这次操作（不重发控制）") }
            }
        }
    }
}

@Composable
private fun PairEditor(repository: DeviceRepository, scope: CoroutineScope, busy: Boolean) {
    var bundle by remember { mutableStateOf("") }
    var clientName by remember { mutableStateOf("Android 手机") }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("配对新设备", style = MaterialTheme.typography.titleMedium)
            Text("配对 JSON 内含 5 分钟有效的一次性码及证书身份。请勿转发。仅支持先通过 HTTPS 直连配对。")
            OutlinedTextField(
                value = bundle, onValueChange = { if (it.length <= 65536) bundle = it }, label = { Text("粘贴 Windows 配对 JSON") },
                visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                enabled = !busy, modifier = Modifier.fillMaxWidth(), maxLines = 5,
            )
            OutlinedTextField(value = clientName, onValueChange = { if (it.length <= 80) clientName = it }, label = { Text("本手机名称（Windows 可见）") }, enabled = !busy, modifier = Modifier.fillMaxWidth())
            Button(onClick = {
                val submitted = bundle
                bundle = ""
                scope.launch { repository.pair(submitted, clientName) }
            }, enabled = !busy && bundle.isNotBlank() && clientName.isNotBlank(), modifier = Modifier.fillMaxWidth()) { Text("验证证书并配对") }
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
