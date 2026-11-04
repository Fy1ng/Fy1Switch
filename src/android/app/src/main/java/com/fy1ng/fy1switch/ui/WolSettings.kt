package com.fy1ng.fy1switch.ui

import android.os.Build
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.fy1ng.fy1switch.core.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import java.time.Instant

@Composable
internal fun WolSettings(device: DeviceState, state: AppState, repository: DeviceRepository, scope: CoroutineScope,
    onAddTile: () -> Unit, onDirtyChanged: (Boolean) -> Unit) {
    val ui by repository.wol.ui.collectAsState()
    val binding = device.profile.wol
    var endpoint by remember(binding?.endpoint) { mutableStateOf(binding?.endpoint ?: state.wolAuthorizations.firstOrNull()?.endpoint.orEmpty()) }
    var code by remember { mutableStateOf("") }
    var enabled by remember(binding?.enabled) { mutableStateOf(binding?.enabled ?: false) }
    var selected by remember(binding?.targetId) { mutableStateOf(binding?.targetId ?: device.profile.deviceId) }
    var expanded by remember { mutableStateOf(false) }
    var confirmClear by remember { mutableStateOf<Boolean?>(null) }
    val normalized = runCatching { WolProtocol.endpoint(endpoint) }.getOrNull()
    val auth = state.wolAuthorizations.find { it.endpoint == normalized }
    val dirty = binding != null && (normalized != binding.endpoint || enabled != binding.enabled || selected != binding.targetId) ||
        binding == null && (enabled || endpoint.isNotBlank() || code.isNotBlank())
    SideEffect { onDirtyChanged(dirty) }
    DisposableEffect(Unit) { onDispose { onDirtyChanged(false) } }
    OutlinedCard(shape = RoundedCornerShape(20.dp)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("远程开机 (Wake on LAN)", style = MaterialTheme.typography.titleMedium)
            Text("通过独立 Gateway 唤醒电脑. 不需要手机连接 WireGuard. 启动后继续使用原显示器连接.",
                style = MaterialTheme.typography.bodySmall)
            OutlinedTextField(endpoint, { if (it.length <= 2048) endpoint = it }, label = { Text("Gateway HTTPS 地址") },
                singleLine = true, enabled = !ui.busy, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth())
            if (state.wolAuthorizations.size > 1) state.wolAuthorizations.forEach { value ->
                TextButton(onClick = { endpoint = value.endpoint }, enabled = !ui.busy) { Text("使用 " + value.endpoint) }
            }
            if (auth == null) {
                OutlinedTextField(code, { if (it.length <= 128) code = it }, label = { Text("一次性注册码") },
                    singleLine = true, enabled = !ui.busy, visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    modifier = Modifier.fillMaxWidth())
                Text("在 VPS 创建短时注册码, 并授予本电脑的 Windows 设备 ID. 注册码仅用于首次授权.", style = MaterialTheme.typography.bodySmall)
                Button(onClick = { val submitted = code; code = ""; scope.launch {
                    repository.wol.register(device.profile.deviceId, endpoint, submitted, Build.MODEL.take(80).ifBlank { "Android" })
                } }, enabled = !ui.busy && normalized != null && code.isNotBlank(), modifier = Modifier.fillMaxWidth()) { Text("授权此手机") }
            } else {
                Text("本手机已获 Gateway 独立授权", style = MaterialTheme.typography.bodySmall)
                Box {
                    OutlinedButton(onClick = { expanded = true }, enabled = !ui.busy) {
                        Text(auth.devices.find { it.deviceId == selected }?.name ?: "选择授权目标")
                    }
                    DropdownMenu(expanded, { expanded = false }) {
                        auth.devices.forEach { target -> DropdownMenuItem(
                            text = { Text(target.name + if (target.deviceId != device.profile.deviceId) " (其他电脑)" else "") },
                            onClick = { selected = target.deviceId; expanded = false },
                            enabled = target.deviceId == device.profile.deviceId,
                        ) }
                    }
                }
                if (auth.devices.none { it.deviceId == device.profile.deviceId }) Text(
                    "Gateway 尚未授权本电脑的设备 ID. 请在 VPS 使用下方 ID 配置目标并重新授权.", color = MaterialTheme.colorScheme.error)
                Text("Windows 设备 ID: ${device.profile.deviceId}", style = MaterialTheme.typography.bodySmall)
                Row {
                    Text("启用远程开机", Modifier.weight(1f))
                    Switch(enabled, { enabled = it }, enabled = !ui.busy)
                }
                Button(onClick = { scope.launch { repository.wol.configure(device.profile.deviceId, endpoint, selected, enabled) } },
                    enabled = !ui.busy && auth.devices.any { it.deviceId == selected } && selected == device.profile.deviceId,
                    modifier = Modifier.fillMaxWidth()) { Text("保存远程开机设置") }
                TextButton(onClick = { scope.launch { repository.wol.refreshDevices(device.profile.deviceId, endpoint) } }, enabled = !ui.busy) {
                    Text("验证 Gateway 并刷新授权目标")
                }
                if (binding != null) TextButton(onClick = { scope.launch { repository.wol.check(device.profile.deviceId) } }, enabled = !ui.busy) {
                    Text("测试 OpenWrt 连接 (不唤醒)")
                }
                TextButton(onClick = { confirmClear = true }, enabled = !ui.busy) { Text("撤销此手机在 Gateway 的全部授权") }
                TextButton(onClick = { confirmClear = false }, enabled = !ui.busy) { Text("仅清除本地授权") }
            }
            if (ui.busy && ui.deviceId == device.profile.deviceId) LinearProgressIndicator(Modifier.fillMaxWidth())
            if (ui.deviceId == device.profile.deviceId) ui.message?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            device.wolOperation?.let { Text(WolPolicy.result(it, repository.hasFreshHost(device.profile.deviceId), Instant.now()),
                style = MaterialTheme.typography.bodySmall) }
            HorizontalDivider()
            Text("远程开机磁贴", style = MaterialTheme.typography.titleSmall)
            Text(if (state.wolTileDeviceId == null) "跟随当前选中的电脑" else
                "固定目标: " + (state.devices.find { it.profile.deviceId == state.wolTileDeviceId }?.profile?.name ?: "已删除的电脑, 请重新选择"),
                style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { scope.launch { repository.wol.setTileTarget(null) } }, enabled = !ui.busy) { Text("跟随当前电脑") }
            TextButton(onClick = { scope.launch { repository.wol.setTileTarget(device.profile.deviceId) } }, enabled = !ui.busy) { Text("固定为这台电脑") }
            FilledTonalButton(onClick = onAddTile, modifier = Modifier.fillMaxWidth()) { Text("添加远程开机磁贴") }
        }
    }
    confirmClear?.let { revoke -> AlertDialog(onDismissRequest = { confirmClear = null },
        title = { Text(if (revoke) "撤销 Gateway 授权?" else "清除本地授权?") },
        text = { Text(if (revoke) "此手机在该 Gateway 上所有电脑的 WOL 授权都会撤销. 不影响原显示器配对."
            else "服务端授权仍有效, 请在 VPS 手动撤销. 所有使用此 Gateway 的本地 WOL 配置将清除.") },
        confirmButton = { TextButton(onClick = { confirmClear = null; scope.launch { repository.wol.clear(device.profile.deviceId, endpoint, revoke) } }) { Text("确认") } },
        dismissButton = { TextButton(onClick = { confirmClear = null }) { Text("取消") } }) }
}

