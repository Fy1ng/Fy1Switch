package com.fy1ng.remotedisplayswitch.ui

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
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

@Composable
fun PairEditor(repository: DeviceRepository, scope: CoroutineScope, busy: Boolean, onPaired: () -> Unit) {
    var bundle by remember { mutableStateOf("") }
    var clientName by remember { mutableStateOf("Android 手机") }
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
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val lanEnabled = !scanning && !qrPairing && (!busy || lanState.busy)
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
        onDispose { lifecycle.removeObserver(observer); lan.stop() }
    }
    val blocked = busy || scanning || qrPairing || lanState.busy
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        scanning = false
        if (result.contents == null) {
            message = "已取消扫码，可重试或粘贴配对 JSON。"
        } else {
            try {
                bundle = PairingInput.validate(result.contents)
                message = "已读取配对信息。请确认二维码来自你信任的电脑，然后配对。"
            } catch (_: Exception) {
                bundle = ""
                message = "二维码不是有效的配对信息，或已过期。不会打开网址或执行电源操作。"
            }
        }
    }
    val launchScan = {
        lan.stop()
        bundle = ""
        message = null
        scanning = true
        try {
            scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setPrompt("扫描电脑上的配对二维码").setBeepEnabled(false)
                .setBarcodeImageEnabled(false).setOrientationLocked(false)
                .setCaptureActivity(PairCaptureActivity::class.java))
        } catch (_: Exception) {
            scanning = false
            advanced = true
            message = "无法打开相机，可粘贴电脑导出的配对 JSON。"
        }
    }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) launchScan() else {
            scanning = false
            advanced = true
            message = "未获得相机权限。可粘贴配对 JSON；如需扫码，请在系统设置中允许相机权限。"
        }
    }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("配对电脑", style = MaterialTheme.typography.titleLarge)
            Text("附近电脑", style = MaterialTheme.typography.titleMedium)
            Text("两端打开配对页并连接同一局域网。设备名称仅供辨认，输入对方屏幕上的短码才验证配对。")
            lanState.code?.let { Text("本机短码：$it · 剩余 ${lanState.seconds} 秒", style = MaterialTheme.typography.titleLarge) }
            if (lanState.busy) Text("正在配对，请保持本页在前台…")
            lanState.message?.let { Text(it) }
            if (lanState.computers.isEmpty()) Text("尚未发现电脑。请检查电脑配对页、防火墙和 Wi-Fi；访客网络/AP 隔离可能阻止发现或连接。")
            lanState.computers.forEach { computer ->
                TextButton(onClick = { selected = computer; shortCode = "" }, enabled = !blocked) {
                    Text("${computer.name} · ${computer.host.hostAddress}:${computer.port}")
                }
            }
            TextButton(onClick = { selected = null; shortCode = ""; lan.stop(); lan.start() }, enabled = !blocked) { Text("刷新发现与本机短码") }
            selected?.let { computer ->
                OutlinedTextField(value = shortCode, onValueChange = { if (it.length <= 6 && it.all { digit -> digit in '0'..'9' }) shortCode = it },
                    label = { Text("${computer.name} 屏幕上的六位短码") }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                    visualTransformation = PasswordVisualTransformation(), enabled = !blocked, modifier = Modifier.fillMaxWidth())
                Button(onClick = { val submitted = shortCode; shortCode = ""; selected = null; lan.connect(computer, submitted) },
                    enabled = !blocked && ShortCodeProtocol.validCode(shortCode) && lanState.computers.any { it.sessionId == computer.sessionId && it.host == computer.host && it.port == computer.port }) {
                    Text("用短码配对")
                }
            }
            Text("在电脑上显示配对二维码，再用手机扫描。配对信息短时有效，请勿转发。首次配对需能直接连接电脑 HTTPS 地址。")
            Button(onClick = {
                if (context.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) launchScan()
                else {
                    lan.stop()
                    scanning = true
                    permission.launch(Manifest.permission.CAMERA)
                }
            }, enabled = !blocked, modifier = Modifier.fillMaxWidth()) { Text("扫描配对二维码") }
            message?.let { Text(it) }
            TextButton(onClick = { advanced = !advanced }, enabled = !blocked) {
                Text(if (advanced) "收起高级备用方式" else "高级备用：粘贴配对 JSON")
            }
            if (advanced) OutlinedTextField(
                value = bundle, onValueChange = { if (it.length <= Protocol.MAX_BODY) bundle = it },
                label = { Text("Windows 配对 JSON") }, visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                enabled = !blocked, modifier = Modifier.fillMaxWidth(), maxLines = 5,
            )
            if (bundle.isNotBlank()) {
                OutlinedTextField(value = clientName, onValueChange = { if (it.length <= 80) clientName = it },
                    label = { Text("本手机名称（电脑可见）") }, enabled = !blocked,
                    modifier = Modifier.fillMaxWidth())
                Button(onClick = {
                    val submitted = bundle
                    bundle = ""
                    message = null
                    lan.stop()
                    qrPairing = true
                    scope.launch {
                      try {
                        val parsed = try {
                            Protocol.pairBundle(PairingInput.validate(submitted), Instant.now())
                        } catch (_: Exception) {
                            message = "配对信息无效或已过期，请重新获取。"
                            return@launch
                        }
                        val previous = repository.state.value.devices.find { it.profile.deviceId == parsed.deviceId }?.profile
                        repository.pair(submitted, clientName)
                        val paired = repository.state.value.devices.find { it.profile.deviceId == parsed.deviceId }?.profile
                        if (paired != null && paired !== previous) onPaired()
                      } finally { qrPairing = false }
                    }
                }, enabled = !blocked && clientName.isNotBlank(), modifier = Modifier.fillMaxWidth()) {
                    Text("验证证书并配对")
                }
            }
        }
    }
}
