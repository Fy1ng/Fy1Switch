package com.fy1ng.remotedisplayswitch.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.fy1ng.remotedisplayswitch.core.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

@Composable
internal fun DeviceSettingsScreen(
    device: DeviceState, state: AppState, repository: DeviceRepository, scope: CoroutineScope,
    onBack: () -> Unit, onPair: () -> Unit, onAddTile: () -> Unit,
    onDirtyChanged: (Boolean) -> Unit,
) {
    val profile = device.profile
    var name by remember(profile.name) { mutableStateOf(profile.name) }
    var endpoint by remember(profile.relay?.endpoint) { mutableStateOf(profile.relay?.endpoint.orEmpty()) }
    var token by remember { mutableStateOf("") }
    var tailscale by remember(profile.tailscaleEndpoint) { mutableStateOf(profile.tailscaleEndpoint.orEmpty()) }
    var autoLearn by remember(profile.autoLearnTailscale) { mutableStateOf(profile.autoLearnTailscale) }
    var advanced by rememberSaveable { mutableStateOf(false) }
    var confirmRemoval by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    val dirty = name != profile.name || endpoint != profile.relay?.endpoint.orEmpty() || token.isNotEmpty() ||
        tailscale != profile.tailscaleEndpoint.orEmpty() || autoLearn != profile.autoLearnTailscale
    SideEffect { onDirtyChanged(dirty) }
    DisposableEffect(Unit) { onDispose { onDirtyChanged(false) } }
    Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
        OutlinedTextField(value = name, onValueChange = { if (it.length <= 80) { name = it; message = null } },
            isError = message == Feedback.InvalidName.text,
            label = { Text("电脑名称") }, singleLine = true, enabled = !state.busy, modifier = Modifier.fillMaxWidth())
        Card(shape = RoundedCornerShape(20.dp),
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("快捷开关", style = MaterialTheme.typography.titleMedium)
                Text("从系统快捷设置直接控制当前电脑", style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                FilledTonalButton(onClick = onAddTile, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
                    AppIcon(AppIconType.Add); Spacer(Modifier.width(8.dp)); Text("添加快捷开关")
                }
            }
        }
        OutlinedCard(shape = RoundedCornerShape(20.dp)) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                TextButton(onClick = { advanced = !advanced }, modifier = Modifier.fillMaxWidth()) {
                    AppIcon(AppIconType.Settings)
                    Spacer(Modifier.width(10.dp))
                    Text("连接设置", Modifier.weight(1f))
                    Text(if (advanced) "收起" else "展开", style = MaterialTheme.typography.labelMedium)
                    Spacer(Modifier.width(8.dp))
                    AppIcon(if (advanced) AppIconType.Up else AppIconType.Down)
                }
                if (advanced) {
                    Text("自动按直连，Tailscale，Relay 顺序连接", style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text("直连地址", style = MaterialTheme.typography.labelLarge)
                    Text(profile.endpoint, style = MaterialTheme.typography.bodySmall)
                    Text("直连地址或证书变化时，请重新配对", style = MaterialTheme.typography.bodySmall)
                    HorizontalDivider()
                    Text("Tailscale 备用直连", style = MaterialTheme.typography.titleSmall)
                    OutlinedTextField(value = tailscale, onValueChange = {
                        if (it.length <= 2048) { tailscale = it; message = null; if (it.isBlank()) autoLearn = false }
                    }, label = { Text("HTTPS 地址（可选）") }, singleLine = true, enabled = !state.busy,
                        isError = message == Feedback.InvalidTailscaleEndpoint.text,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri), modifier = Modifier.fillMaxWidth())
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("从电脑自动更新地址", Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                        Switch(checked = autoLearn, onCheckedChange = { autoLearn = it }, enabled = !state.busy,
                            modifier = Modifier.semantics { contentDescription = "从电脑自动更新 Tailscale 地址" })
                    }
                    HorizontalDivider()
                    Text("Relay 中继", style = MaterialTheme.typography.titleSmall)
                    OutlinedTextField(value = endpoint, onValueChange = { if (it.length <= 2048) { endpoint = it; message = null } },
                        isError = message == Feedback.InvalidRelayEndpoint.text,
                        label = { Text("Relay HTTPS 地址（可选）") }, singleLine = true, enabled = !state.busy,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri), modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(value = token, onValueChange = { if (it.length <= 4096) { token = it; message = null } },
                        isError = message in setOf(Feedback.RelayTokenRequired.text, Feedback.InvalidRelayToken.text, Feedback.RelayDeviceTokenConflict.text),
                        label = { Text("控制端凭据") },
                        placeholder = { Text(if (profile.relay != null) "留空保留已存凭据" else "填写 Relay 的控制端凭据") },
                        visualTransformation = PasswordVisualTransformation(), singleLine = true, enabled = !state.busy,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), modifier = Modifier.fillMaxWidth())
                    Text("只配置可信中继。更换地址需重新填写凭据，清空地址并保存会停用中继。",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                } else {
                    Text(if (profile.relay != null) "已配置中继" else "使用已配对的连接地址",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        message?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) }
        Button(onClick = {
            val submittedName = name
            val submittedEndpoint = endpoint
            val submittedToken = token
            val submittedTailscale = tailscale
            val submittedAutoLearn = autoLearn
            scope.launch {
                val result = repository.configure(profile.deviceId, submittedName, submittedEndpoint, submittedToken,
                    clearRelay = false, tailscaleEndpoint = submittedTailscale, autoLearnTailscale = submittedAutoLearn)
                if (result == Feedback.Saved) { token = ""; message = null } else message = result.text
            }
        }, enabled = !state.busy && dirty && name.isNotBlank(),
            modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp), shape = RoundedCornerShape(16.dp)) {
            AppIcon(if (dirty) AppIconType.Save else AppIconType.Check)
            Spacer(Modifier.width(8.dp))
            Text(if (state.busy) "正在处理" else if (dirty) "保存更改" else "已保存")
        }
        TextButton(onClick = onPair, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
            AppIcon(AppIconType.Phone); Spacer(Modifier.width(8.dp)); Text("重新配对电脑")
        }
        HorizontalDivider()
        TextButton(onClick = { confirmRemoval = true }, enabled = !state.busy,
            colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error),
            modifier = Modifier.fillMaxWidth()) {
            AppIcon(AppIconType.Delete); Spacer(Modifier.width(8.dp)); Text("移除此电脑")
        }
    }
    if (confirmRemoval) AlertDialog(
        onDismissRequest = { confirmRemoval = false }, title = { Text("移除此电脑？") },
        text = { Text("删除本手机保存的配对和操作记录。电脑端授权仍需在 Windows 上撤销。") },
        confirmButton = { TextButton(onClick = {
            confirmRemoval = false
            scope.launch {
                val result = repository.remove(profile.deviceId)
                if (result == Feedback.Saved) onBack() else message = result.text
            }
        }, enabled = !state.busy) { Text("移除") } },
        dismissButton = { TextButton(onClick = { confirmRemoval = false }) { Text("取消") } },
    )
}
