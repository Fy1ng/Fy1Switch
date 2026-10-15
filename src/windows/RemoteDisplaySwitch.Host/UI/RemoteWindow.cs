using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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
using RemoteDisplaySwitch.Discovery;
using RemoteDisplaySwitch.Remote;

namespace RemoteDisplaySwitch.Host.UI;

public sealed class RemoteWindow : Window
{
    private readonly RemoteManager _manager;
    private readonly LanPairingCoordinator _lan;
    private readonly CheckBox _enabled = new() { Content = "启用手机控制", Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox _networks = new() { DisplayMemberPath = "Label", MinHeight = 34 };
    private readonly TextBox _listen = new();
    private readonly TextBox _port = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _tailscaleListen = new();
    private readonly TextBox _tailscaleEndpoint = new();
    private readonly TextBox _relay = new();
    private readonly PasswordBox _relayToken = new();
    private readonly TextBox _bundle = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _connectionState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _networkStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBlock _code = new() { Text = "尚未开始", FontSize = 30, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _pairStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _peers = new() { MinHeight = 65, MaxHeight = 140, DisplayMemberPath = "Name" };
    private readonly PasswordBox _peerCode = new() { MaxLength = 6, MinHeight = 34 };
    private readonly ListBox _clients = new() { DisplayMemberPath = "Name", MinHeight = 60, MaxHeight = 140 };
    private readonly StackPanel _page = new() { Margin = new Thickness(20, 12, 20, 20) };
    private readonly StackPanel _configuration = new();
    private readonly StackPanel _reversePairing = new();
    private readonly Expander _reverse = new() { Header = "改为在电脑输入手机短码", Margin = new Thickness(0, 12, 0, 0) };
    private readonly Expander _settings = new() { Header = "连接设置", Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _pairCard;
    private readonly List<Button> _actionButtons = new();
    private readonly Button _primary;
    private readonly Button _useNetwork;
    private readonly Button _connect;
    private readonly Button _stop = new() { Content = "停止配对", Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 8, 0, 8) };
    private readonly Image _qr = new() { Width = 220, Height = 220, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairBundle? _currentBundle;
    private bool _busy;
    private bool _stopping;
    private bool _closed;
    private int _knownClients;
    private bool _successCleanup;
    private long _pairGeneration;
    public RemoteWindow(RemoteManager manager)
    {
        _manager = manager;
        _lan = new LanPairingCoordinator(manager);
        _lan.Changed += LanChanged;
        Resources.MergedDictionaries.Add(DesktopTheme.CreateResources());
        Title = "连接手机"; Width = 680; Height = 700; MinWidth = 520; MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 250)); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        _page.Children.Add(Text("用手机控制显示器", 24, true));
        _page.Children.Add(Text("在手机打开配对页, 扫描二维码或输入本机短码。"));
        _page.Children.Add(_connectionState);
        var start = new WrapPanel();
        _primary = AddButton(start, "启用手机控制", StartConnectionAsync);
        _primary.Background = new SolidColorBrush(Color.FromRgb(47, 108, 206)); _primary.Foreground = Brushes.White;
        _stop.Click += async (_, _) => await StopPairingAsync(); start.Children.Add(_stop);
        _page.Children.Add(start); _page.Children.Add(_status);
        var pair = new StackPanel();
        var pairContent = new Grid(); pairContent.ColumnDefinitions.Add(new ColumnDefinition()); pairContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pairInstructions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        pairInstructions.Children.Add(Text("扫码或输入本机短码", 18, true)); pairInstructions.Children.Add(_code); pairInstructions.Children.Add(_pairStatus);
        pairContent.Children.Add(pairInstructions); Grid.SetColumn(_qr, 1); pairContent.Children.Add(_qr); pair.Children.Add(pairContent);
        _pairCard = Card(pair); _page.Children.Add(_pairCard);
        _reversePairing.Children.Add(Text("同一局域网内, 在手机打开配对页后选择设备。")); _reversePairing.Children.Add(_peers);
        Field(_reversePairing, "手机屏幕上的六位短码", _peerCode);
        var connect = new WrapPanel(); _connect = AddButton(connect, "连接所选手机", ConnectAsync); _reversePairing.Children.Add(connect);
        _reversePairing.Children.Add(Text("设备名仅用于识别. 短码五分钟有效, 一次使用, 最多尝试五次。", 12));
        _reverse.Content = _reversePairing; _page.Children.Add(_reverse);

        _configuration.Children.Add(_enabled);
        _configuration.Children.Add(Text("首次使用请选择电脑当前网络, 再点击下方按钮即可保存并开始连接。"));
        Field(_configuration, "电脑当前网络", _networks);
        var networkButtons = new WrapPanel();
        _useNetwork = AddButton(networkButtons, "使用此地址并连接", UseNetworkAsync);
        AddButton(networkButtons, "刷新网络列表", () => { LoadNetworks(); return Task.CompletedTask; }); _configuration.Children.Add(networkButtons);
        _configuration.Children.Add(Text("手动配置", 16, true));
        Field(_configuration, "局域网监听 IP", _listen); Field(_configuration, "共同 HTTPS 端口", _port); Field(_configuration, "手机访问局域网 HTTPS 地址", _endpoint);
        Field(_configuration, "Tailscale 监听 IP (可选)", _tailscaleListen); Field(_configuration, "Tailscale HTTPS 地址 (可选)", _tailscaleEndpoint);
        _configuration.Children.Add(Text("Tailscale 作为备用入口, 不覆盖局域网设置. 两项留空可关闭。", 12));
        Field(_configuration, "Relay HTTPS 地址 (可选)", _relay); Field(_configuration, "Windows Relay 凭据 (空白保留)", _relayToken);
        _configuration.Children.Add(Text("Relay 可读取转发内容. 不会自动修改防火墙或路由器。", 12));
        var apply = new WrapPanel(); AddButton(apply, "保存连接设置", SaveAsync); _configuration.Children.Add(apply); _configuration.Children.Add(_networkStatus);
        Field(_configuration, "备用配对信息 (请勿公开分享)", _bundle);
        _settings.Content = _configuration; _page.Children.Add(_settings);
        var clients = new StackPanel(); clients.Children.Add(_clients);
        var revoke = new WrapPanel();
        AddButton(revoke, "刷新列表", () => { RefreshClients(); return Task.CompletedTask; });
        AddButton(revoke, "撤销手机授权", () =>
        {
            if (_clients.SelectedItem is PairedClient client && MessageBox.Show(this, "撤销后该手机将不能控制显示器，确认继续？", "撤销授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _manager.Security.Revoke(client.ClientId);
            RefreshClients(); return Task.CompletedTask;
        });
        clients.Children.Add(revoke); _page.Children.Add(new Expander { Header = "已连接手机", Content = clients, Margin = new Thickness(0, 12, 0, 0) });
        Content = WindowFrame.Wrap(this, new ScrollViewer { Content = _page, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _enabled.IsChecked = manager.Settings.Enabled; _listen.Text = manager.Settings.ListenAddress; _port.Text = manager.Settings.Port.ToString(); _endpoint.Text = manager.Settings.PublicEndpoint; _relay.Text = manager.Settings.RelayUrl ?? "";
        _tailscaleListen.Text = manager.Settings.TailscaleListenAddress ?? ""; _tailscaleEndpoint.Text = manager.Settings.TailscaleEndpoint ?? "";
        LoadNetworks(); RefreshClients(); RefreshStatus(); _knownClients = _manager.Security.Clients.Count;
        _settings.IsExpanded = !_manager.Running;
        _networks.SelectionChanged += (_, _) => RefreshActions(); _peers.SelectionChanged += (_, _) => RefreshActions(); _peerCode.PasswordChanged += (_, _) => RefreshActions();
        RefreshActions();
        Loaded += async (_, _) => { if (_manager.Running) await RunAsync(StartPairingAsync); };
        _timer.Tick += (_, _) => RenderPairing(); _timer.Start();
        SystemEvents.SessionSwitch += SessionSwitched;
        Closed += async (_, _) =>
        {
            _closed = true; Interlocked.Increment(ref _pairGeneration); _timer.Stop(); SystemEvents.SessionSwitch -= SessionSwitched; _lan.Changed -= LanChanged;
            _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); _peerCode.Clear(); _relayToken.Clear();
            try { await _lan.DisposeAsync(); } catch { }
        };
    }
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private static Border Card(UIElement child) => new() { Child = child, Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(0, 12, 0, 0), BorderBrush = new SolidColorBrush(Color.FromRgb(216, 225, 237)), BorderThickness = new Thickness(1) };
    private static void Field(Panel panel, string label, Control input) { panel.Children.Add(Text(label)); input.MinHeight = 32; panel.Children.Add(input); }
    private Button AddButton(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 8, 8, 8) };
        button.Click += async (_, _) => await RunAsync(action); panel.Children.Add(button); _actionButtons.Add(button); return button;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _busy || _stopping) return; _busy = true; RefreshActions();
        try { await action(); }
        catch (Exception exception)
        {
            if (!_closed) _status.Text = exception.Message switch
            {
                "select_lan_address" => "请在连接设置中选择电脑当前网络, 再点击使用此地址并连接。",
                "remote_disabled" => "请先点击启用手机控制。",
                "invalid_port" or "invalid_listener" => "请检查连接设置中的监听 IP 和端口 (1024-65535)。",
                "invalid_endpoint" => "请填写有效的 HTTPS 连接地址。",
                "invalid_tailscale_listener" or "invalid_tailscale_endpoint" => "请同时填写匹配的 Tailscale 监听 IP 和 HTTPS 地址, 或同时留空。",
                "relay_token_missing" => "请填写有效的 Windows Relay 凭据, 或清空 Relay 地址后保存。",
                "peer_expired" => "设备已离开配对页，请刷新搜索后重试。",
                _ when exception is OperationCanceledException => "配对已停止, 可重新开始连接。",
                _ => "连接未完成。请检查地址、网络权限、端口和配对码；未自动重复认证。"
            };
        }
        finally { _busy = false; if (!_closed) { RefreshStatus(); RenderPairing(); } }
    }
    private void LoadNetworks()
    {
        var selected = (_networks.SelectedItem as LocalNetworkChoice)?.Address;
        var choices = LocalNetworkChoices.Get(); _networks.ItemsSource = choices;
        _networks.SelectedItem = choices.FirstOrDefault(choice => choice.Address == selected) ?? choices.FirstOrDefault(choice => choice.Address == _manager.Settings.ListenAddress) ?? choices.FirstOrDefault(choice => choice.Address == _manager.Settings.TailscaleListenAddress);
    }
    private async Task StartConnectionAsync()
    {
        if (_manager.Running) { await StartPairingAsync(); return; }
        _enabled.IsChecked = true;
        if (Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var endpoint) && endpoint.IsLoopback &&
            string.IsNullOrWhiteSpace(_tailscaleEndpoint.Text) && string.IsNullOrWhiteSpace(_relay.Text))
        {
            _settings.IsExpanded = true;
            _status.Text = "请选择电脑当前网络, 然后点击使用此地址并连接。";
            _networks.Focus(); return;
        }
        await SaveAsync();
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
        var settings = new RemoteSettings(_enabled.IsChecked == true, _listen.Text.Trim(), port, _endpoint.Text.Trim(), string.IsNullOrWhiteSpace(_relay.Text) ? null : _relay.Text.Trim(), secret,
            string.IsNullOrWhiteSpace(_tailscaleEndpoint.Text) ? null : _tailscaleEndpoint.Text.Trim(), string.IsNullOrWhiteSpace(_tailscaleListen.Text) ? null : _tailscaleListen.Text.Trim());
        settings.Validate();
        await _lan.StopAsync(); ClearPairing();
        if (_closed) return;
        await _manager.ApplyAsync(settings);
        if (_closed) return;
        _relayToken.Clear(); RefreshStatus(); _settings.IsExpanded = !_manager.Running;
        _status.Text = _manager.Running ? "连接设置已保存。" : "手机控制已关闭, 已配对手机的授权仍保留。";
        if (_manager.Running) await StartPairingAsync();
    }
    private bool IsLanConfiguration() => IPAddress.TryParse(new Uri(_manager.Settings.PublicEndpoint).Host, out var address) && LocalNetworkChoices.Eligible(address, false);
    private async Task StartPairingAsync()
    {
        var generation = Interlocked.Increment(ref _pairGeneration);
        await _lan.StopAsync(); ClearPairing();
        if (_closed || generation != Interlocked.Read(ref _pairGeneration)) return;
        if (!_manager.Running) throw new InvalidOperationException("remote_disabled");
        _currentBundle = _manager.BeginPairing(); _bundle.Text = WireJson.Serialize(_currentBundle); RenderQr(_bundle.Text);
        _knownClients = _manager.Security.Clients.Count; _successCleanup = false;
        _status.Text = "";
        if (IsLanConfiguration()) await _lan.StartAsync();
        if (_closed || generation != Interlocked.Read(ref _pairGeneration)) { _manager.Security.CancelPairing(); await _lan.StopAsync(); ClearPairing(); return; }
        RefreshStatus(); RenderPairing();
    }
    private async Task StopPairingAsync()
    {
        if (_closed || _stopping) return;
        _stopping = true; Interlocked.Increment(ref _pairGeneration); _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); RefreshActions();
        try { await _lan.StopAsync(); if (!_closed) _status.Text = "配对已停止, 可重新开始连接。"; }
        catch { if (!_closed) _status.Text = "配对已取消, 请重新开始连接。"; }
        finally { _stopping = false; if (!_closed) RenderPairing(); }
    }
    private async Task ConnectAsync()
    {
        if (_peers.SelectedItem is not DiscoveredPeer peer) { _status.Text = "请先选择附近手机。"; return; }
        var code = _peerCode.Password; _peerCode.Clear();
        if (code.Length != 6 || code.Any(character => character < '0' || character > '9')) { _status.Text = "请输入对方屏幕上的六位数字，包含开头的 0。"; return; }
        var generation = Interlocked.Read(ref _pairGeneration); ClearPairing();
        var success = await _lan.ConnectAsync(peer, code);
        if (_closed || generation != Interlocked.Read(ref _pairGeneration)) return;
        ClearPairing(); _status.Text = success ? "手机已连接，可开始控制。" : "配对未完成，请在两端重新开始配对。"; RefreshClients();
    }
    private void LanChanged() { if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RenderPairing)); }
    private void RenderPairing()
    {
        if (_closed) return;
        if (_currentBundle is not null && !_successCleanup && _manager.Security.Clients.Count > _knownClients)
        {
            _successCleanup = true; ClearPairing(); RefreshClients();
            _ = FinishQrPairingAsync();
        }
        if (_currentBundle is { } bundle && bundle.ExpiresAt <= DateTimeOffset.UtcNow) ClearPairing();
        if (_lan.Status is "sending-bundle" or "awaiting-android") ClearPairing();
        var hasCode = _currentBundle is not null && (_lan.Status is "listening" or "rejected") && _lan.Code.Length == 6 && _lan.ExpiresAt > DateTimeOffset.UtcNow && !_stopping;
        _code.Text = hasCode ? _lan.Code[..3] + " " + _lan.Code[3..] : _currentBundle is not null ? "请扫描二维码" : _lan.Status == "completed" ? "已连接" : "尚未开始";
        var selected = _peers.SelectedItem as DiscoveredPeer;
        _peers.ItemsSource = _lan.Candidates; _peers.SelectedItem = _lan.Candidates.FirstOrDefault(peer => peer == selected);
        _pairStatus.Text = _lan.Status switch
        {
            "listening" => $"等待手机连接, 短码剩余 {Math.Max(0, (int)((_lan.ExpiresAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds)} 秒。",
            "completed" => "配对成功，手机授权已保存。",
            "authenticating" or "sending-bundle" or "awaiting-android" or "connecting" => "正在安全配对，请勿重复操作…",
            "rejected" => "短码未匹配, 请核对后重试。",
            "expired" => "配对码已过期，请重新开始。",
            "exhausted" => "尝试次数达到上限，请稍后重新开始。",
            "failed" => "配对失败，请核对对方的配对码。",
            "discovery_unavailable" => "局域网发现暂不可用，可扫码连接。请检查防火墙和网络的组播支持。",
            _ => _currentBundle is not null ? "在手机配对页扫描二维码。" : _manager.Running ? "点击开始连接, 显示二维码和本机短码。" : "启用手机控制后, 在这里连接手机。"
        };
        if (_lan.Status == "completed") { ClearPairing(); RefreshClients(); }
        RefreshActions();
    }
    private void RefreshActions()
    {
        if (_closed) return;
        var handshake = _lan.Status is "authenticating" or "sending-bundle" or "awaiting-android" or "connecting";
        var ready = !_busy && !_stopping && !handshake;
        var pairing = _currentBundle is not null || _lan.Active && (_lan.Status is "listening" or "rejected" || handshake);
        foreach (var button in _actionButtons) button.IsEnabled = ready;
        _configuration.IsEnabled = ready; _reversePairing.IsEnabled = ready;
        _pairCard.Visibility = _manager.Running ? Visibility.Visible : Visibility.Collapsed;
        _reverse.Visibility = _manager.Running ? Visibility.Visible : Visibility.Collapsed;
        _primary.Content = !_manager.Running ? "启用手机控制" : pairing ? "刷新连接码" : "开始连接";
        _useNetwork.IsEnabled = ready && _networks.SelectedItem is LocalNetworkChoice;
        _connect.IsEnabled = ready && _lan.Active && (_lan.Status is "listening" or "rejected") && _peers.SelectedItem is DiscoveredPeer && _peerCode.Password.Length == 6;
        _stop.IsEnabled = !_stopping && pairing;
        _stop.Visibility = pairing || _stopping ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SessionSwitched(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock)
        {
            Interlocked.Increment(ref _pairGeneration);
            _lan.Cancel(); _manager.Security.CancelPairing();
            if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(async () => await StopPairingAsync()));
        }
    }
    private async Task FinishQrPairingAsync()
    {
        var generation = Interlocked.Read(ref _pairGeneration);
        try
        {
            if (_lan.Status is not ("authenticating" or "sending-bundle" or "awaiting-android" or "connecting")) await _lan.StopAsync();
            if (!_closed && generation == Interlocked.Read(ref _pairGeneration)) _status.Text = "手机授权已保存, 请以手机上的连接结果为准。";
        }
        catch { if (!_closed && generation == Interlocked.Read(ref _pairGeneration)) _status.Text = "手机授权已保存, 请确认手机连接状态。"; }
    }
    private void ClearPairing() { _currentBundle = null; _bundle.Clear(); _qr.Source = null; _qr.Visibility = Visibility.Collapsed; _code.Text = "尚未开始"; }
    private void RenderQr(string payload)
    {
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M); using var code = new PngByteQRCode(data); using var stream = new MemoryStream(code.GetGraphic(6));
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); _qr.Source = image; _qr.Visibility = Visibility.Visible;
    }
    private void RefreshStatus()
    {
        var tailscale = _manager.TailscaleStatus switch
        {
            "listening" => $"已启用 · {_manager.Settings.TailscaleEndpoint}",
            "unavailable" => "暂不可用，局域网仍可连接；恢复网络后重新保存以重试",
            _ => "未启用"
        };
        _connectionState.Text = _manager.Running ? "手机控制已启用, 已配对手机可直接使用。" : "手机控制未启用. 首次使用请在连接设置中选择电脑当前网络。";
        _networkStatus.Text = $"当前连接地址: {_manager.Settings.PublicEndpoint}\nTailscale: {tailscale}";
    }
    private void RefreshClients() => _clients.ItemsSource = _manager.Security.Clients.ToArray();
}
