package io.fy1ng.fydisplay.network

import android.content.Context

enum class ConnectionMode { AUTO, DIRECT, RELAY }

data class AppConfig(
    val directUrl: String,
    val relayUrl: String,
    val deviceId: String,
    val token: String,
    val mode: ConnectionMode,
) {
    companion object {
        private const val PREFS = "fy_display_remote"

        fun load(context: Context): AppConfig {
            val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val mode = runCatching { ConnectionMode.valueOf(p.getString("mode", "AUTO") ?: "AUTO") }
                .getOrDefault(ConnectionMode.AUTO)
            return AppConfig(
                directUrl = p.getString("direct_url", "http://100.64.0.1:27120") ?: "",
                relayUrl = p.getString("relay_url", "") ?: "",
                deviceId = p.getString("device_id", "fy-pc-main") ?: "fy-pc-main",
                token = p.getString("token", "") ?: "",
                mode = mode,
            )
        }

        fun save(context: Context, config: AppConfig) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putString("direct_url", config.directUrl.trimEnd('/'))
                .putString("relay_url", config.relayUrl.trimEnd('/'))
                .putString("device_id", config.deviceId.trim())
                .putString("token", config.token.trim())
                .putString("mode", config.mode.name)
                .apply()
        }
    }
}
