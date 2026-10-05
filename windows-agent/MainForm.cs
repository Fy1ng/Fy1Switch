using System.Diagnostics;
using Microsoft.Win32;

namespace FyDisplayAgent;

public sealed class MainForm : Form
{
    private readonly AgentService _service;
    private readonly ConfigStore _store;
    private readonly NotifyIcon _tray;
    private readonly TextBox _deviceId = new();
    private readonly TextBox _token = new();
    private readonly ComboBox _monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _relayUrl = new();
    private readonly Label _apiStatus = new() { AutoSize = true };
    private readonly Label _ddcStatus = new() { AutoSize = true };
    private readonly Label _relayStatus = new() { AutoSize = true };
    private readonly Label _displayStatus = new() { AutoSize = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 160, Dock = DockStyle.Fill };
    private readonly CheckBox _autoStart = new() { Text = "Start with Windows" };
    private bool _reallyExit;

    public MainForm(AgentService service, ConfigStore store, bool firstRun)
    {
        _service = service;
        _store = store;
        Text = "FyDisplay Remote - Windows Agent v0.3.2";
        Width = 860;
        Height = 680;
        MinimumSize = new Size(720, 580);
        StartPosition = FormStartPosition.CenterScreen;

        _tray = new NotifyIcon
        {
            Text = "FyDisplay Remote",
            Icon = SystemIcons.Application,
            Visible = true
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Toggle display", null, async (_, _) => await RunActionAsync("toggle"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        BuildUi();
        LoadConfigToUi();
        _service.StatusChanged += (_, _) => SafeUi(UpdateConnectionLabels);
        Shown += async (_, _) =>
        {
            UpdateConnectionLabels();
            await RefreshMonitorsAsync();
            await RefreshStatusAsync();
            if (firstRun)
            {
                AppendLog("First run: generated a random device token. Native DDC/CI control is built in; PowerToys is not required.");
                _tray.ShowBalloonTip(3500, "FyDisplay Remote", "Agent is running. Select the MCCS-capable physical monitor, then configure Android with the Device ID and Token.", ToolTipIcon.Info);
            }
        };
        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        AddRow(root, "API", _apiStatus);
        AddRow(root, "DDC/CI", _ddcStatus);
        AddRow(root, "Relay", _relayStatus);
        AddRow(root, "Display", _displayStatus);
        AddRow(root, "Device ID", _deviceId);

        var tokenPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _token.UseSystemPasswordChar = true;
        tokenPanel.Controls.Add(_token, 0, 0);
        var reveal = new Button { Text = "Show", AutoSize = true };
        reveal.Click += (_, _) =>
        {
            _token.UseSystemPasswordChar = !_token.UseSystemPasswordChar;
            reveal.Text = _token.UseSystemPasswordChar ? "Show" : "Hide";
        };
        var copyToken = new Button { Text = "Copy", AutoSize = true };
        copyToken.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_token.Text)) Clipboard.SetText(_token.Text);
            AppendLog("Token copied to clipboard.");
        };
        tokenPanel.Controls.Add(reveal, 1, 0);
        tokenPanel.Controls.Add(copyToken, 2, 0);
        AddRow(root, "Token", tokenPanel);

        var monitorPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        monitorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        monitorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monitorPanel.Controls.Add(_monitors, 0, 0);
        var refreshMonitors = new Button { Text = "Rescan", AutoSize = true };
        refreshMonitors.Click += async (_, _) => await RefreshMonitorsAsync();
        monitorPanel.Controls.Add(refreshMonitors, 1, 0);
        AddRow(root, "Monitor", monitorPanel);

        AddRow(root, "Relay URL", _relayUrl);
        AddRow(root, "Startup", _autoStart);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var save = new Button { Text = "Save settings", AutoSize = true };
        var refresh = new Button { Text = "Refresh status", AutoSize = true };
        var on = new Button { Text = "Display ON", AutoSize = true };
        var off = new Button { Text = "Display OFF (DPM)", AutoSize = true };
        var toggle = new Button { Text = "Toggle", AutoSize = true };
        var configFolder = new Button { Text = "Open config folder", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        refresh.Click += async (_, _) => await RefreshStatusAsync();
        on.Click += async (_, _) => await RunActionAsync("on");
        off.Click += async (_, _) => await RunActionAsync("off");
        toggle.Click += async (_, _) => await RunActionAsync("toggle");
        configFolder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_store.PathOnDisk}\"") { UseShellExecute = true });
        buttons.Controls.AddRange([save, refresh, on, off, toggle, configFolder]);
        AddRow(root, "Actions", buttons);

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Text = "Mirror/VDD-safe selection: the agent prefers a monitor that returns a real MCCS capabilities string (especially one advertising VCP D6). A successful D6 read alone is not treated as proof that writes are supported. OFF uses 0x04 (Off/DPM)."
        };
        AddRow(root, "Control", hint);
        AddRow(root, "Log", _log);
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control control)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 0, 0) };
        control.Dock = control is Label ? DockStyle.None : DockStyle.Fill;
        panel.Controls.Add(l, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private void LoadConfigToUi()
    {
        var cfg = _service.Config;
        _deviceId.Text = cfg.DeviceId;
        _token.Text = cfg.Token;
        _relayUrl.Text = cfg.RelayUrl;
        _autoStart.Checked = IsAutoStartEnabled();
    }

    private async Task RefreshMonitorsAsync()
    {
        try
        {
            _ddcStatus.Text = "Scanning…";
            var list = await _service.EnumerateMonitorsAsync();
            SafeUi(() =>
            {
                var cfg = _service.Config;
                _monitors.BeginUpdate();
                _monitors.Items.Clear();
                foreach (var m in list) _monitors.Items.Add(m);
                _monitors.EndUpdate();

                if (list.Count == 0)
                {
                    _ddcStatus.Text = "No external DDC/CI monitor found";
                    AppendLog("No DDC/CI monitor found. Check the monitor OSD setting and cable/adapter support.");
                    return;
                }

                foreach (var m in list)
                {
                    var caps = m.AdvertisesPowerControl
                        ? "MCCS capabilities advertise D6"
                        : m.HasCapabilities
                            ? "MCCS capabilities present (D6 not advertised)"
                            : $"no MCCS capabilities ({m.CapabilitiesError ?? "unavailable"})";
                    var read = m.SupportsPowerRead
                        ? $"D6 read=0x{m.PowerState.GetValueOrDefault():X2}"
                        : $"D6 read unavailable ({m.ProbeError ?? "read failed"})";
                    AppendLog($"Probe {m.Key}: {caps}; {read}; {m.Description}.");
                    if (!string.IsNullOrWhiteSpace(m.CapabilitiesSummary))
                        AppendLog($"Caps {m.Key}: {m.CapabilitiesSummary}");
                }

                // Automatic UI selection follows the same rule as command execution: capability
                // evidence beats a successful D6 read because the VDD can fake the latter.
                var selected = list.ToList().FindIndex(m =>
                    string.Equals(m.Key, cfg.MonitorId, StringComparison.OrdinalIgnoreCase) && m.AdvertisesPowerControl);
                if (selected < 0) selected = list.ToList().FindIndex(m => m.AdvertisesPowerControl);

                if (selected < 0)
                {
                    selected = list.ToList().FindIndex(m =>
                        string.Equals(m.Key, cfg.MonitorId, StringComparison.OrdinalIgnoreCase) && m.HasCapabilities);
                    if (selected < 0) selected = list.ToList().FindIndex(m => m.HasCapabilities);
                }

                if (selected < 0)
                {
                    selected = list.ToList().FindIndex(m => string.Equals(m.Key, cfg.MonitorId, StringComparison.OrdinalIgnoreCase));
                    if (selected < 0) selected = Math.Clamp(cfg.MonitorNumber - 1, 0, list.Count - 1);
                }

                _monitors.SelectedIndex = selected;
                var capsCount = list.Count(m => m.HasCapabilities);
                var d6CapsCount = list.Count(m => m.AdvertisesPowerControl);
                var readableCount = list.Count(m => m.SupportsPowerRead);
                _ddcStatus.Text = $"Detected {list.Count}; MCCS {capsCount}; D6 caps {d6CapsCount}; D6 readable {readableCount}";
                AppendLog($"DDC/CI scan found {list.Count} candidate(s); {capsCount} returned MCCS capabilities, {d6CapsCount} advertise D6, {readableCount} answered a D6 read.");
                AppendLog($"Auto-selected {_monitors.SelectedItem}.");
            });
        }
        catch (Exception ex)
        {
            SafeUi(() =>
            {
                _ddcStatus.Text = "Unavailable";
                AppendLog("DDC/CI scan error: " + ex.Message);
            });
        }
    }

    private void SaveSettings()
    {
        try
        {
            var old = _service.Config;
            var selected = _monitors.SelectedItem as DdcMonitorInfo;
            var cfg = new AgentConfig
            {
                DeviceId = _deviceId.Text.Trim(),
                Token = _token.Text.Trim(),
                Port = old.Port,
                MonitorId = selected?.Key ?? old.MonitorId,
                MonitorNumber = _monitors.SelectedIndex >= 0 ? _monitors.SelectedIndex + 1 : old.MonitorNumber,
                OffPowerState = old.OffPowerState,
                OnPowerState = old.OnPowerState,
                LastPowerState = old.LastPowerState,
                RelayUrl = _relayUrl.Text.Trim(),
                RelayPollSeconds = old.RelayPollSeconds,
                LegacyPowerToysCli = ""
            };
            _service.UpdateConfig(cfg);
            SetAutoStart(_autoStart.Checked);
            AppendLog("Settings saved.");
            UpdateConnectionLabels();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateConnectionLabels()
    {
        var cfg = _service.Config;
        _apiStatus.Text = $"Listening on 0.0.0.0:{cfg.Port} (LAN/Tailscale)";
        _relayStatus.Text = string.IsNullOrWhiteSpace(cfg.RelayUrl)
            ? "Disabled"
            : (_service.RelayOnline ? "Connected" : "Disconnected: " + _service.LastRelayMessage);
    }

    private async Task RefreshStatusAsync()
    {
        var r = await _service.ExecuteAsync("status");
        SafeUi(() =>
        {
            _displayStatus.Text = r.Ok ? $"{r.DisplayState} ({r.PowerState})" : "Unavailable";
            AppendLog(r.Ok ? $"Display status: {r.DisplayState} ({r.PowerState}){FormatNote(r.Message)}" : "Status error: " + r.Message);
            UpdateConnectionLabels();
        });
    }

    private async Task RunActionAsync(string action)
    {
        var r = await _service.ExecuteAsync(action);
        SafeUi(() =>
        {
            _displayStatus.Text = r.Ok ? $"{r.DisplayState} ({r.PowerState})" : "Unavailable";
            AppendLog(r.Ok ? $"{action}: {r.DisplayState} ({r.PowerState}){FormatNote(r.Message)}" : $"{action} failed: {r.Message}");
        });
    }

    private static string FormatNote(string? note) => string.IsNullOrWhiteSpace(note) ? "" : " - " + note;

    private void AppendLog(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(text)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(action); } catch { } }
        else action();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1800, "FyDisplay Remote", "Agent is still running in the notification area.", ToolTipIcon.Info);
        }
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        _tray.Visible = false;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue("FyDisplayRemote") is string;
    }

    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                        ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("FyDisplayRemote", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("FyDisplayRemote", false);
    }
}
