using System;
using System.Linq;
using System.IO;
using System.Net;
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
    private readonly CheckBox _enabled = new() { Content = "允许手机控制显示器", Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox _networks = new() { DisplayMemberPath = "Label", MinHeight = 34 };
    private readonly TextBox _listen = new();
    private readonly TextBox _port = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _tailscaleListen = new();
    private readonly TextBox _tailscaleEndpoint = new();
    private readonly TextBox _relay = new();
    private readonly PasswordBox _relayToken = new();
    private readonly TextBox _bundle = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _code = new() { Text = "— — — — — —", FontSize = 36, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _pairStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _peers = new() { MinHeight = 65, MaxHeight = 140, DisplayMemberPath = "Name" };
    private readonly PasswordBox _peerCode = new() { MaxLength = 6, MinHeight = 34 };
    private readonly ListBox _clients = new() { DisplayMemberPath = "Name", MinHeight = 60, MaxHeight = 140 };
    private readonly StackPanel _page = new() { Margin = new Thickness(24) };
    private readonly Image _qr = new() { Width = 250, Height = 250, Margin = new Thickness(0, 10, 0, 10), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairBundle? _currentBundle;
    private bool _busy;
    private bool _closed;
    private int _knownClients;
    private bool _successCleanup;
    public RemoteWindow(RemoteManager manager)
    {
        _manager = manager;
        _lan = new LanPairingCoordinator(manager);
        _lan.Changed += LanChanged;
        Resources.MergedDictionaries.Add(DesktopTheme.CreateResources());
        Title = "连接手机"; Width = 720; Height = 820; MinWidth = 560; MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 250)); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        _page.Children.Add(Text("连接手机", 26, true));
        _page.Children.Add(Text("同一局域网打开两端配对页，可扫码，或选择附近设备并输入对方的六位码。"));
        _page.Children.Add(_enabled);
        Field(_page, "连接地址", _networks);
        var networkButtons = new WrapPanel();
        AddButton(networkButtons, "刷新网卡", () => { LoadNetworks(); return Task.CompletedTask; });
        AddButton(networkButtons, "使用所选地址", () =>
        {
            if (_networks.SelectedItem is not LocalNetworkChoice choice) throw new InvalidOperationException("select_lan_address");
            var settings = choice.Apply(_manager.Settings);
            var port = int.TryParse(_port.Text, out var parsed) ? parsed : settings.Port;
            var endpoint = new UriBuilder("https", choice.Address, port).Uri.GetLeftPart(UriPartial.Authority);
            if (choice.Tailscale) { _tailscaleListen.Text = choice.Address; _tailscaleEndpoint.Text = endpoint; }
            else { _listen.Text = choice.Address; _endpoint.Text = endpoint; }
            _status.Text = "地址已填入，保存后生效；不会覆盖其他设置。"; return Task.CompletedTask;
        });
        _page.Children.Add(networkButtons);
        Field(_page, "Tailscale 监听 IP（可选）", _tailscaleListen);
        Field(_page, "Tailscale HTTPS 地址（可选）", _tailscaleEndpoint);
        _page.Children.Add(Text("选择 Tailscale 网卡只填入备用入口，不覆盖局域网。两项留空可关闭；不可用时保留局域网连接，恢复网络后重新保存以重试。"));
        var advanced = new StackPanel();
        Field(advanced, "局域网监听 IP", _listen); Field(advanced, "共同 HTTPS 端口", _port); Field(advanced, "手机访问局域网 HTTPS 地址", _endpoint);
        Field(advanced, "Relay HTTPS 地址（可选）", _relay); Field(advanced, "Windows Relay 凭据（空白保留）", _relayToken);
        advanced.Children.Add(Text("Relay 为可信中继，可读取转发内容；不会自动修改防火墙或路由器。"));
        _page.Children.Add(new Expander { Header = "高级网络与 Relay 设置", Content = advanced });
        var apply = new WrapPanel(); AddButton(apply, "保存连接设置", SaveAsync); _page.Children.Add(apply); _page.Children.Add(_status);
        var pair = new StackPanel();
        pair.Children.Add(Text("本机配对码", 18, true)); pair.Children.Add(_code); pair.Children.Add(_pairStatus);
        var start = new WrapPanel(); AddButton(start, "开始配对 / 刷新配对码", StartPairingAsync); AddButton(start, "停止搜索", async () => { await _lan.StopAsync(); ClearPairing(); }); pair.Children.Add(start);
        pair.Children.Add(_qr);
        pair.Children.Add(new Expander { Header = "备用：复制配对信息", Content = _bundle });
        pair.Children.Add(Text("附近手机", 17, true)); pair.Children.Add(_peers);
        Field(pair, "选择手机后，输入手机屏幕上的六位码", _peerCode);
        var connect = new WrapPanel(); AddButton(connect, "连接所选手机", ConnectAsync); pair.Children.Add(connect);
        pair.Children.Add(Text("设备名仅用于识别候选。短码五分钟有效、一次使用；每码最多五次认证尝试。手机退出配对页后将停止被发现。"));
        _page.Children.Add(Card(pair));
        var clients = new StackPanel(); clients.Children.Add(_clients);
        var revoke = new WrapPanel();
        AddButton(revoke, "刷新列表", () => { RefreshClients(); return Task.CompletedTask; });
        AddButton(revoke, "撤销手机授权", () =>
        {
            if (_clients.SelectedItem is PairedClient client && MessageBox.Show(this, "撤销后该手机将不能控制显示器，确认继续？", "撤销授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _manager.Security.Revoke(client.ClientId);
            RefreshClients(); return Task.CompletedTask;
        });
        clients.Children.Add(revoke); _page.Children.Add(new Expander { Header = "已连接手机", Content = clients, Margin = new Thickness(0, 12, 0, 0) });
        Content = new ScrollViewer { Content = _page, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _enabled.IsChecked = manager.Settings.Enabled; _listen.Text = manager.Settings.ListenAddress; _port.Text = manager.Settings.Port.ToString(); _endpoint.Text = manager.Settings.PublicEndpoint; _relay.Text = manager.Settings.RelayUrl ?? "";
        _tailscaleListen.Text = manager.Settings.TailscaleListenAddress ?? ""; _tailscaleEndpoint.Text = manager.Settings.TailscaleEndpoint ?? "";
        LoadNetworks(); RefreshClients(); RefreshStatus(); _knownClients = _manager.Security.Clients.Count;
        Loaded += async (_, _) => { if (_manager.Running && IsLanConfiguration()) await RunAsync(StartPairingAsync); };
        _timer.Tick += (_, _) => RenderPairing(); _timer.Start();
        SystemEvents.SessionSwitch += SessionSwitched;
        Closed += async (_, _) =>
        {
            _closed = true; _timer.Stop(); SystemEvents.SessionSwitch -= SessionSwitched; _lan.Changed -= LanChanged;
            _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); _peerCode.Clear(); _relayToken.Clear();
            try { await _lan.DisposeAsync(); } catch { }
        };
    }
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private static Border Card(UIElement child) => new() { Child = child, Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(0, 12, 0, 0), BorderBrush = new SolidColorBrush(Color.FromRgb(216, 225, 237)), BorderThickness = new Thickness(1) };
    private static void Field(Panel panel, string label, Control input) { panel.Children.Add(Text(label)); input.MinHeight = 32; panel.Children.Add(input); }
    private void AddButton(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 8, 8, 8) };
        button.Click += async (_, _) => await RunAsync(action); panel.Children.Add(button);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _busy) return; _busy = true; _page.IsEnabled = false;
        try { await action(); }
        catch (Exception exception)
        {
            if (!_closed) _status.Text = exception.Message switch
            {
                "select_lan_address" => "请选择当前局域网 IP 并保存；Tailscale 使用扫码，不依赖局域网组播。",
                "remote_disabled" => "请先开启手机控制并保存连接设置。",
                "peer_expired" => "设备已离开配对页，请刷新搜索后重试。",
                _ => "连接未完成。请检查地址、网络权限、端口和配对码；未自动重复认证。"
            };
        }
        finally { _busy = false; if (!_closed) { _page.IsEnabled = true; RenderPairing(); } }
    }
    private void LoadNetworks()
    {
        var selected = (_networks.SelectedItem as LocalNetworkChoice)?.Address;
        var choices = LocalNetworkChoices.Get(); _networks.ItemsSource = choices;
        _networks.SelectedItem = choices.FirstOrDefault(choice => choice.Address == selected) ?? choices.FirstOrDefault(choice => choice.Address == _manager.Settings.ListenAddress) ?? choices.FirstOrDefault(choice => !choice.Tailscale) ?? choices.FirstOrDefault();
    }
    private async Task SaveAsync()
    {
        await _lan.StopAsync(); ClearPairing();
        if (!int.TryParse(_port.Text, out var port)) throw new ArgumentException("invalid_port");
        if (_listen.Text.Trim() == "127.0.0.1" && Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var currentEndpoint) && currentEndpoint.IsLoopback && _networks.SelectedItem is LocalNetworkChoice { Tailscale: false } initialChoice)
        {
            _listen.Text = initialChoice.Address;
            _endpoint.Text = new UriBuilder("https", initialChoice.Address, port).Uri.GetLeftPart(UriPartial.Authority);
        }
        var secret = string.IsNullOrWhiteSpace(_relayToken.Password) ? _manager.Settings.ProtectedRelayToken : _manager.ProtectedRelayToken(_relayToken.Password);
        await _manager.ApplyAsync(new(_enabled.IsChecked == true, _listen.Text.Trim(), port, _endpoint.Text.Trim(), string.IsNullOrWhiteSpace(_relay.Text) ? null : _relay.Text.Trim(), secret,
            string.IsNullOrWhiteSpace(_tailscaleEndpoint.Text) ? null : _tailscaleEndpoint.Text.Trim(), string.IsNullOrWhiteSpace(_tailscaleListen.Text) ? null : _tailscaleListen.Text.Trim()));
        _relayToken.Clear(); RefreshStatus();
        if (_manager.Running) await StartPairingAsync();
    }
    private bool IsLanConfiguration() => IPAddress.TryParse(new Uri(_manager.Settings.PublicEndpoint).Host, out var address) && LocalNetworkChoices.Eligible(address, false);
    private async Task StartPairingAsync()
    {
        await _lan.StopAsync(); ClearPairing();
        if (!_manager.Running) throw new InvalidOperationException("remote_disabled");
        _currentBundle = _manager.BeginPairing(); _bundle.Text = WireJson.Serialize(_currentBundle); RenderQr(_bundle.Text);
        _knownClients = _manager.Security.Clients.Count; _successCleanup = false;
        if (IsLanConfiguration()) await _lan.StartAsync();
        else _pairStatus.Text = "当前地址支持扫码连接；双向搜索仅在本机局域网地址开启。";
        RefreshStatus(); RenderPairing();
    }
    private async Task ConnectAsync()
    {
        if (_peers.SelectedItem is not DiscoveredPeer peer) { _status.Text = "请先选择附近手机。"; return; }
        var code = _peerCode.Password; _peerCode.Clear();
        if (code.Length != 6 || code.Any(character => character < '0' || character > '9')) { _status.Text = "请输入对方屏幕上的六位数字，包含开头的 0。"; return; }
        var success = await _lan.ConnectAsync(peer, code);
        ClearPairing(); _status.Text = success ? "手机已连接，可开始控制。" : "配对未完成，请在两端重新开始配对。"; RefreshClients();
    }
    private void LanChanged() { if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RenderPairing)); }
    private void RenderPairing()
    {
        if (_closed) return;
        if (!_successCleanup && _manager.Security.Clients.Count > _knownClients)
        {
            _successCleanup = true; ClearPairing(); RefreshClients();
            _ = FinishQrPairingAsync();
        }
        if (_currentBundle is { } bundle && bundle.ExpiresAt <= DateTimeOffset.UtcNow) ClearPairing();
        _code.Text = _lan.Code.Length == 6 ? _lan.Code[..3] + " " + _lan.Code[3..] : "— — — — — —";
        var selected = _peers.SelectedItem as DiscoveredPeer;
        _peers.ItemsSource = _lan.Candidates; _peers.SelectedItem = _lan.Candidates.FirstOrDefault(peer => peer == selected);
        _pairStatus.Text = _lan.Status switch
        {
            "listening" => $"等待连接 · 剩余 {Math.Max(0, (int)((_lan.ExpiresAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds)} 秒。可让手机输入上方码，或在下面选择手机。",
            "completed" => "配对成功，手机授权已保存。",
            "authenticating" or "sending-bundle" or "awaiting-android" or "connecting" => "正在安全配对，请勿重复操作…",
            "expired" => "配对码已过期，请重新开始。",
            "exhausted" => "尝试次数达到上限，请稍后重新开始。",
            "failed" => "配对失败，请核对对方的配对码。",
            "discovery_unavailable" => "局域网发现暂不可用，可扫码连接。请检查防火墙和网络的组播支持。",
            _ => _manager.Running ? "扫码配对，或开始局域网搜索。" : "启用手机控制后可配对。"
        };
        if (_lan.Status == "completed") { ClearPairing(); RefreshClients(); }
    }
    private void SessionSwitched(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock)
        {
            _lan.Cancel(); _manager.Security.CancelPairing();
            Dispatcher.BeginInvoke(new Action(async () => { try { await _lan.StopAsync(); } finally { ClearPairing(); } }));
        }
    }
    private async Task FinishQrPairingAsync()
    {
        if (_lan.Status is not ("authenticating" or "sending-bundle" or "awaiting-android" or "connecting")) await _lan.StopAsync();
        if (!_closed) _status.Text = "手机授权已保存；短码模式仍以加密确认结果为准。";
    }
    private void ClearPairing() { _currentBundle = null; _bundle.Clear(); _qr.Source = null; _qr.Visibility = Visibility.Collapsed; }
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
        _status.Text = $"手机连接：{(_manager.Running ? "已启用" : "未启用")} · {_manager.Settings.PublicEndpoint}\nTailscale：{tailscale}";
    }
    private void RefreshClients() => _clients.ItemsSource = _manager.Security.Clients.ToArray();
}
