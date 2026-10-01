package io.fy1ng.fydisplay.ui

import android.app.Activity
import android.app.StatusBarManager
import android.content.ComponentName
import android.graphics.drawable.Icon
import android.os.Build
import android.os.Bundle
import android.service.quicksettings.TileService
import android.view.Gravity
import android.view.ViewGroup
import android.widget.*
import io.fy1ng.fydisplay.R
import io.fy1ng.fydisplay.network.AppConfig
import io.fy1ng.fydisplay.network.ConnectionMode
import io.fy1ng.fydisplay.network.RemoteClient
import io.fy1ng.fydisplay.tile.DisplayTileService
import java.util.concurrent.Executors

class MainActivity : Activity() {
    private val executor = Executors.newSingleThreadExecutor()
    private lateinit var directUrl: EditText
    private lateinit var relayUrl: EditText
    private lateinit var deviceId: EditText
    private lateinit var token: EditText
    private lateinit var mode: Spinner
    private lateinit var status: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        title = "FyDisplay Remote"
        setContentView(buildUi())
        loadConfig()
    }

    override fun onDestroy() {
        executor.shutdownNow()
        super.onDestroy()
    }

    private fun buildUi(): ScrollView {
        val density = resources.displayMetrics.density
        fun dp(v: Int) = (v * density).toInt()
        val scroll = ScrollView(this)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(24), dp(20), dp(24))
        }
        scroll.addView(root, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        root.addView(TextView(this).apply { text = "FyDisplay Remote"; textSize = 26f })
        root.addView(TextView(this).apply { text = "Tailscale 直连优先，中继自动兜底"; textSize = 14f; setPadding(0, dp(6), 0, dp(18)) })

        mode = Spinner(this).apply { adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, ConnectionMode.entries.map { it.name }) }
        addField(root, "连接模式", mode)
        directUrl = EditText(this).apply { hint = "http://100.x.x.x:27120" }
        relayUrl = EditText(this).apply { hint = "https://display.example.com" }
        deviceId = EditText(this).apply { hint = "fy-pc-main" }
        token = EditText(this).apply { hint = "长随机 Token"; inputType = 129 }
        addField(root, "Direct / Tailscale URL", directUrl)
        addField(root, "Relay URL", relayUrl)
        addField(root, "Device ID", deviceId)
        addField(root, "Token", token)

        root.addView(Button(this).apply {
            text = "保存配置"
            setOnClickListener { saveConfig(); toast("已保存") }
        }, matchWrap())

        root.addView(Button(this).apply {
            text = "测试连接"
            setOnClickListener {
                saveConfig()
                status.text = "正在连接…"
                executor.execute {
                    val result = RemoteClient(applicationContext).status()
                    runOnUiThread {
                        status.text = if (result.ok) "在线 · ${result.transport} · 显示器 ${result.displayState} (${result.powerState})" else "连接失败 · ${result.transport} · ${result.message}"
                    }
                }
            }
        }, matchWrap())

        root.addView(Button(this).apply {
            text = "添加到快捷设置"
            setOnClickListener { requestTile() }
        }, matchWrap())

        status = TextView(this).apply { text = "尚未测试"; setPadding(0, dp(16), 0, 0) }
        root.addView(status, matchWrap())
        return scroll
    }

    private fun addField(root: LinearLayout, label: String, view: android.view.View) {
        root.addView(TextView(this).apply { text = label; setPadding(0, 18, 0, 4) }, matchWrap())
        root.addView(view, matchWrap())
    }

    private fun matchWrap() = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { gravity = Gravity.CENTER_HORIZONTAL }

    private fun loadConfig() {
        val cfg = AppConfig.load(this)
        directUrl.setText(cfg.directUrl)
        relayUrl.setText(cfg.relayUrl)
        deviceId.setText(cfg.deviceId)
        token.setText(cfg.token)
        mode.setSelection(ConnectionMode.entries.indexOf(cfg.mode).coerceAtLeast(0))
    }

    private fun saveConfig() {
        AppConfig.save(this, AppConfig(directUrl.text.toString(), relayUrl.text.toString(), deviceId.text.toString(), token.text.toString(), ConnectionMode.entries[mode.selectedItemPosition]))
        TileService.requestListeningState(this, ComponentName(this, DisplayTileService::class.java))
    }

    private fun requestTile() {
        if (Build.VERSION.SDK_INT >= 33) {
            val sbm = getSystemService(StatusBarManager::class.java)
            sbm.requestAddTileService(ComponentName(this, DisplayTileService::class.java), "显示器", Icon.createWithResource(this, R.drawable.ic_monitor), mainExecutor) { result ->
                runOnUiThread { toast("系统返回：$result") }
            }
        } else {
            toast("请打开快捷设置编辑页面，手动添加“显示器”磁贴")
        }
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
}
