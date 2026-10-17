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

public sealed class RemoteWindow : Window
{
    private readonly RemoteManager _manager;
    private readonly LanPairingCoordinator _lan;
    private readonly CheckBox _enabled = new() { Content = "启用手机控制", Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox _networks = new() { DisplayMemberPath = "Label", MinHeight = 34 };
    private readonly TextBox _listen = new(), _port = new(), _endpoint = new(), _tailscaleListen = new(), _tailscaleEndpoint = new(), _relay = new();
    private readonly PasswordBox _relayToken = new();
    private readonly TextBlock _status = Text("");
    private readonly TextBlock _pairHint = Text("");
    private readonly TextBlock _code = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _clients = new() { DisplayMemberPath = "Label", MinHeight = 60, MaxHeight = 160, ToolTip = "显示授权时间，不表示手机当前在线。" };
    private readonly StackPanel _configuration = new();
    private readonly Expander _settings = new() { Header = "连接设置", Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _pairCard;
    private readonly List<Button> _actionButtons = new();
    private readonly Button _primary, _useNetwork, _copy, _revoke;
    private readonly Button _stop = new() { Content = "停止配对", Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 8, 0, 8) };
    private readonly Image _qr = new() { Width = 220, Height = 220, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairBundle? _currentBundle;
    private bool _busy, _stopping, _closed, _sessionOpen, _successCleanup;
    private int _knownClients;
    private long _pairGeneration;
    private string? _notice;
    private DateTimeOffset? _noticeUntil;

    public RemoteWindow(RemoteManager manager)
    {
        _manager = manager;
        _lan = new LanPairingCoordinator(manager);
        _lan.Changed += LanChanged;
        DesktopTheme.Attach(this);
        Title = "连接手机"; Width = 680; Height = 700; MinWidth = 520; MinHeight = 520;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        var page = new StackPanel { Margin = new Thickness(20, 12, 20, 20) };
        page.Children.Add(Text("用手机控制显示器", 24, true));
        page.Children.Add(Text("在手机打开“添加电脑”，扫描电脑二维码或输入电脑短码。"));
        page.Children.Add(Card(_status));
        var start = new WrapPanel();
        _primary = AddButton(start, "启用手机控制", StartConnectionAsync);
        _primary.SetResourceReference(Control.BackgroundProperty, DesktopTheme.AccentBrush);
        _primary.SetResourceReference(Control.ForegroundProperty, DesktopTheme.AccentForegroundBrush);
        _stop.Click += async (_, _) => await StopPairingAsync(); start.Children.Add(_stop);
        page.Children.Add(start);
        var pairContent = new Grid();
        pairContent.ColumnDefinitions.Add(new ColumnDefinition());
        pairContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var instructions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        instructions.Children.Add(Text("扫描电脑二维码", 18, true));
        instructions.Children.Add(_pairHint);
        instructions.Children.Add(_code);
        pairContent.Children.Add(instructions); Grid.SetColumn(_qr, 1); pairContent.Children.Add(_qr);
        _pairCard = Card(pairContent); page.Children.Add(_pairCard);
        _configuration.Children.Add(_enabled);
        _configuration.Children.Add(Text("有多个网络时，请选择手机能够访问的电脑网络。"));
        Field(_configuration, "电脑当前网络", _networks);
        var networkButtons = new WrapPanel();
        _useNetwork = AddButton(networkButtons, "使用此网络并连接", UseNetworkAsync);
        AddButton(networkButtons, "刷新网络列表", () => { LoadNetworks(); return Task.CompletedTask; });
        _configuration.Children.Add(networkButtons);
        _configuration.Children.Add(Text("高级连接选项", 16, true));
        Field(_configuration, "局域网 IP 地址", _listen); Field(_configuration, "HTTPS 端口", _port);
        Field(_configuration, "手机访问电脑的 HTTPS 地址", _endpoint);
        Field(_configuration, "Tailscale IP 地址（可选）", _tailscaleListen);
        Field(_configuration, "Tailscale HTTPS 地址（可选）", _tailscaleEndpoint);
        _configuration.Children.Add(Text("Tailscale 用于备用连接，两项留空可关闭。", 12));
        Field(_configuration, "中继 HTTPS 地址（可选）", _relay);
        Field(_configuration, "电脑端中继凭据（留空保留）", _relayToken);
        _configuration.Children.Add(Text("中继服务可以读取转发内容。请只使用你信任的服务。不会自动修改防火墙或路由器。", 12));
        var apply = new WrapPanel(); AddButton(apply, "保存连接设置", SaveAsync);
        _copy = AddButton(apply, "复制备用配对信息", CopyPairingAsync);
        _copy.ToolTip = "用于手机“添加电脑”中的粘贴方式。配对信息包含一次性凭据，请勿公开分享。";
        _configuration.Children.Add(apply);
        _settings.Content = _configuration; page.Children.Add(_settings);
        var clients = new StackPanel(); clients.Children.Add(_clients);
        var clientActions = new WrapPanel();
        AddButton(clientActions, "刷新手机列表", () => { RefreshClients(); return Task.CompletedTask; });
        _revoke = AddButton(clientActions, "撤销手机授权", () =>
        {
            if (_clients.SelectedItem is ClientItem item && MessageBox.Show(this,
                "撤销后该手机将不能控制显示器，确认继续？", "撤销授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _manager.Security.Revoke(item.Client.ClientId);
                Notice("已撤销该手机的授权。", transient: true);
            }
            RefreshClients(); return Task.CompletedTask;
        });
        clients.Children.Add(clientActions);
        page.Children.Add(new Expander { Header = "已授权手机", Content = clients, Margin = new Thickness(0, 12, 0, 0) });
        Content = WindowFrame.Wrap(this, new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _enabled.IsChecked = manager.Settings.Enabled; _listen.Text = manager.Settings.ListenAddress;
        _port.Text = manager.Settings.Port.ToString(); _endpoint.Text = manager.Settings.PublicEndpoint;
        _relay.Text = manager.Settings.RelayUrl ?? "";
        _tailscaleListen.Text = manager.Settings.TailscaleListenAddress ?? "";
        _tailscaleEndpoint.Text = manager.Settings.TailscaleEndpoint ?? "";
        LoadNetworks(); RefreshClients(); _knownClients = _manager.Security.Clients.Count;
        _settings.IsExpanded = !_manager.Running;
        _networks.SelectionChanged += (_, _) => RefreshActions();
        _clients.SelectionChanged += (_, _) => RefreshActions();
        RenderPairing();
        Loaded += async (_, _) => { if (_manager.Running) await RunAsync(StartPairingAsync); };
        _timer.Tick += async (_, _) => { RenderPairing(); if (CanRenew()) await RunAsync(StartPairingAsync); };
        _timer.Start();
        SystemEvents.SessionSwitch += SessionSwitched;
        Closed += async (_, _) =>
        {
            _closed = true; _sessionOpen = false; Interlocked.Increment(ref _pairGeneration); _timer.Stop();
            SystemEvents.SessionSwitch -= SessionSwitched; _lan.Changed -= LanChanged;
            _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); _relayToken.Clear();
            try { await _lan.DisposeAsync(); } catch { }
        };
    }
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    {
        Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6)
    };
    private static Border Card(UIElement child)
    {
        var card = new Border { Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, DesktopTheme.SurfaceBrush);
        card.SetResourceReference(Border.BorderBrushProperty, DesktopTheme.BorderBrush);
        return card;
    }
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
    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _busy || _stopping) return;
        _busy = true; _notice = null; RefreshActions();
        try { await action(); }
        catch (Exception exception)
        {
            if (!_closed) Notice(exception.Message switch
            {
                "select_lan_address" => "请在连接设置中选择电脑当前网络，然后点击“使用此网络并连接”。",
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
    private async Task StartConnectionAsync()
    {
        if (_manager.Running) { await StartPairingAsync(); return; }
        _enabled.IsChecked = true;
        if (Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var endpoint) && endpoint.IsLoopback &&
            string.IsNullOrWhiteSpace(_tailscaleEndpoint.Text) && string.IsNullOrWhiteSpace(_relay.Text))
        {
            var choices = (_networks.ItemsSource as IEnumerable<LocalNetworkChoice>)?.Where(choice => !choice.Tailscale).ToArray();
            if (choices is { Length: 1 }) { _networks.SelectedItem = choices[0]; await UseNetworkAsync(); return; }
            _settings.IsExpanded = true;
            Notice("请选择手机能够访问的电脑网络，然后点击“使用此网络并连接”。");
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
        _relayToken.Clear(); _settings.IsExpanded = !_manager.Running;
        if (_manager.Running) await StartPairingAsync();
        else Notice("手机控制已关闭，已授权的手机仍保留。", transient: true);
    }
    private bool IsLanConfiguration() => IPAddress.TryParse(new Uri(_manager.Settings.PublicEndpoint).Host, out var address) && LocalNetworkChoices.Eligible(address, false);
    private bool Handshake => _lan.Status is "authenticating" or "sending-bundle" or "awaiting-android" or "connecting";
    private async Task StartPairingAsync()
    {
        _sessionOpen = false;
        var generation = Interlocked.Increment(ref _pairGeneration);
        await _lan.StopAsync(); ClearPairing();
        if (_closed || generation != Interlocked.Read(ref _pairGeneration)) return;
        if (!_manager.Running) throw new InvalidOperationException("remote_disabled");
        _currentBundle = _manager.BeginPairing(); RenderQr(WireJson.Serialize(_currentBundle));
        _knownClients = _manager.Security.Clients.Count; _successCleanup = false; _notice = null;
        if (IsLanConfiguration()) await _lan.StartAsync();
        if (_closed || generation != Interlocked.Read(ref _pairGeneration))
        {
            _manager.Security.CancelPairing(); await _lan.StopAsync(); ClearPairing(); return;
        }
        _sessionOpen = true; RenderPairing();
    }
    private bool CanRenew() => !_closed && !_busy && !_stopping && _sessionOpen && _manager.Running
        && PairingRenewalPolicy.ShouldRenew(_lan.Status, _currentBundle?.ExpiresAt, DateTimeOffset.UtcNow);
    private async Task StopPairingAsync()
    {
        if (_closed || _stopping) return;
        _stopping = true; _sessionOpen = false; Interlocked.Increment(ref _pairGeneration);
        _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); RefreshActions();
        try { await _lan.StopAsync(); if (!_closed) Notice("配对已停止，可重新开始连接。"); }
        catch { if (!_closed) Notice("配对已取消，请重新开始连接。"); }
        finally { _stopping = false; if (!_closed) RenderPairing(); }
    }
    private Task CopyPairingAsync()
    {
        if (_currentBundle is { } bundle && bundle.ExpiresAt > DateTimeOffset.UtcNow && !Handshake)
        {
            Clipboard.SetText(WireJson.Serialize(bundle));
            Notice("已复制备用配对信息，请仅粘贴到你信任的手机。", transient: true);
        }
        return Task.CompletedTask;
    }
    private void LanChanged() { if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RenderPairing)); }
    private void RenderPairing()
    {
        if (_closed) return;
        if (!_successCleanup && _sessionOpen && _manager.Security.Clients.Count > _knownClients)
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
        _pairHint.Text = hasCode ? "也可在手机选择这台电脑，输入下方六位短码。" : "在手机“添加电脑”中扫描此二维码。";
        _code.Text = hasCode ? _lan.Code[..3] + " " + _lan.Code[3..] : "";
        _code.Visibility = hasCode ? Visibility.Visible : Visibility.Collapsed;
        _qr.Visibility = hasBundle && _qr.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_noticeUntil <= now) { _notice = null; _noticeUntil = null; }
        var remaining = Math.Max(0, (int)((_currentBundle?.ExpiresAt ?? now) - now).TotalSeconds);
        _status.Text = _stopping ? "正在停止配对……" : Handshake ? "正在连接手机，请稍候……" : _notice ?? (_lan.Status switch
        {
            "listening" when hasBundle => $"等待手机连接，配对码剩余 {remaining / 60}:{remaining % 60:00}。",
            "rejected" => "短码未匹配，请在手机核对后重新输入。",
            "exhausted" => "尝试次数已达上限，请稍后手动重新配对。",
            "failed" => "连接未完成，请在两端重新配对。",
            "expired" when _sessionOpen => "配对码已过期，正在更新……",
            "discovery_unavailable" when hasBundle => $"附近搜索暂不可用，请在手机扫描二维码。二维码剩余 {remaining / 60}:{remaining % 60:00}。",
            _ => hasBundle ? $"请在手机扫描二维码，剩余 {remaining / 60}:{remaining % 60:00}。"
                : _manager.Running ? "手机控制已启用。连接新手机时，点击“开始配对”。"
                : "手机控制未启用。点击“启用手机控制”开始连接。"
        });
        var tailscale = _manager.TailscaleStatus switch { "listening" => "已启用", "unavailable" => "暂不可用", _ => "未启用" };
        _settings.ToolTip = $"当前地址：{_manager.Settings.PublicEndpoint}\nTailscale：{tailscale}";
        RefreshActions();
    }
    private void RefreshActions()
    {
        if (_closed) return;
        var ready = !_busy && !_stopping && !Handshake;
        var pairing = _currentBundle is not null || _lan.Active && (_lan.Status is "listening" or "rejected" || Handshake);
        foreach (var button in _actionButtons) button.IsEnabled = ready;
        _configuration.IsEnabled = ready;
        _pairCard.Visibility = _currentBundle is { } current && current.ExpiresAt > DateTimeOffset.UtcNow && !Handshake
            ? Visibility.Visible : Visibility.Collapsed;
        _primary.Content = !_manager.Running ? "启用手机控制" : pairing ? "更新配对码" : "开始配对";
        _useNetwork.IsEnabled = ready && _networks.SelectedItem is LocalNetworkChoice;
        _copy.IsEnabled = ready && _currentBundle is { } bundle && bundle.ExpiresAt > DateTimeOffset.UtcNow;
        _revoke.IsEnabled = ready && _clients.SelectedItem is ClientItem;
        _stop.IsEnabled = !_stopping && (pairing || _busy);
        _stop.Visibility = pairing || _busy || _stopping ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SessionSwitched(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason != SessionSwitchReason.SessionLock) return;
        _sessionOpen = false; Interlocked.Increment(ref _pairGeneration); _lan.Cancel(); _manager.Security.CancelPairing();
        if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(async () => await StopPairingAsync()));
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
