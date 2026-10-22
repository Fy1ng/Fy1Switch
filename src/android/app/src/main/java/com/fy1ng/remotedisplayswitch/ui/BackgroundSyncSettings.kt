package com.fy1ng.remotedisplayswitch.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.fy1ng.remotedisplayswitch.background.BackgroundSyncState

@Composable
internal fun BackgroundSyncSettings(
    state: BackgroundSyncState,
    message: String?,
    permissionPending: Boolean,
    onEnabled: (Boolean) -> Unit,
    onOpenNotificationSettings: () -> Unit,
) {
    OutlinedCard(shape = RoundedCornerShape(20.dp)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("后台同步显示器状态", modifier = Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
                Switch(checked = state.enabled, onCheckedChange = onEnabled, enabled = !permissionPending,
                    modifier = Modifier.semantics { contentDescription = "后台同步显示器状态" })
            }
            Text("应用全局设置, 仅同步当前电脑. 开启后显示常驻通知, 约每 10 秒读取状态, 减少下拉快捷面板后的等待.",
                style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(when {
                permissionPending -> "等待系统通知权限"
                state.enabled && state.running -> "正在后台同步"
                state.enabled -> "已启用, 后台同步尚未运行"
                else -> "已关闭, 打开首页或下拉快捷面板时刷新"
            }, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            (message ?: state.error)?.let {
                Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            }
            TextButton(onClick = onOpenNotificationSettings, enabled = !permissionPending) { Text("系统通知设置") }
        }
    }
}
