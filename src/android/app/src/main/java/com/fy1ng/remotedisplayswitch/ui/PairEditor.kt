package com.fy1ng.remotedisplayswitch.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.fy1ng.remotedisplayswitch.pairing.LanPairing
import com.fy1ng.remotedisplayswitch.pairing.NearbyComputer
import com.fy1ng.remotedisplayswitch.pairing.ShortCodeProtocol
import com.fy1ng.remotedisplayswitch.core.DeviceRepository
import com.fy1ng.remotedisplayswitch.core.Protocol
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import java.time.Instant

object PairingInput {
    fun validate(text: String, now: Instant = Instant.now()): String {
        Protocol.pairBundle(text, now)
        return text
    }
}

/** One automatic attempt per target/code input. Failures require an edit or an explicit retry. */
internal class ShortCodeSubmission {
    private var lastInput: Pair<String, String>? = null

    fun edit() { lastInput = null }

    fun claim(target: String, code: String): Boolean {
        if (!ShortCodeProtocol.validCode(code)) return false
        val input = target to code
        if (lastInput == input) return false
        lastInput = input
        return true
    }
}

@Composable
fun PairEditor(repository: DeviceRepository, scope: CoroutineScope, busy: Boolean, onPaired: () -> Unit) {
    var bundle by remember { mutableStateOf("") }
    var clientName by remember { mutableStateOf(Build.MODEL.orEmpty().ifBlank { "Android 手机" }.take(80)) }
    var nearby by remember { mutableStateOf(false) }
    var advanced by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var scanning by remember { mutableStateOf(false) }
    var qrPairing by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val latestPaired by rememberUpdatedState(onPaired)
    val lan = remember(repository, context) { LanPairing(context, repository) { latestPaired() } }
    val lanState by lan.state.collectAsState()
    var selected by remember { mutableStateOf<NearbyComputer?>(null) }
    var shortCode by remember { mutableStateOf("") }
    val submissions = remember { ShortCodeSubmission() }
    fun targetKey(computer: NearbyComputer) = "${computer.sessionId}/${computer.host.hostAddress}/${computer.port}"
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val lanEnabled = nearby && bundle.isBlank() && !scanning && !qrPairing && (!busy || lanState.busy)
    DisposableEffect(lan, lifecycle, lanEnabled) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME && lanEnabled) lan.start()
            if (event == Lifecycle.Event.ON_PAUSE || event == Lifecycle.Event.ON_STOP || event == Lifecycle.Event.ON_DESTROY) {
                lan.stop()
                shortCode = ""
                selected = null
            }
        }
        lifecycle.addObserver(observer)
        if (lanEnabled && lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) lan.start()
        onDispose {
            lifecycle.removeObserver(observer)
            lan.stop()
            shortCode = ""
            selected = null
        }
    }
    val blocked = busy || scanning || qrPairing || lanState.busy
    LaunchedEffect(lanState.computers) {
        val current = selected
        if (current != null && lanState.computers.none { targetKey(it) == targetKey(current) }) {
            selected = null
            shortCode = ""
            submissions.edit()
        }
        if (selected == null && lanState.computers.size == 1) selected = lanState.computers.single()
    }
    fun submitCode(computer: NearbyComputer, retry: Boolean = false) {
        if (blocked || clientName.isBlank() || lanState.computers.none { targetKey(it) == targetKey(computer) }) return
        if (retry) submissions.edit()
        if (submissions.claim(targetKey(computer), shortCode)) lan.connect(computer, shortCode, clientName)
    }
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        scanning = false
        if (result.contents == null) {
            message = "已取消扫码。可重新扫描或选择其他配对方式。"
        } else {
            try {
                bundle = PairingInput.validate(result.contents)
                message = "已读取配对信息。请确认二维码来自你信任的电脑。"
            } catch (_: Exception) {
                bundle = ""
                message = "二维码无效或已过期。请在电脑重新生成。"
            }
        }
    }
    val launchScan = {
        lan.stop()
        shortCode = ""
        selected = null
        bundle = ""
        advanced = false
        message = null
        scanning = true
        try {
            scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setPrompt("扫描电脑上的配对二维码").setBeepEnabled(false)
                .setBarcodeImageEnabled(false).setOrientationLocked(false)
                .setCaptureActivity(PairCaptureActivity::class.java))
        } catch (_: Exception) {
            scanning = false
            message = "无法打开相机。可选择下方的其他配对方式。"
        }
    }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) launchScan() else {
            scanning = false
            message = "未获得相机权限。可在系统设置中允许相机，或选择其他配对方式。"
        }
    }
    val confirmBundle: () -> Unit = {
        val submitted = bundle
        val submittedName = clientName
        bundle = ""
        message = null
        lan.stop()
        qrPairing = true
        scope.launch {
            try {
                val parsed = try {
                    Protocol.pairBundle(PairingInput.validate(submitted), Instant.now())
                } catch (_: Exception) {
                    message = "配对信息无效或已过期。请在电脑重新获取。"
                    return@launch
                }
                val previous = repository.state.value.devices.find { it.profile.deviceId == parsed.deviceId }?.profile
                repository.pair(submitted, submittedName)
                val paired = repository.state.value.devices.find { it.profile.deviceId == parsed.deviceId }?.profile
                if (paired != null && paired !== previous) latestPaired()
            } finally {
                qrPairing = false
            }
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("扫描二维码", style = MaterialTheme.typography.titleLarge)
                Text("在电脑打开“连接手机”，显示配对二维码。", color = MaterialTheme.colorScheme.onSurfaceVariant)
                Button(onClick = {
                    if (context.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) launchScan()
                    else {
                        lan.stop()
                        shortCode = ""
                        selected = null
                        scanning = true
                        try {
                            permission.launch(Manifest.permission.CAMERA)
                        } catch (_: Exception) {
                            scanning = false
                            message = "无法申请相机权限。可选择其他配对方式。"
                        }
                    }
                }, enabled = !blocked, modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp)) {
                    AppIcon(AppIconType.Scan)
                    Spacer(Modifier.width(8.dp))
                    Text(if (bundle.isNotBlank() && !advanced) "重新扫描电脑二维码" else "扫描电脑二维码")
                }
                if (!advanced) {
                    message?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
                    if (qrPairing) {
                        LinearProgressIndicator(Modifier.fillMaxWidth())
                        Text("正在连接电脑…", style = MaterialTheme.typography.bodyMedium)
                    }
                }
                if (!advanced && bundle.isNotBlank()) {
                    PairConfirmation(clientName, { clientName = it }, !blocked, confirmBundle) {
                        bundle = ""
                        message = null
                    }
                }
            }
        }

        OutlinedCard(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                TextButton(onClick = {
                    nearby = !nearby
                    advanced = false
                    bundle = ""
                    message = null
                    shortCode = ""
                    selected = null
                    if (!nearby) lan.stop()
                }, enabled = !scanning && !qrPairing && (!busy || lanState.busy),
                    modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                    AppIcon(AppIconType.Monitor)
                    Spacer(Modifier.width(8.dp))
                    Text(if (nearby) "收起附近电脑与短码配对" else "其他方式：附近电脑与短码配对")
                }
                if (nearby) {
                    Text("在电脑打开“连接手机”，并让两端连接同一 Wi-Fi。输入电脑上显示的短码。", style = MaterialTheme.typography.bodyMedium)
                    if (lanState.busy) {
                        LinearProgressIndicator(Modifier.fillMaxWidth())
                        Text("正在配对，请保持本页在前台…")
                    }
                    lanState.message?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
                    if (lanState.computers.isNotEmpty()) {
                        HorizontalDivider()
                        Text("选择电脑", style = MaterialTheme.typography.titleSmall)
                        lanState.computers.forEach { computer ->
                            OutlinedButton(onClick = { selected = computer; shortCode = ""; submissions.edit() }, enabled = !blocked,
                                modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                                AppIcon(if (selected?.let(::targetKey) == targetKey(computer)) AppIconType.Check else AppIconType.Monitor)
                                Spacer(Modifier.width(8.dp))
                                Text(computer.name.filterNot(Char::isISOControl) + if (selected?.let(::targetKey) == targetKey(computer)) "（已选择）" else "")
                            }
                        }
                    } else if (!lanState.busy && lanState.message == null) {
                        Text("尚未发现电脑。请确认电脑已打开“连接手机”。",
                            style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    selected?.let { computer ->
                        OutlinedTextField(value = clientName, onValueChange = { if (it.length <= 80) clientName = it },
                            label = { Text("本手机名称") }, supportingText = { Text("配对后显示在电脑上") },
                            enabled = !blocked, singleLine = true, modifier = Modifier.fillMaxWidth())
                        OutlinedTextField(value = shortCode,
                            onValueChange = {
                                if (it.length <= 6 && it.all { digit -> digit in '0'..'9' } && it != shortCode) {
                                    shortCode = it
                                    submissions.edit()
                                    if (it.length == 6) submitCode(computer)
                                }
                            },
                            label = { Text("电脑上的六位短码") },
                            supportingText = { Text("输入满六位后自动连接。请核对 ${computer.name.filterNot(Char::isISOControl)} 上显示的短码。") },
                            singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            enabled = !blocked, modifier = Modifier.fillMaxWidth())
                        Button(onClick = { submitCode(computer, retry = true) }, enabled = !blocked && clientName.isNotBlank() && ShortCodeProtocol.validCode(shortCode) &&
                            lanState.computers.any { it.sessionId == computer.sessionId && it.host == computer.host && it.port == computer.port },
                            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                            AppIcon(AppIconType.Refresh)
                            Spacer(Modifier.width(8.dp))
                            Text("重新尝试连接")
                        }
                    }
                    TextButton(onClick = {
                        selected = null
                        shortCode = ""
                        submissions.edit()
                        lan.stop()
                        if (lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) lan.start()
                    }, enabled = !blocked, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                        AppIcon(AppIconType.Refresh)
                        Spacer(Modifier.width(8.dp))
                        Text("重新查找电脑")
                    }
                }
            }
        }

        OutlinedCard(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                TextButton(onClick = {
                    advanced = !advanced
                    nearby = false
                    bundle = ""
                    message = null
                    shortCode = ""
                    selected = null
                    lan.stop()
                }, enabled = !blocked, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
                    AppIcon(AppIconType.Paste)
                    Spacer(Modifier.width(8.dp))
                    Text(if (advanced) "收起高级备用方式" else "高级备用方式")
                }
                if (advanced) {
                    Text("粘贴电脑导出的配对 JSON。配对信息短时有效，请勿转发。",
                        style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    OutlinedTextField(value = bundle, onValueChange = {
                        if (it.length <= Protocol.MAX_BODY) {
                            bundle = it
                            message = null
                        }
                    },
                        label = { Text("配对 JSON") }, visualTransformation = PasswordVisualTransformation(),
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), enabled = !blocked,
                        modifier = Modifier.fillMaxWidth(), maxLines = 5)
                    message?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
                    if (qrPairing) {
                        LinearProgressIndicator(Modifier.fillMaxWidth())
                        Text("正在连接电脑…", style = MaterialTheme.typography.bodyMedium)
                    }
                    if (bundle.isNotBlank()) {
                        PairConfirmation(clientName, { clientName = it }, !blocked, confirmBundle) { bundle = "" }
                    }
                }
            }
        }
    }
}

@Composable
private fun PairConfirmation(
    clientName: String, onNameChange: (String) -> Unit, enabled: Boolean,
    onConfirm: () -> Unit, onCancel: () -> Unit,
) {
    OutlinedTextField(value = clientName, onValueChange = { if (it.length <= 80) onNameChange(it) },
        label = { Text("本手机名称") }, supportingText = { Text("配对后显示在电脑上") },
        enabled = enabled, singleLine = true, modifier = Modifier.fillMaxWidth())
    Button(onClick = onConfirm, enabled = enabled && clientName.isNotBlank(),
        modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
        AppIcon(AppIconType.Phone)
        Spacer(Modifier.width(8.dp))
        Text("确认连接")
    }
    TextButton(onClick = onCancel, enabled = enabled, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
        AppIcon(AppIconType.Delete)
        Spacer(Modifier.width(8.dp))
        Text("清除配对信息")
    }
}
