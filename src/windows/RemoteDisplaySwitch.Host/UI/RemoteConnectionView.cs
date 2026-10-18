using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QRCoder;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Remote;

namespace RemoteDisplaySwitch.Host.UI;

internal sealed class RemoteConnectionView : IAsyncDisposable
{
    private readonly RemoteManager _manager;
    private readonly Window _owner;
    private readonly Action<string> _copyToClipboard;
    private bool _pairingVisible, _settingsVisible;
    private Task _closeTask = Task.CompletedTask;
    private Task _activeAction = Task.CompletedTask;
    private long _visibilityGeneration;
    private readonly LanPairingCoordinator _lan;
    private readonly CheckBox _enabled = new() { Content = "启用手机控制", Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox _networks = new() { DisplayMemberPath = "Label", MinHeight = 34 };
    private readonly TextBox _listen = new(), _port = new(), _endpoint = new(), _tailscaleListen = new(), _tailscaleEndpoint = new(), _relay = new();
    private readonly PasswordBox _relayToken = new();
    private readonly TextBlock _status = Text("");
    private readonly TextBlock _pairStatus = Text("");
    private readonly TextBlock _codeLabel = Text("配对码", 12);
    private readonly TextBlock _code = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _clients = new() { DisplayMemberPath = "Label", MinHeight = 60, MaxHeight = 160, ToolTip = "显示授权时间，不表示手机当前在线。" };
    private readonly StackPanel _configuration = new();
    private readonly List<Button> _actionButtons = new();
    private readonly Button _primary, _useNetwork, _copy, _revoke;
    private readonly Image _qr = new() { Width = 220, Height = 220, Visibility = Visibility.Collapsed };
    private readonly Border _qrFrame;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairBundle? _currentBundle;
    private bool _busy, _stopping, _closed, _sessionOpen, _successCleanup;
    private int _knownClients;
    private long _pairGeneration;
    private string? _notice;
    private DateTimeOffset? _noticeUntil;

    public RemoteConnectionView(RemoteManager manager, Window owner, Action<string>? copyToClipboard = null)
    {
        _manager = manager; _owner = owner; _copyToClipboard = copyToClipboard ?? Clipboard.SetText;
        _lan = new LanPairingCoordinator(manager);
        _lan.Changed += LanChanged;

        var pairing = new StackPanel { Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center };
        _qrFrame = new Border { Background = Brushes.White, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Child = _qr, HorizontalAlignment = HorizontalAlignment.Center };
        pairing.Children.Add(_qrFrame);
        _codeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        pairing.Children.Add(_codeLabel);
        _code.HorizontalAlignment = HorizontalAlignment.Center;
        pairing.Children.Add(_code);
        _pairStatus.TextAlignment = TextAlignment.Center;
        _pairStatus.MaxWidth = 300;
        _pairStatus.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        pairing.Children.Add(_pairStatus);
        PairingContent = pairing;

        var page = new StackPanel();
        page.Children.Add(Text("手机控制", 18, true)); page.Children.Add(_status);
        _configuration.Children.Add(_enabled);
        _configuration.Children.Add(Text("请选择手机能够访问的电脑网络。"));
        Field(_configuration, "电脑当前网络", _networks);
        var networkButtons = new WrapPanel();
        _useNetwork = AddButton(networkButtons, "使用此网络并保存", UseNetworkAsync);
        AddButton(networkButtons, "刷新网络列表", () => { LoadNetworks(); return Task.CompletedTask; });
        _configuration.Children.Add(networkButtons);
        Field(_configuration, "局域网 IP 地址", _listen); Field(_configuration, "HTTPS 端口", _port);
        Field(_configuration, "手机访问电脑的 HTTPS 地址", _endpoint);
        Field(_configuration, "Tailscale IP 地址（可选）", _tailscaleListen);
        Field(_configuration, "Tailscale HTTPS 地址（可选）", _tailscaleEndpoint);
        _configuration.Children.Add(Text("Tailscale 用于备用连接，两项留空可关闭。", 12));
        Field(_configuration, "中继 HTTPS 地址（可选）", _relay);
        Field(_configuration, "电脑端中继凭据（留空保留）", _relayToken);
        _configuration.Children.Add(Text("中继服务可以读取转发内容。请只使用你信任的服务。不会自动修改防火墙或路由器。", 12));
        var actions = new WrapPanel();
        AddButton(actions, "保存连接设置", SaveAsync);
        _primary = AddButton(actions, "显示配对码", () =>
        {
            _owner.Dispatcher.BeginInvoke(new Action(() => { if (!_closed) PairingRequested?.Invoke(); }));
            return Task.CompletedTask;
        });
        _copy = AddButton(actions, "复制备用配对信息", CopyPairingAsync);
        _copy.ToolTip = "用于手机“添加电脑”中的粘贴方式。配对信息包含一次性凭据，请勿公开分享。";
        _configuration.Children.Add(actions); page.Children.Add(_configuration);

        page.Children.Add(Text("已授权手机", 18, true)); page.Children.Add(_clients);
        var clientActions = new WrapPanel();
        AddButton(clientActions, "刷新手机列表", () => { RefreshClients(); return Task.CompletedTask; });
        _revoke = AddButton(clientActions, "撤销手机授权", () =>
        {
            if (_clients.SelectedItem is ClientItem item && MessageBox.Show(_owner,
                "撤销后该手机将不能控制显示器，确认继续？", "撤销授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _manager.Security.Revoke(item.Client.ClientId); Notice("已撤销该手机的授权。", transient: true);
            }
            RefreshClients(); return Task.CompletedTask;
        });
        page.Children.Add(clientActions); SettingsContent = page;
        LoadSavedSettings(); LoadNetworks(); RefreshClients(); _knownClients = _manager.Security.Clients.Count;
        _networks.SelectionChanged += (_, _) => RefreshActions();
        _clients.SelectionChanged += (_, _) => RefreshActions();
        RenderPairing();
        _timer.Tick += async (_, _) => { RenderPairing(); if (CanRenew()) await RunAsync(StartPairingAsync); };
        SystemEvents.SessionSwitch += SessionSwitched;
    }

    internal FrameworkElement PairingContent { get; }
    internal FrameworkElement SettingsContent { get; }
    internal event Action? SettingsRequested;
    internal event Action? PairingRequested;

    internal async Task OpenPairingAsync()
    {
        if (_closed || _pairingVisible) return;
        var generation = Interlocked.Increment(ref _visibilityGeneration);
        await _closeTask;
        await _activeAction;
        if (_closed || _pairingVisible || generation != Interlocked.Read(ref _visibilityGeneration)) return;
        if (!_manager.Running)
        {
            Notice("请先启用手机控制并保存连接设置，然后点击“连接手机”。");
            RenderPairing(); SettingsRequested?.Invoke(); return;
        }
        _pairingVisible = true; _notice = null; _timer.Start(); RenderPairing();
        await RunAsync(StartPairingAsync);
    }

    internal Task ClosePairingAsync()
    {
        Interlocked.Increment(ref _visibilityGeneration);
        _pairingVisible = false;
        if (!_settingsVisible) _timer.Stop();
        if (!_closeTask.IsCompleted) return _closeTask;
        _closeTask = StopPairingAsync(notify: false);
        return _closeTask;
    }

    internal void SetSettingsVisible(bool visible)
    {
        if (_closed) return;
        _settingsVisible = visible;
        if (visible) { RefreshClients(); _timer.Start(); }
        else
        {
            _relayToken.Clear();
            if (!_pairingVisible)
            {
                _timer.Stop(); _manager.Security.CancelPairing(); ClearPairing();
            }
        }
        RenderPairing();
    }

    private void LoadSavedSettings()
    {
        var saved = _manager.Settings;
        _enabled.IsChecked = saved.Enabled; _listen.Text = saved.ListenAddress; _port.Text = saved.Port.ToString();
        _endpoint.Text = saved.PublicEndpoint; _relay.Text = saved.RelayUrl ?? "";
        _tailscaleListen.Text = saved.TailscaleListenAddress ?? ""; _tailscaleEndpoint.Text = saved.TailscaleEndpoint ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true; _pairingVisible = false; _sessionOpen = false; Interlocked.Increment(ref _pairGeneration); _timer.Stop();
        SystemEvents.SessionSwitch -= SessionSwitched; _lan.Changed -= LanChanged;
        _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); _relayToken.Clear();
        await _closeTask;
        await _activeAction;
        await _lan.DisposeAsync();
    }
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    {
        Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6)
    };
    private static void Field(Panel panel, string label, Control input) { panel.Children.Add(Text(label)); input.MinHeight = 32; panel.Children.Add(input); }
    private Button AddButton(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 8, 8, 8) };
        button.Click += async (_, _) => await RunAsync(action); panel.Children.Add(button); _actionButtons.Add(button); return button;
    }
    private void Notice(string text, bool transient = false)
    {
        _notice = text; _noticeUntil = transient ? DateTimeOffset.UtcNow.AddSeconds(5) : null;
    }
    private Task RunAsync(Func<Task> action)
    {
        if (_closed || _busy || _stopping) return _activeAction;
        _activeAction = RunCoreAsync(action);
        return _activeAction;
    }
    private async Task RunCoreAsync(Func<Task> action)
    {
        _busy = true; _notice = null; RefreshActions();
        try { await action(); }
        catch (Exception exception)
        {
            if (!_closed) Notice(exception.Message switch
            {
                "select_lan_address" => "请在设置中选择电脑当前网络，然后点击“使用此网络并保存”。",
                "remote_disabled" => "请先启用手机控制。",
                "invalid_port" or "invalid_listener" => "请检查连接设置中的 IP 地址和端口（1024—65535）。",
                "invalid_endpoint" => "请填写有效的 HTTPS 连接地址。",
                "invalid_tailscale_listener" or "invalid_tailscale_endpoint" => "请同时填写 Tailscale IP 和 HTTPS 地址，或同时留空。",
                "relay_token_missing" => "请填写有效的电脑端中继凭据，或清空中继地址后保存。",
                _ when exception is OperationCanceledException => "配对已停止，可重新开始连接。",
                _ => "操作未完成，请检查网络和连接设置后重试。"
            });
        }
        finally { _busy = false; if (!_closed) RenderPairing(); }
    }
    private void LoadNetworks()
    {
        var selected = (_networks.SelectedItem as LocalNetworkChoice)?.Address;
        var choices = LocalNetworkChoices.Get(); _networks.ItemsSource = choices;
        var lanChoices = choices.Where(choice => !choice.Tailscale).ToArray();
        _networks.SelectedItem = choices.FirstOrDefault(choice => choice.Address == selected)
            ?? choices.FirstOrDefault(choice => choice.Address == _manager.Settings.ListenAddress)
            ?? (lanChoices.Length == 1 ? lanChoices[0] : null);
    }
    private async Task UseNetworkAsync()
    {
        if (_networks.SelectedItem is not LocalNetworkChoice choice) throw new InvalidOperationException("select_lan_address");
        if (!int.TryParse(_port.Text, out var port) || port is < 1024 or > 65535) throw new ArgumentException("invalid_port");
        var endpoint = new UriBuilder("https", choice.Address, port).Uri.GetLeftPart(UriPartial.Authority);
        if (choice.Tailscale) { _tailscaleListen.Text = choice.Address; _tailscaleEndpoint.Text = endpoint; }
        else { _listen.Text = choice.Address; _endpoint.Text = endpoint; }
        _enabled.IsChecked = true; await SaveAsync();
    }
    private async Task SaveAsync()
    {
        if (!int.TryParse(_port.Text, out var port)) throw new ArgumentException("invalid_port");
        var secret = string.IsNullOrWhiteSpace(_relayToken.Password) ? _manager.Settings.ProtectedRelayToken : _manager.ProtectedRelayToken(_relayToken.Password);
        var settings = new RemoteSettings(_enabled.IsChecked == true, _listen.Text.Trim(), port, _endpoint.Text.Trim(),
            string.IsNullOrWhiteSpace(_relay.Text) ? null : _relay.Text.Trim(), secret,
            string.IsNullOrWhiteSpace(_tailscaleEndpoint.Text) ? null : _tailscaleEndpoint.Text.Trim(),
            string.IsNullOrWhiteSpace(_tailscaleListen.Text) ? null : _tailscaleListen.Text.Trim());
        settings.Validate(); _sessionOpen = false;
        var generation = Interlocked.Increment(ref _pairGeneration);
        await _lan.StopAsync(); ClearPairing();
        if (_closed || generation != Interlocked.Read(ref _pairGeneration)) return;
        await _manager.ApplyAsync(settings);
        if (_closed || generation != Interlocked.Read(ref _pairGeneration)) return;
        _relayToken.Clear();
        if (_manager.Running && _pairingVisible) await StartPairingAsync();
        else if (_manager.Running) Notice("连接设置已保存，点击“连接手机”显示配对码。", transient: true);
        else Notice("手机控制已关闭，已授权的手机仍保留。", transient: true);
    }
    private bool IsLanConfiguration() => IPAddress.TryParse(new Uri(_manager.Settings.PublicEndpoint).Host, out var address) && LocalNetworkChoices.Eligible(address, false);
    private bool Handshake => _lan.Status is "authenticating" or "sending-bundle" or "awaiting-android" or "connecting";
    private async Task StartPairingAsync()
    {
        _sessionOpen = false;
        var generation = Interlocked.Increment(ref _pairGeneration);
        await _lan.StopAsync(); ClearPairing();
        if (_closed || !_pairingVisible || generation != Interlocked.Read(ref _pairGeneration)) return;
        if (!_manager.Running) throw new InvalidOperationException("remote_disabled");
        _currentBundle = _manager.BeginPairing(); RenderQr(WireJson.Serialize(_currentBundle));
        _knownClients = _manager.Security.Clients.Count; _successCleanup = false; _notice = null;
        if (IsLanConfiguration()) await _lan.StartAsync();
        if (_closed || !_pairingVisible || generation != Interlocked.Read(ref _pairGeneration))
        {
            _manager.Security.CancelPairing(); await _lan.StopAsync(); ClearPairing(); return;
        }
        _sessionOpen = true; RenderPairing();
    }
    private bool CanRenew() => !_closed && _pairingVisible && !_busy && !_stopping && _sessionOpen && _manager.Running
        && PairingRenewalPolicy.ShouldRenew(_lan.Status, _currentBundle?.ExpiresAt, DateTimeOffset.UtcNow);
    private async Task StopPairingAsync(bool notify = true)
    {
        if (_closed || _stopping) return;
        _stopping = true; _sessionOpen = false; Interlocked.Increment(ref _pairGeneration);
        _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); RefreshActions();
        try { await _lan.StopAsync(); if (!_closed && notify) Notice("配对已停止，可重新开始连接。"); }
        catch { if (!_closed && notify) Notice("配对已取消，请重新开始连接。"); }
        finally { _stopping = false; if (!_closed) RenderPairing(); }
    }
    private async Task CopyPairingAsync()
    {
        if (!_manager.Running) throw new InvalidOperationException("remote_disabled");
        if (!_pairingVisible)
        {
            await _lan.StopAsync();
            if (_closed || !_settingsVisible) return;
            _currentBundle = _manager.BeginPairing(); _sessionOpen = false;
            _knownClients = _manager.Security.Clients.Count; _successCleanup = false;
        }
        if (_currentBundle is { } bundle && bundle.ExpiresAt > DateTimeOffset.UtcNow && !Handshake)
        {
            _copyToClipboard(WireJson.Serialize(bundle));
            Notice("已复制备用配对信息，请仅粘贴到你信任的手机。", transient: true);
        }
    }
    private void LanChanged() { if (!_closed && !_owner.Dispatcher.HasShutdownStarted) _owner.Dispatcher.BeginInvoke(new Action(RenderPairing)); }
    private void RenderPairing()
    {
        if (_closed) return;
        if (!_successCleanup && (_sessionOpen || _currentBundle is not null) && _manager.Security.Clients.Count > _knownClients)
        {
            _successCleanup = true; _sessionOpen = false; ClearPairing(); RefreshClients();
            Notice("手机授权已保存，请在手机确认连接结果。", transient: true);
            _ = FinishQrPairingAsync();
        }
        if (_lan.Status == "completed")
        {
            if (_sessionOpen) Notice("手机已连接，可开始控制显示器。", transient: true);
            _sessionOpen = false; ClearPairing(); RefreshClients();
        }
        if (_lan.Status is "sending-bundle" or "awaiting-android" or "failed" or "exhausted") ClearPairing();
        var now = DateTimeOffset.UtcNow;
        var hasBundle = _currentBundle is { } current && current.ExpiresAt > now;
        var hasCode = hasBundle && (_lan.Status is "listening" or "rejected") && _lan.Code.Length == 6 && _lan.ExpiresAt > now && !_stopping;
        _code.Text = hasCode ? _lan.Code[..3] + " " + _lan.Code[3..] : "";
        _code.Visibility = hasCode ? Visibility.Visible : Visibility.Collapsed;
        _codeLabel.Visibility = _code.Visibility;
        _qr.Visibility = hasBundle && _qr.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        _qrFrame.Visibility = _qr.Visibility;
        if (_noticeUntil <= now) { _notice = null; _noticeUntil = null; }
        var remaining = Math.Max(0, (int)((_currentBundle?.ExpiresAt ?? now) - now).TotalSeconds);
        _pairStatus.Text = _stopping ? "正在停止配对……" : Handshake ? "正在连接手机，请稍候……" : _notice ?? (_lan.Status switch
        {
            "listening" when hasBundle => $"等待手机连接，配对码剩余 {remaining / 60}:{remaining % 60:00}。",
            "rejected" => "短码未匹配，请在手机核对后重新输入。",
            "exhausted" => "尝试次数已达上限，请稍后手动重新配对。",
            "failed" => "连接未完成，请在两端重新配对。",
            "expired" when _sessionOpen => "配对码已过期，正在更新……",
            "discovery_unavailable" when hasBundle => $"附近搜索暂不可用，请在手机扫描二维码。二维码剩余 {remaining / 60}:{remaining % 60:00}。",
            _ => hasBundle ? $"请在手机扫描二维码，剩余 {remaining / 60}:{remaining % 60:00}。"
                : _busy ? "正在生成配对码……"
                : _manager.Running ? "关闭后可重新点击“连接手机”配对。"
                : "请先在设置中启用手机控制。"
        });
        _status.Text = _busy ? "正在处理，请稍候……" : _notice ?? (_manager.Running ? "手机控制已启用。" : "手机控制未启用。请选择电脑网络并保存设置。");
        var tailscale = _manager.TailscaleStatus switch { "listening" => "已启用", "unavailable" => "暂不可用", _ => "未启用" };
        SettingsContent.ToolTip = $"当前地址：{_manager.Settings.PublicEndpoint}\nTailscale：{tailscale}";
        RefreshActions();
    }
    private void RefreshActions()
    {
        if (_closed) return;
        var ready = !_busy && !_stopping && !Handshake;
        foreach (var button in _actionButtons) button.IsEnabled = ready;
        _configuration.IsEnabled = ready;
        _primary.IsEnabled = ready && _manager.Running;
        _useNetwork.IsEnabled = ready && _networks.SelectedItem is LocalNetworkChoice;
        _copy.IsEnabled = ready && _manager.Running;
        _revoke.IsEnabled = ready && _clients.SelectedItem is ClientItem;
    }
    private void SessionSwitched(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason != SessionSwitchReason.SessionLock) return;
        _sessionOpen = false; Interlocked.Increment(ref _pairGeneration); _lan.Cancel(); _manager.Security.CancelPairing();
        if (!_closed && !_owner.Dispatcher.HasShutdownStarted) _owner.Dispatcher.BeginInvoke(new Action(async () =>
        {
            _relayToken.Clear(); await ClosePairingAsync();
        }));
    }
    private async Task FinishQrPairingAsync()
    {
        var generation = Interlocked.Read(ref _pairGeneration);
        try
        {
            if (!Handshake) await _lan.StopAsync();
            if (!_closed && generation == Interlocked.Read(ref _pairGeneration)) RenderPairing();
        }
        catch { if (!_closed && generation == Interlocked.Read(ref _pairGeneration)) Notice("手机授权已保存，请在手机确认连接结果。"); }
    }
    private void ClearPairing() { _currentBundle = null; _qr.Source = null; _qr.Visibility = Visibility.Collapsed; _code.Text = ""; }
    private void RenderQr(string payload)
    {
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data); using var stream = new MemoryStream(code.GetGraphic(6));
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); _qr.Source = image; _qr.Visibility = Visibility.Visible;
    }
    private sealed record ClientItem(PairedClient Client)
    {
        public string Label => $"{Client.Name}\n授权于 {Client.PairedAt.ToLocalTime():yyyy/MM/dd HH:mm}";
    }
    private void RefreshClients()
    {
        var authorized = _manager.Security.Clients;
        if (_clients.Items.OfType<ClientItem>().Select(item => item.Client).SequenceEqual(authorized)) return;
        var selected = (_clients.SelectedItem as ClientItem)?.Client.ClientId;
        var clients = authorized.Select(client => new ClientItem(client)).ToArray();
        _clients.ItemsSource = clients;
        _clients.SelectedItem = clients.FirstOrDefault(item => item.Client.ClientId == selected);
    }
}
