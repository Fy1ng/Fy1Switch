package com.fy1ng.fy1switch.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.pulltorefresh.rememberPullToRefreshState
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import com.fy1ng.fy1switch.core.*
import com.fy1ng.fy1switch.background.BackgroundSyncManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import java.time.ZoneId
import java.time.format.DateTimeFormatter

private const val HOME = "home"
private const val SETTINGS = "settings"
private const val PAIRING = "pairing"

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun Fy1SwitchScreen(
    repository: DeviceRepository, scope: CoroutineScope, onAddTile: () -> Unit,
    tileMessage: String?, showTileGuide: Boolean, onDismissTileGuide: () -> Unit, onPaired: () -> Unit,
    onTileMessageConsumed: (String) -> Unit, onPairingVisibilityChanged: (Boolean) -> Unit,
    backgroundSync: BackgroundSyncManager, backgroundSyncMessage: String?, backgroundSyncPermissionPending: Boolean,
    onBackgroundSyncEnabled: (Boolean) -> Unit, onOpenNotificationSettings: () -> Unit,
) {
    val state by repository.state.collectAsState()
    val backgroundSyncState by backgroundSync.state.collectAsState()
    var freshnessTick by remember { mutableLongStateOf(0L) }
    val fresh = remember(repository, state, freshnessTick) {
        state.defaultDeviceId?.let { repository.hasFreshPower(it) } == true
    }
    val visibleState = HomePolicy.visibleState(state, fresh)
    var page by rememberSaveable { mutableStateOf(HOME) }
    var choosing by remember { mutableStateOf(false) }
    var settingsDirty by remember { mutableStateOf(false) }
    var discardDestination by remember { mutableStateOf<String?>(null) }
    val leaveSettings: (String) -> Unit = { destination ->
        if (settingsDirty) discardDestination = destination else page = destination
    }
    val snackbar = remember { SnackbarHostState() }
    val latestPairingVisibility by rememberUpdatedState(onPairingVisibilityChanged)
    DisposableEffect(page) {
        latestPairingVisibility(page == PAIRING)
        onDispose { latestPairingVisibility(false) }
    }
    LaunchedEffect(tileMessage) {
        tileMessage?.let {
            snackbar.showSnackbar(it, withDismissAction = true, duration = SnackbarDuration.Long)
            onTileMessageConsumed(it)
        }
    }
    LaunchedEffect(Unit) { repository.initialize() }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    LaunchedEffect(page, state.loaded, state.defaultDeviceId, state.storageFailed, lifecycle) {
        if (page == SETTINGS && state.loaded && state.selected == null) page = HOME
        val deviceId = state.defaultDeviceId
        if (page == HOME && state.loaded && !state.storageFailed && deviceId != null) {
            lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
                if (!HomePolicy.canReuseBackgroundPower(repository.state.value, backgroundSync.state.value.running,
                        repository.hasFreshPower(deviceId))) repository.recover(deviceId)
                repository.state.map { HomePolicy.completedUncertainSet(it, deviceId) }
                    .filterNotNull().distinctUntilChanged().collect { repository.recover(deviceId) }
            }
        }
    }
    LaunchedEffect(page, state.defaultDeviceId, lifecycle) {
        if (page == HOME) lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            var expiredRecoveryRequested = repository.state.value.defaultDeviceId?.let { !repository.hasFreshPower(it) } ?: true
            while (true) {
                freshnessTick += 1
                val current = repository.state.value
                val deviceId = current.defaultDeviceId
                val freshNow = deviceId?.let { repository.hasFreshPower(it) } == true
                if (freshNow) expiredRecoveryRequested = false
                else if (backgroundSync.state.value.running && !current.busy && current.loaded && !current.storageFailed &&
                    deviceId != null && !expiredRecoveryRequested) {
                    expiredRecoveryRequested = true
                    repository.recover(deviceId)
                }
                delay(1_000)
            }
        }
    }
    LaunchedEffect(state.notice, page) {
        if (page == SETTINGS && state.notice == Feedback.Saved) snackbar.showSnackbar(Feedback.Saved.text)
    }
    BackHandler(choosing || page != HOME) {
        if (choosing) choosing = false else if (page == SETTINGS) leaveSettings(HOME) else page = HOME
    }
    val dark = androidx.compose.foundation.isSystemInDarkTheme()
    val context = androidx.compose.ui.platform.LocalContext.current
    val colors = if (android.os.Build.VERSION.SDK_INT >= 31) {
        if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
    } else if (dark) darkColorScheme(primary = Color(0xFFB2C5FF)) else lightColorScheme(primary = Color(0xFF365BC1))
    MaterialTheme(colorScheme = colors) {
        Scaffold(
            snackbarHost = { SnackbarHost(snackbar) },
            topBar = {
                Surface {
                    Row(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 12.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        if (page == HOME) {
                            TextButton(onClick = { choosing = true }, enabled = state.loaded && !state.storageFailed,
                                modifier = Modifier.weight(1f)) {
                                AppIcon(AppIconType.Monitor)
                                Spacer(Modifier.width(10.dp))
                                Text(state.selected?.profile?.name ?: "远程显示器", Modifier.weight(1f),
                                    maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
                                AppIcon(AppIconType.Down)
                            }
                            IconButton(onClick = { page = SETTINGS }, enabled = state.selected != null && !state.storageFailed) {
                                AppIcon(AppIconType.Settings, description = "设备设置")
                            }
                        } else {
                            IconButton(onClick = { if (page == SETTINGS) leaveSettings(HOME) else page = HOME }) {
                                AppIcon(AppIconType.Back, description = "返回")
                            }
                            Text(if (page == SETTINGS) "设备设置" else "添加电脑",
                                modifier = Modifier.padding(start = 4.dp),
                                style = MaterialTheme.typography.titleLarge)
                        }
                    }
                }
            },
        ) { padding ->
            val pullState = rememberPullToRefreshState()
            val refreshing = page == HOME && StatePolicy.refreshing(state)
            PullToRefreshBox(isRefreshing = refreshing, state = pullState,
                onRefresh = {
                    if (page == HOME && state.loaded && !state.busy && !state.storageFailed) {
                        state.selected?.let { scope.launch { repository.refresh(it.profile.deviceId) } }
                    }
                }, modifier = Modifier.fillMaxSize().padding(padding),
                indicator = {}) {
            key(page) {
                Column(Modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState())
                    .padding(horizontal = 20.dp, vertical = 12.dp),
                    horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    Column(Modifier.widthIn(max = 600.dp).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                        when {
                            !state.loaded -> {
                                LinearProgressIndicator(Modifier.fillMaxWidth())
                                Text("正在读取设备", color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            state.storageFailed -> {
                                Text("无法读取配对信息", style = MaterialTheme.typography.headlineSmall)
                                Text(Feedback.Storage.text, color = MaterialTheme.colorScheme.error)
                            }
                            page == PAIRING -> {
                                val initialRequest = remember { state.request?.id }
                                if (!state.busy && state.request?.id != initialRequest) {
                                    state.notice?.takeIf { it !in setOf(Feedback.Ready, Feedback.Updated, Feedback.Saved, Feedback.Busy) }?.let {
                                        Text(it.text, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                    }
                                }
                                PairEditor(repository, scope, state.busy) {
                                    if (page == PAIRING) page = HOME
                                    onPaired()
                                }
                            }
                            page == SETTINGS -> state.selected?.let { device ->
                                key(device.profile.deviceId) {
                                    DeviceSettingsScreen(device, state, repository, scope,
                                        onBack = { if (page == SETTINGS) page = HOME },
                                        onPair = { leaveSettings(PAIRING) }, onAddTile = onAddTile,
                                        onDirtyChanged = { settingsDirty = it },
                                        backgroundSyncState = backgroundSyncState, backgroundSyncMessage = backgroundSyncMessage,
                                        backgroundSyncPermissionPending = backgroundSyncPermissionPending,
                                        onBackgroundSyncEnabled = onBackgroundSyncEnabled,
                                        onOpenNotificationSettings = onOpenNotificationSettings)
                                }
                            }
                            state.selected == null -> EmptyHome { page = PAIRING }
                            else -> {
                                val device = requireNotNull(visibleState.selected)
                                key(device.profile.deviceId) { DeviceCard(device, visibleState, repository, scope) }
                                if (showTileGuide) Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer)) {
                                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                        Text("添加快捷开关，下次无需打开应用", style = MaterialTheme.typography.titleSmall)
                                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                            TextButton(onClick = { onDismissTileGuide(); onAddTile() }, enabled = !state.busy) {
                                                AppIcon(AppIconType.Add)
                                                Spacer(Modifier.width(8.dp))
                                                Text("添加")
                                            }
                                            TextButton(onClick = onDismissTileGuide) { Text("以后再说") }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            }
        }
        if (choosing) ModalBottomSheet(onDismissRequest = { choosing = false }) {
            Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp).navigationBarsPadding(),
                verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("选择电脑", style = MaterialTheme.typography.titleLarge)
                Text("快捷开关也控制当前电脑", style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                Column(Modifier.heightIn(max = 320.dp).verticalScroll(rememberScrollState())) {
                    state.devices.forEach { device ->
                        TextButton(onClick = {
                            if (device.profile.deviceId == state.defaultDeviceId) choosing = false else scope.launch {
                                if (repository.select(device.profile.deviceId) == Feedback.Saved) choosing = false
                            }
                        }, enabled = !state.busy, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
                            Column(Modifier.weight(1f), horizontalAlignment = Alignment.Start) {
                                Text(device.profile.name, textAlign = TextAlign.Start,
                                    maxLines = 2, overflow = TextOverflow.Ellipsis)
                                Text(HomePolicy.connectionStatus(device), style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            if (device.profile.deviceId == state.defaultDeviceId) AppIcon(AppIconType.Check, description = "当前电脑")
                        }
                    }
                }
                TextButton(onClick = { choosing = false; page = PAIRING }, modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp)) {
                    AppIcon(AppIconType.Add); Spacer(Modifier.width(8.dp)); Text("添加电脑")
                }
                Spacer(Modifier.height(16.dp))
            }
        }
        if (discardDestination != null) AlertDialog(
            onDismissRequest = { discardDestination = null },
            title = { Text("放弃未保存的修改？") },
            text = { Text("当前更改尚未保存。") },
            confirmButton = { TextButton(onClick = {
                discardDestination?.let { destination ->
                    page = destination
                    discardDestination = null
                    settingsDirty = false
                }
            }) { Text("放弃修改") } },
            dismissButton = { TextButton(onClick = { discardDestination = null }) { Text("继续编辑") } },
        )
    }
}

@Composable
private fun EmptyHome(onPair: () -> Unit) {
    Column(Modifier.fillMaxWidth().padding(vertical = 48.dp), horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(20.dp)) {
        AppIcon(AppIconType.Monitor, modifier = Modifier.size(72.dp), tint = MaterialTheme.colorScheme.primary)
        Text("连接你的电脑", style = MaterialTheme.typography.headlineMedium)
        Text("配对后，在手机上控制显示器开关", color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center)
        Button(onClick = onPair, modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp), shape = RoundedCornerShape(18.dp)) {
            AppIcon(AppIconType.Add); Spacer(Modifier.width(8.dp)); Text("添加电脑")
        }
    }
}

@Composable
private fun DeviceCard(device: DeviceState, state: AppState, repository: DeviceRepository, scope: CoroutineScope) {
    val deviceId = device.profile.deviceId
    val power = StatePolicy.confirmedPower(device)
    val action = HomePolicy.action(state)
    val haptic = LocalHapticFeedback.current
    val performAction: () -> Unit = {
        if (action.enabled) {
            if (action.intent != null) haptic.performHapticFeedback(HapticFeedbackType.LongPress)
            scope.launch {
                when {
                    action.queryOperation -> repository.recover(deviceId)
                    action.intent != null -> if (repository.hasFreshPower(deviceId)) repository.set(deviceId, action.intent)
                        else repository.refresh(deviceId)
                    else -> repository.refresh(deviceId)
                }
            }
        }
    }
    var details by rememberSaveable { mutableStateOf(false) }
    val tint = when (power) {
        DisplayPower.On -> MaterialTheme.colorScheme.primary
        DisplayPower.Off -> MaterialTheme.colorScheme.onSurfaceVariant
        else -> MaterialTheme.colorScheme.tertiary
    }
    Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(28.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
        Column(Modifier.padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Text(device.status?.displayName?.filterNot(Char::isISOControl)?.take(80) ?: "显示器",
                style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
            FilledTonalIconButton(onClick = performAction, enabled = action.enabled,
                modifier = Modifier.size(112.dp), shape = CircleShape,
                colors = IconButtonDefaults.filledTonalIconButtonColors(containerColor = tint.copy(alpha = 0.10f), contentColor = tint)) {
                if (state.busy) CircularProgressIndicator(Modifier.size(48.dp), strokeWidth = 3.dp)
                else AppIcon(if (action.intent != null) AppIconType.Power else AppIconType.Refresh,
                    modifier = Modifier.size(56.dp), description = action.label, tint = tint)
            }
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.Center) {
                AppIcon(when (power) {
                    DisplayPower.On -> AppIconType.Monitor
                    DisplayPower.Off -> AppIconType.MonitorOff
                    DisplayPower.Unknown -> AppIconType.Question
                }, tint = tint)
                Spacer(Modifier.width(10.dp))
                Text(HomePolicy.status(state), style = MaterialTheme.typography.headlineSmall, textAlign = TextAlign.Center)
            }
            val message = if (StatePolicy.refreshing(state)) null
            else if (device.connected && device.status?.available == false) "请在电脑上确认显示器绑定" else when (device.feedback) {
                Feedback.Ready, Feedback.Paired -> "配对已保存，连接后读取状态"
                Feedback.Updated, Feedback.Confirmed, Feedback.Saved, Feedback.Busy -> null
                Feedback.Pending, Feedback.Unknown, Feedback.AcceptedUnconfirmed -> if (device.requiresRefresh) "先确认本次结果，避免重复开关" else null
                Feedback.NotFound -> "未查到记录，可刷新当前状态"
                Feedback.Offline -> "请检查电脑和网络连接"
                else -> device.feedback.text
            }
            if (message != null) Text(message, style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center)
            else if (power != DisplayPower.Unknown) device.status?.observation?.let {
                Text("上次确认 " + DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault()).format(it.observedAt),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Button(onClick = performAction, enabled = action.enabled, modifier = Modifier.fillMaxWidth().heightIn(min = 60.dp), shape = RoundedCornerShape(18.dp)) {
                if (!state.busy) {
                    AppIcon(if (action.intent != null) AppIconType.Power else AppIconType.Refresh)
                    Spacer(Modifier.width(10.dp))
                }
                Text(action.label, style = MaterialTheme.typography.titleMedium)
            }
            if (power == DisplayPower.Unknown && StatePolicy.canSet(state)) {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedButton(onClick = {
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        scope.launch { repository.set(deviceId, PowerIntent.On) }
                    }, enabled = StatePolicy.canSet(state, PowerIntent.On), modifier = Modifier.weight(1f).heightIn(min = 48.dp),
                        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 10.dp)) {
                        AppIcon(AppIconType.Monitor); Spacer(Modifier.width(8.dp)); Text("开启")
                    }
                    OutlinedButton(onClick = {
                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                        scope.launch { repository.set(deviceId, PowerIntent.Off) }
                    }, enabled = StatePolicy.canSet(state, PowerIntent.Off), modifier = Modifier.weight(1f).heightIn(min = 48.dp),
                        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 10.dp)) {
                        AppIcon(AppIconType.MonitorOff); Spacer(Modifier.width(8.dp)); Text("关闭")
                    }
                }
            }
            if (action.intent != null || action.queryOperation) TextButton(
                onClick = { scope.launch { repository.refresh(deviceId) } }, enabled = !state.busy,
            ) {
                AppIcon(AppIconType.Refresh); Spacer(Modifier.width(8.dp)); Text("刷新当前状态")
            }
            TextButton(onClick = { details = !details }) {
                AppIcon(if (details) AppIconType.Up else AppIconType.Down)
                Spacer(Modifier.width(8.dp))
                Text(if (details) "收起详情" else "查看详情")
            }
            if (details) {
                HorizontalDivider()
                Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    device.lastSuccessfulRoute?.let { Text("最近成功路径：" + it.label, style = MaterialTheme.typography.bodySmall) }
                    device.status?.observation?.let {
                        Text("读回时间：" + DateTimeFormatter.ofPattern("MM-dd HH:mm:ss").withZone(ZoneId.systemDefault()).format(it.observedAt),
                            style = MaterialTheme.typography.bodySmall)
                    }
                    device.operation?.let { operation ->
                        Text("最近操作：" + when (operation.outcome) {
                            OperationOutcome.Confirmed -> "已确认"
                            OperationOutcome.AcceptedUnconfirmed -> "已接受，未确认"
                            OperationOutcome.Pending -> "处理中"
                            OperationOutcome.Unknown -> "结果未知"
                            OperationOutcome.Failed -> "未成功"
                        }, style = MaterialTheme.typography.bodySmall)
                        Text("操作编号：" + operation.operationId.toString().take(8), style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }
}
