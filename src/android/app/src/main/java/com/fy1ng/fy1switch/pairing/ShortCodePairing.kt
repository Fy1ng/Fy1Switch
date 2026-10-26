package com.fy1ng.fy1switch.pairing

import com.fy1ng.fy1switch.core.DeviceRepository
import com.fy1ng.fy1switch.core.Feedback
import com.fy1ng.fy1switch.core.Protocol
import java.time.Instant

object ShortCodePairing {
    suspend fun acceptBundle(repository: DeviceRepository, text: String, clientName: String, onFailure: (String) -> Unit = {}): Boolean {
        val bundle = runCatching { Protocol.pairBundle(text, Instant.now()) }.getOrNull()
        if (bundle == null) {
            onFailure("配对信息无效或已过期. 请核对手机与电脑时间, 并在电脑重新开始配对.")
            return false
        }
        if (repository.state.value.devices.any { it.profile.deviceId == bundle.deviceId }) {
            onFailure("这台电脑已添加. 请返回主页选择电脑, 或在设备设置中重新配对.")
            return false
        }
        val feedback = repository.pair(text, clientName)
        val success = repository.state.value.devices.any { it.profile.deviceId == bundle.deviceId && it.profile.certificatePin == bundle.certificatePin }
        if (!success) onFailure(when (feedback) {
            Feedback.Security -> "配对证书验证失败. 请核对手机时间, 并在电脑重新开始配对."
            Feedback.Offline -> "无法连接电脑的 HTTPS 服务. 请检查局域网连接和 Windows 防火墙, 并在电脑重新开始配对."
            Feedback.InvalidInput, Feedback.Expired -> "HTTPS 配对信息已失效或被电脑拒绝. 请核对手机与电脑时间, 并在电脑重新开始配对."
            else -> feedback.text
        })
        return success
    }
}
