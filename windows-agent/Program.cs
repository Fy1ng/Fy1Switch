using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace FyDisplayAgent;

public sealed class AgentConfig
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = $"fy-pc-{Environment.MachineName.ToLowerInvariant()}";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 27120;
    [JsonPropertyName("monitor_id")] public string MonitorId { get; set; } = "";
    [JsonPropertyName("monitor_number")] public int MonitorNumber { get; set; } = 1;
    [JsonPropertyName("powertoys_cli")] public string PowerToysCli { get; set; } = "";
    [JsonPropertyName("off_power_state")] public string OffPowerState { get; set; } = "0x04";
    [JsonPropertyName("on_power_state")] public string OnPowerState { get; set; } = "0x01";
    [JsonPropertyName("relay_url")] public string RelayUrl { get; set; } = "";
    [JsonPropertyName("relay_poll_seconds")] public int RelayPollSeconds { get; set; } = 25;
}

public sealed class ApiResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("display_state")] public string? DisplayState { get; set; }
    [JsonPropertyName("power_state")] public string? PowerState { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class RelayCommand
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
}

public sealed class RelayResult
{
    [JsonPropertyName("command_id")] public string CommandId { get; set; } = "";
    [JsonPropertyName("result")] public ApiResponse Result { get; set; } = new();
}

public sealed class ConfigStore
{
    public string PathOnDisk { get; }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigStore(string path) => PathOnDisk = path;

    public (AgentConfig Config, bool Created) LoadOrCreate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
        if (!File.Exists(PathOnDisk))
        {
            var cfg = new AgentConfig { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
            Save(cfg);
            return (cfg, true);
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(PathOnDisk), JsonOptions) ?? new AgentConfig();
            if (string.IsNullOrWhiteSpace(cfg.DeviceId)) cfg.DeviceId = $"fy-pc-{Environment.MachineName.ToLowerInvariant()}";
            if (string.IsNullOrWhiteSpace(cfg.Token)) cfg.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (cfg.Port is < 1 or > 65535) cfg.Port = 27120;
            if (cfg.MonitorNumber < 1) cfg.MonitorNumber = 1;
            if (string.IsNullOrWhiteSpace(cfg.OffPowerState)) cfg.OffPowerState = "0x04";
            if (string.IsNullOrWhiteSpace(cfg.OnPowerState)) cfg.OnPowerState = "0x01";
            if (cfg.RelayPollSeconds is < 1 or > 30) cfg.RelayPollSeconds = 25;
            Save(cfg);
            return (cfg, false);
        }
        catch
        {
            var backup = PathOnDisk + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(PathOnDisk, backup, true); } catch { }
            var cfg = new AgentConfig { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
            Save(cfg);
            return (cfg, true);
        }
    }

    public void Save(AgentConfig cfg)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOnDisk)!);
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(cfg, JsonOptions));
    }
}

public sealed class AgentService : IDisposable
{
    private static readonly Regex HexState = new("0x[0-9a-fA-F]{2,4}", RegexOptions.Compiled);
    private readonly ConfigStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private WebApplication? _app;
    private Task? _relayTask;
    private volatile AgentConfig _config;
    private volatile bool _relayOnline;
    private volatile string _lastRelayMessage = "disabled";

    public event EventHandler? StatusChanged;
    public AgentConfig Config => _config;
    public bool RelayOnline => _relayOnline;
    public string LastRelayMessage => _lastRelayMessage;

    public AgentService(AgentConfig config, ConfigStore store)
    {
        _config = config;
        _store = store;
    }