@Composable
internal fun WolHome(device: DeviceState, state: AppState, repository: DeviceRepository, scope: CoroutineScope, onConfigure: () -> Unit) {
    val ui by repository.wol.ui.collectAsState()
    val configured = WolPolicy.configured(state, device)
    val fresh = repository.hasFreshHost(device.profile.deviceId)
    var confirmNew by remember { mutableStateOf(false) }
    OutlinedCard(shape = RoundedCornerShape(20.dp)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("远程开机", style = MaterialTheme.typography.titleMedium)
            if (!configured) {
                Text("尚未启用远程开机, 原显示器控制不受影响.", style = MaterialTheme.typography.bodySmall)
                TextButton(onClick = onConfigure) { Text("配置远程开机") }
            } else {
                Text(WolPolicy.result(device.wolOperation, fresh, Instant.now()), style = MaterialTheme.typography.bodySmall)
                Button(onClick = { scope.launch { repository.wol.wake(device.profile.deviceId) } },
                    enabled = !ui.busy && !fresh, modifier = Modifier.fillMaxWidth()) {
                    if (ui.busy && ui.deviceId == device.profile.deviceId) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    else AppIcon(AppIconType.Power)
                    Spacer(Modifier.width(8.dp))
                    Text(if (fresh) "Windows Host 已上线" else if (device.wolOperation?.status in setOf(WolStatus.Unknown, WolStatus.Pending)) "查询上次唤醒请求" else "发送唤醒包")
                }
                if (device.wolOperation != null) TextButton(onClick = { scope.launch { repository.wol.query(device.profile.deviceId) } }, enabled = !ui.busy) { Text("查询最近唤醒结果") }
                if (device.wolOperation?.status == WolStatus.Unknown && Instant.now() >= device.wolOperation.issuedAt.plusSeconds(90))
                    TextButton(onClick = { confirmNew = true }, enabled = !ui.busy && !fresh) { Text("明确发送一次新的唤醒请求") }
                if (ui.deviceId == device.profile.deviceId) ui.message?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            }
        }
    }
    if (confirmNew) AlertDialog(onDismissRequest = { confirmNew = false }, title = { Text("发送新的唤醒请求?") },
        text = { Text("上次请求可能已经发送. 此操作将创建新的请求编号并再次发送一次唤醒包.") },
        confirmButton = { TextButton(onClick = { confirmNew = false; scope.launch { repository.wol.wake(device.profile.deviceId, true) } }) { Text("发送一次") } },
        dismissButton = { TextButton(onClick = { confirmNew = false }) { Text("取消") } })
}
