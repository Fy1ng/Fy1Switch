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
    val context = LocalContext.current
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
            Text("在电脑上显示配对二维码，再用手机扫描。配对信息短时有效，请勿转发。首次配对需能直接连接电脑 HTTPS 地址。")
            Button(onClick = {
                if (context.checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) launchScan()
                else {
                    scanning = true
                    permission.launch(Manifest.permission.CAMERA)
                }
            }, enabled = !busy && !scanning, modifier = Modifier.fillMaxWidth()) { Text("扫描配对二维码") }
            message?.let { Text(it) }
            TextButton(onClick = { advanced = !advanced }, enabled = !busy && !scanning) {
                Text(if (advanced) "收起高级备用方式" else "高级备用：粘贴配对 JSON")
            }
            if (advanced) OutlinedTextField(
                value = bundle, onValueChange = { if (it.length <= Protocol.MAX_BODY) bundle = it },
                label = { Text("Windows 配对 JSON") }, visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                enabled = !busy && !scanning, modifier = Modifier.fillMaxWidth(), maxLines = 5,
            )
            if (bundle.isNotBlank()) {
                OutlinedTextField(value = clientName, onValueChange = { if (it.length <= 80) clientName = it },
                    label = { Text("本手机名称（电脑可见）") }, enabled = !busy,
                    modifier = Modifier.fillMaxWidth())
                Button(onClick = {
                    val submitted = bundle
                    bundle = ""
                    message = null
                    scope.launch {
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
                    }
                }, enabled = !busy && !scanning && clientName.isNotBlank(), modifier = Modifier.fillMaxWidth()) {
                    Text("验证证书并配对")
                }
            }
        }
    }
}