    public void Start()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(_config.Port));
        var app = builder.Build();

        app.MapGet("/v1/status", async (HttpRequest req) => await HandleHttpAsync(req, "status"));
        app.MapGet("/v1/display", async (HttpRequest req) => await HandleHttpAsync(req, "status"));
        app.MapPost("/v1/display/on", async (HttpRequest req) => await HandleHttpAsync(req, "on"));
        app.MapPost("/v1/display/off", async (HttpRequest req) => await HandleHttpAsync(req, "off"));
        app.MapPost("/v1/display/toggle", async (HttpRequest req) => await HandleHttpAsync(req, "toggle"));

        app.StartAsync(_cts.Token).GetAwaiter().GetResult();
        _app = app;
        _relayTask = Task.Run(() => RelayLoopAsync(_cts.Token));
    }

    public void UpdateConfig(AgentConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceId)) throw new ArgumentException("Device ID cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.Token)) throw new ArgumentException("Token cannot be empty.");
        if (config.MonitorNumber < 1) config.MonitorNumber = 1;
        config.RelayUrl = config.RelayUrl.Trim().TrimEnd('/');
        _store.Save(config);
        _config = config;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool Authorized(HttpRequest req)
        => string.Equals(req.Headers.Authorization.ToString(), "Bearer " + _config.Token, StringComparison.Ordinal);

    private async Task<IResult> HandleHttpAsync(HttpRequest req, string action)
    {
        if (!Authorized(req))
            return Results.Json(new ApiResponse { Ok = false, Message = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        var result = await ExecuteAsync(action);
        return Results.Json(result, statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway);
    }

    public async Task<ApiResponse> ExecuteAsync(string action)
    {
        var cfg = _config;
        try
        {
            return action switch
            {
                "status" => ResponseForState(cfg, await ReadPowerStateAsync(cfg), null),
                "on" => await SetAndRespondAsync(cfg, cfg.OnPowerState, false, "display power-on command sent"),
                "off" => await SetAndRespondAsync(cfg, cfg.OffPowerState, true, "display power-off command sent"),
                "toggle" => await ToggleAsync(cfg),
                _ => new ApiResponse { Ok = false, DeviceId = cfg.DeviceId, Message = "unsupported action" }
            };
        }
        catch (Exception ex)
        {
            return new ApiResponse { Ok = false, DeviceId = cfg.DeviceId, Message = ex.Message };
        }
    }

    private async Task<ApiResponse> ToggleAsync(AgentConfig cfg)
    {
        var state = await ReadPowerStateAsync(cfg);
        return string.Equals(state, cfg.OnPowerState, StringComparison.OrdinalIgnoreCase)
            ? await SetAndRespondAsync(cfg, cfg.OffPowerState, true, "display power-off command sent")
            : await SetAndRespondAsync(cfg, cfg.OnPowerState, false, "display power-on command sent");
    }

    private async Task<ApiResponse> SetAndRespondAsync(AgentConfig cfg, string state, bool confirmOff, string message)
    {
        await SetPowerStateAsync(cfg, state, confirmOff);
        return ResponseForState(cfg, state, message);
    }

    private static ApiResponse ResponseForState(AgentConfig cfg, string state, string? message)
    {
        var normalized = state.ToLowerInvariant();
        return new ApiResponse
        {
            Ok = true,
            DeviceId = cfg.DeviceId,
            DisplayState = string.Equals(normalized, cfg.OnPowerState.ToLowerInvariant(), StringComparison.Ordinal) ? "on" : "off",
            PowerState = normalized,
            Message = message
        };
    }

    public string DiscoverPowerToysCli()
    {
        var cfg = _config;
        if (!string.IsNullOrWhiteSpace(cfg.PowerToysCli) && File.Exists(cfg.PowerToysCli)) return cfg.PowerToysCli;

        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(System.IO.Path.Combine(programFiles, "PowerToys", "PowerToys.PowerDisplay.Cli.exe"));
            candidates.Add(System.IO.Path.Combine(programFiles, "Microsoft", "PowerToys", "PowerToys.PowerDisplay.Cli.exe"));
        }
        if (!string.IsNullOrWhiteSpace(local))
        {
            candidates.Add(System.IO.Path.Combine(local, "PowerToys", "PowerToys.PowerDisplay.Cli.exe"));
            candidates.Add(System.IO.Path.Combine(local, "Microsoft", "PowerToys", "PowerToys.PowerDisplay.Cli.exe"));
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(System.IO.Path.Combine(dir.Trim(), "PowerToys.PowerDisplay.Cli.exe"));

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private string[] MonitorArgs(AgentConfig cfg)
        => string.IsNullOrWhiteSpace(cfg.MonitorId)
            ? ["--monitor-number", cfg.MonitorNumber.ToString()]
            : ["--monitor-id", cfg.MonitorId];

    private async Task<string> ReadPowerStateAsync(AgentConfig cfg)
    {
        var args = new List<string> { "get" };
        args.AddRange(MonitorArgs(cfg));
        args.AddRange(["--setting", "power-state"]);
        var output = await RunPowerToysAsync(cfg, args, TimeSpan.FromSeconds(7));
        var match = HexState.Match(output);
        if (!match.Success) throw new InvalidOperationException("Could not parse power-state from PowerToys output: " + output.Trim());
        return match.Value.ToLowerInvariant();
    }

    private async Task SetPowerStateAsync(AgentConfig cfg, string state, bool confirmOff)
    {
        var args = new List<string> { "set" };
        args.AddRange(MonitorArgs(cfg));
        args.AddRange(["--power-state", state]);
        if (confirmOff) args.Add("--confirm-power-off");
        args.Add("--quiet");
        _ = await RunPowerToysAsync(cfg, args, TimeSpan.FromSeconds(9));
    }

    private async Task<string> RunPowerToysAsync(AgentConfig cfg, IEnumerable<string> args, TimeSpan timeout)
    {
        var cli = !string.IsNullOrWhiteSpace(cfg.PowerToysCli) && File.Exists(cfg.PowerToysCli) ? cfg.PowerToysCli : DiscoverPowerToysCli();
        if (string.IsNullOrWhiteSpace(cli))
            throw new FileNotFoundException("PowerToys.PowerDisplay.Cli.exe was not found. Open Settings and set the PowerToys CLI path.");

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start PowerToys CLI.");
        using var cts = new CancellationTokenSource(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("PowerToys CLI timed out.");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PowerToys CLI failed ({process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private async Task RelayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config;
            if (string.IsNullOrWhiteSpace(cfg.RelayUrl))
            {
                SetRelay(false, "disabled");
                await DelaySafe(1500, ct);
                continue;
            }

            try
            {
                var wait = Math.Clamp(cfg.RelayPollSeconds, 1, 30);
                var url = $"{cfg.RelayUrl.TrimEnd('/')}/v1/agent/poll?device_id={Uri.EscapeDataString(cfg.DeviceId)}&wait={wait}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
                req.Headers.UserAgent.ParseAdd("FyDisplayAgent/0.2");
                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.NoContent)
                {
                    SetRelay(true, "connected");
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Relay HTTP {(int)resp.StatusCode}: {body.Trim()}");
                var cmd = JsonSerializer.Deserialize<RelayCommand>(body) ?? throw new InvalidOperationException("Relay returned an invalid command.");
                if (string.IsNullOrWhiteSpace(cmd.Id) || string.IsNullOrWhiteSpace(cmd.Action)) throw new InvalidOperationException("Relay returned an incomplete command.");
                SetRelay(true, "connected");
                var result = await ExecuteAsync(cmd.Action);
                await PostRelayResultAsync(cfg, cmd.Id, result, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetRelay(false, ex.Message);
                await DelaySafe(3000, ct);
            }
        }
    }

    private async Task PostRelayResultAsync(AgentConfig cfg, string commandId, ApiResponse result, CancellationToken ct)
    {
        var url = $"{cfg.RelayUrl.TrimEnd('/')}/v1/agent/result?device_id={Uri.EscapeDataString(cfg.DeviceId)}";
        var json = JsonSerializer.Serialize(new RelayResult { CommandId = commandId, Result = result });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Relay result HTTP {(int)resp.StatusCode}: {(await resp.Content.ReadAsStringAsync(ct)).Trim()}");
    }

    private void SetRelay(bool online, string message)
    {
        var changed = _relayOnline != online || _lastRelayMessage != message;
        _relayOnline = online;
        _lastRelayMessage = message;
        if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task DelaySafe(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _app?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
        try { _relayTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _http.Dispose();
        _cts.Dispose();
    }
}

public sealed class MainForm : Form
{
    private readonly AgentService _service;
    private readonly ConfigStore _store;
    private readonly NotifyIcon _tray;
    private readonly TextBox _deviceId = new();
    private readonly TextBox _token = new();
    private readonly TextBox _monitorId = new();
    private readonly NumericUpDown _monitorNumber = new() { Minimum = 1, Maximum = 32, Value = 1 };
    private readonly TextBox _cliPath = new();
    private readonly TextBox _relayUrl = new();
    private readonly Label _apiStatus = new() { AutoSize = true };
    private readonly Label _powerToysStatus = new() { AutoSize = true };
    private readonly Label _relayStatus = new() { AutoSize = true };
    private readonly Label _displayStatus = new() { AutoSize = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill };
    private readonly CheckBox _autoStart = new() { Text = "Start with Windows" };
    private bool _reallyExit;

    public MainForm(AgentService service, ConfigStore store, bool firstRun)
    {
        _service = service;
        _store = store;
        Text = "FyDisplay Remote - Windows Agent";
        Width = 720;
        Height = 650;
        MinimumSize = new Size(640, 560);
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
            await RefreshStatusAsync();
            if (firstRun)
            {
                AppendLog("First run: a random device token was generated automatically.");
                _tray.ShowBalloonTip(3000, "FyDisplay Remote", "Windows Agent is running. Configure Android with the Device ID and Token shown in the app.", ToolTipIcon.Info);
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
        AddRow(root, "PowerToys", _powerToysStatus);
        AddRow(root, "Relay", _relayStatus);
        AddRow(root, "Display", _displayStatus);
        AddRow(root, "Device ID", _deviceId);

        var tokenPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _token.UseSystemPasswordChar = true;
        tokenPanel.Controls.Add(_token, 0, 0);
        var copyToken = new Button { Text = "Copy", AutoSize = true };
        copyToken.Click += (_, _) => { Clipboard.SetText(_token.Text); AppendLog("Token copied to clipboard."); };
        tokenPanel.Controls.Add(copyToken, 1, 0);
        AddRow(root, "Token", tokenPanel);

        AddRow(root, "Monitor ID", _monitorId);
        AddRow(root, "Monitor number", _monitorNumber);

        var cliPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        cliPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cliPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cliPanel.Controls.Add(_cliPath, 0, 0);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "PowerToys CLI|PowerToys.PowerDisplay.Cli.exe|Executables|*.exe" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _cliPath.Text = dlg.FileName;
        };
        cliPanel.Controls.Add(browse, 1, 0);
        AddRow(root, "PowerToys CLI", cliPanel);
        AddRow(root, "Relay URL", _relayUrl);
        AddRow(root, "Startup", _autoStart);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var save = new Button { Text = "Save settings", AutoSize = true };
        var refresh = new Button { Text = "Refresh status", AutoSize = true };
        var on = new Button { Text = "Display ON", AutoSize = true };
        var off = new Button { Text = "Display OFF", AutoSize = true };
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
        _monitorId.Text = cfg.MonitorId;
        _monitorNumber.Value = Math.Clamp(cfg.MonitorNumber, 1, 32);
        _cliPath.Text = cfg.PowerToysCli;
        _relayUrl.Text = cfg.RelayUrl;
        _autoStart.Checked = IsAutoStartEnabled();
    }

    private void SaveSettings()
    {
        try
        {
            var oldPort = _service.Config.Port;
            var cfg = new AgentConfig
            {
                DeviceId = _deviceId.Text.Trim(),
                Token = _token.Text.Trim(),
                Port = oldPort,
                MonitorId = _monitorId.Text.Trim(),
                MonitorNumber = (int)_monitorNumber.Value,
                PowerToysCli = _cliPath.Text.Trim(),
                OffPowerState = _service.Config.OffPowerState,
                OnPowerState = _service.Config.OnPowerState,
                RelayUrl = _relayUrl.Text.Trim(),
                RelayPollSeconds = _service.Config.RelayPollSeconds
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
        var cli = _service.DiscoverPowerToysCli();
        _powerToysStatus.Text = string.IsNullOrWhiteSpace(cli) ? "Not found" : cli;
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
            AppendLog(r.Ok ? $"Display status: {r.DisplayState} ({r.PowerState})" : "Status error: " + r.Message);
            UpdateConnectionLabels();
        });
    }

    private async Task RunActionAsync(string action)
    {
        var r = await _service.ExecuteAsync(action);
        SafeUi(() =>
        {
            _displayStatus.Text = r.Ok ? $"{r.DisplayState} ({r.PowerState})" : "Unavailable";
            AppendLog(r.Ok ? $"{action}: {r.DisplayState} ({r.PowerState})" : $"{action} failed: {r.Message}");
        });
    }

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

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var configPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FyDisplayRemote", "config.json");
        var store = new ConfigStore(configPath);
        var (config, created) = store.LoadOrCreate();
        using var service = new AgentService(config, store);

        try
        {
            service.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"FyDisplay Remote could not start the local API on port {config.Port}.\n\n{ex.Message}", "FyDisplay Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var form = new MainForm(service, store, created);
        Application.Run(form);
    }
}
