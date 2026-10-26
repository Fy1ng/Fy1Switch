using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QRCoder;
using Fy1Switch.Contracts;
using Fy1Switch.Remote;

namespace Fy1Switch.Host.UI;

internal sealed class RemoteConnectionView : IAsyncDisposable
{
    private readonly RemoteManager _manager;
    private readonly Window _owner;
    private readonly Action<string> _copyToClipboard;
    private readonly Func<IReadOnlyList<LocalNetworkChoice>> _getNetworks;
    private bool _pairingVisible, _settingsVisible;
    private Task _closeTask = Task.CompletedTask;
    private Task _activeAction = Task.CompletedTask;
    private long _visibilityGeneration;
    private readonly LanPairingCoordinator _lan;
    private readonly CheckBox _enabled = new() { Content = "启用手机控制", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox _automaticNetwork = new() { Content = "自动选择局域网", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox _tailscaleEnabled = new() { Content = "启用 Tailscale", Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox _relayEnabled = new() { Content = "启用中继", Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox _networks = new() { DisplayMemberPath = "Label", MinHeight = 34 };
    private readonly TextBox _listen = new(), _port = new(), _endpoint = new(), _tailscaleListen = new(), _tailscalePort = new(), _tailscaleEndpoint = new(), _relay = new();
    private readonly PasswordBox _relayToken = new();
    private readonly PasswordBox _relayRegistrationCode = new() { MaxLength = 6 };
    private readonly TextBlock _relayCodeText = Text("", 24, true), _relayCodeInfo = Text("", 12);
    private readonly Button _generateRelayCode, _cancelRelayCode;
    private RelayCode? _currentRelayCode;
    private CancellationTokenSource? _relayCodeCancellation;
    private Task _relayCleanup = Task.CompletedTask;
    private readonly TextBlock _status = Text("");
    private readonly TextBlock _networkSummary = Text("");
    private readonly TextBlock _tailscaleStatus = Text("", 12);
    private readonly TextBlock _pairStatus = Text("");
    private readonly TextBlock _codeLabel = Text("配对码", 12);
    private readonly TextBlock _code = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _clients = new() { MinHeight = 54, MaxHeight = 170, ToolTip = "显示授权时间, 不表示手机当前在线." };
    private readonly TextBlock _clientsEmpty = SettingsUi.Hint("尚未绑定手机. 点击连接手机开始配对.");
    private readonly StackPanel _configuration = new();
    private readonly StackPanel _tailscaleFields = new() { Margin = new Thickness(26, 8, 0, 0) };
    private readonly StackPanel _relayFields = new() { Margin = new Thickness(26, 8, 0, 0) };
    private readonly Expander _advanced = new();
    private readonly List<Button> _actionButtons = new();
    private readonly Button _primary, _copy, _revoke;
    private IReadOnlyList<LocalNetworkChoice> _networkChoices = Array.Empty<LocalNetworkChoice>();
    private LocalNetworkChoice? _detectedLan;
    private bool _updatingNetwork, _endpointGenerated;
    private readonly Image _qr = new() { Width = 220, Height = 220, Visibility = Visibility.Collapsed };
    private readonly Border _qrFrame;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PairBundle? _currentBundle;
    private bool _busy, _stopping, _closed, _sessionOpen, _successCleanup;
    private int _knownClients;
    private long _pairGeneration;
    private string? _notice;
    private DateTimeOffset? _noticeUntil;

    public RemoteConnectionView(RemoteManager manager, Window owner, Action<string>? copyToClipboard = null, Func<IReadOnlyList<LocalNetworkChoice>>? getNetworks = null)
    {
        _manager = manager; _owner = owner; _copyToClipboard = copyToClipboard ?? Clipboard.SetText;
        _getNetworks = getNetworks ?? LocalNetworkChoices.Get;
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

        var remote = new StackPanel();
        _enabled.Margin = new Thickness(8, 0, 0, 0);
        _enabled.FontSize = 12;
        remote.Children.Add(SettingsUi.Heading("远程控制", AppIconKind.Phone, _enabled));
        var devicesLabel = SettingsUi.Label("已绑定设备", 12, secondary: true);
        devicesLabel.Margin = new Thickness(0, 14, 0, 10);
        remote.Children.Add(devicesLabel);
        SettingsUi.List(_clients);
        System.Windows.Automation.AutomationProperties.SetName(_clients, "已绑定设备");
        remote.Children.Add(_clients);
        remote.Children.Add(_clientsEmpty);
        var clientActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AddButton(clientActions, "刷新设备", () => { RefreshClients(); return Task.CompletedTask; }, AppIconKind.Refresh);
        _revoke = AddButton(clientActions, "撤销授权", () =>
        {
            if (_clients.SelectedItem is ClientItem item && MessageBox.Show(_owner,
                "撤销后该手机将不能控制显示器, 确认继续?", "撤销授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _manager.Security.Revoke(item.Client.ClientId); Notice("已撤销该手机的授权.", transient: true);
            }
            RefreshClients(); return Task.CompletedTask;
        }, AppIconKind.Revoke);
        remote.Children.Add(clientActions);
        remote.Children.Add(SettingsUi.Separator());
        _tailscaleEnabled.Margin = new Thickness(0);
        remote.Children.Add(_tailscaleEnabled);
        _tailscalePort.SetBinding(TextBox.TextProperty, new Binding(nameof(TextBox.Text))
        {
            Source = _port, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        _tailscaleFields.Children.Add(SettingsUi.Fields("Tailscale IP 地址", _tailscaleListen, "端口号", _tailscalePort));
        _tailscaleEndpoint.IsReadOnly = true;
        _tailscaleStatus.Margin = new Thickness(0, 7, 0, 0);
        _tailscaleStatus.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        _tailscaleFields.Children.Add(_tailscaleStatus);
        remote.Children.Add(_tailscaleFields);
        remote.Children.Add(SettingsUi.Separator());
        _relayEnabled.Margin = new Thickness(0);
        remote.Children.Add(_relayEnabled);
        _relayFields.Children.Add(SettingsUi.Fields("中继 HTTPS 地址", _relay, "接入码", _relayRegistrationCode, 150));
        _relayRegistrationCode.ToolTip = "在中继 WebUI 生成的六位电脑注册码";
        var relayPairingActions = new WrapPanel();
        var register = AddButton(relayPairingActions, "注册到中继", RegisterRelayAsync, AppIconKind.Link);
        SettingsUi.Button(register, primary: true);
        register.Margin = new Thickness(0, 0, 8, 6);
        _generateRelayCode = AddButton(relayPairingActions, "生成手机中继配对码", GenerateRelayCodeAsync, AppIconKind.Phone);
        _cancelRelayCode = AddButton(relayPairingActions, "取消配对", CancelRelayCodeAsync, AppIconKind.Close);
        relayPairingActions.Margin = new Thickness(0, 12, 0, 0);
        _relayFields.Children.Add(relayPairingActions);
        _relayFields.Children.Add(_relayCodeText); _relayFields.Children.Add(_relayCodeInfo);
        _relayFields.Children.Add(SettingsUi.Hint("接入码由中继 WebUI 生成. 请只连接可信的中继服务."));
        remote.Children.Add(_relayFields);
        remote.Children.Add(SettingsUi.Separator());
        var actions = new WrapPanel();
        var save = AddButton(actions, "保存连接设置", SaveAsync, AppIconKind.Save);
        SettingsUi.Button(save, primary: true);
        save.Margin = new Thickness(0, 0, 8, 6);
        _primary = AddButton(actions, "连接手机", () =>
        {
            _owner.Dispatcher.BeginInvoke(new Action(() => { if (!_closed) PairingRequested?.Invoke(); }));
            return Task.CompletedTask;
        }, AppIconKind.Phone);
        remote.Children.Add(actions);
        _status.FontSize = 12;
        _status.Margin = new Thickness(0, 6, 0, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        remote.Children.Add(_status);
        _configuration.Children.Add(SettingsUi.Card(remote));

        var advanced = new StackPanel { Margin = new Thickness(26, 12, 0, 0) };
        _automaticNetwork.Margin = new Thickness(0, 0, 0, 12);
        advanced.Children.Add(_automaticNetwork);
        advanced.Children.Add(SettingsUi.Fields("局域网 IP 地址", _listen, "HTTPS 端口", _port));
        _networkSummary.FontSize = 12;
        _networkSummary.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        _networkSummary.Margin = new Thickness(0, 8, 0, 12);
        advanced.Children.Add(_networkSummary);
        Field(advanced, "自定义局域网", _networks);
        var networkButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        AddButton(networkButtons, "刷新网络列表", () => { LoadNetworks(); return Task.CompletedTask; }, AppIconKind.Refresh);
        AddButton(networkButtons, "重新检测 Tailscale", () => { LoadNetworks(); FillTailscale(force: true); return Task.CompletedTask; }, AppIconKind.Refresh);
        advanced.Children.Add(networkButtons);
        Field(advanced, "手机访问电脑的 HTTPS 地址", _endpoint);
        Field(advanced, "Tailscale HTTPS 地址", _tailscaleEndpoint);
        Field(advanced, "旧版中继凭据 (留空保留)", _relayToken);
        _copy = AddButton(advanced, "复制备用配对信息", CopyPairingAsync, AppIconKind.Copy);
        _copy.Margin = new Thickness(0, 12, 0, 0);
        _copy.ToolTip = "用于手机添加电脑时粘贴. 配对信息包含一次性凭据, 请勿公开分享.";
        var advancedHeading = new StackPanel { Orientation = Orientation.Horizontal };
        var expanded = new CheckBox { IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        expanded.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Expander.IsExpanded)) { Source = _advanced });
        advancedHeading.Children.Add(expanded);
        advancedHeading.Children.Add(SettingsUi.Label("高级设置", 16, bold: true));
        _advanced.Header = advancedHeading;
        _advanced.Content = advanced;
        _advanced.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.Expander");
        System.Windows.Automation.AutomationProperties.SetName(_advanced, "高级设置");
        _configuration.Children.Add(SettingsUi.Card(_advanced));
        SettingsContent = _configuration;
        LoadSavedSettings(); LoadNetworks(); RefreshClients(); _knownClients = _manager.Security.Clients.Count;
        _automaticNetwork.Checked += (_, _) => { if (!_updatingNetwork) LoadNetworks(preferCurrent: false); UpdateNetworkSummary(); };
        _automaticNetwork.Unchecked += (_, _) => UpdateNetworkSummary();
        _networks.SelectionChanged += (_, _) =>
        {
            if (!_updatingNetwork && _networks.SelectedItem is LocalNetworkChoice choice)
            {
                _endpointGenerated = true;
                SetLanAddress(choice.Address);
            }
        };
        _listen.TextChanged += (_, _) => { if (!_updatingNetwork) { UpdateGeneratedEndpoints(); UpdateNetworkSummary(); } };
        _endpoint.TextChanged += (_, _) => { if (!_updatingNetwork) { _endpointGenerated = false; UpdateNetworkSummary(); } };
        _port.TextChanged += (_, _) => { if (!_updatingNetwork) { UpdateGeneratedEndpoints(); UpdateNetworkSummary(); } };
        _tailscaleListen.TextChanged += (_, _) => { if (!_updatingNetwork) UpdateGeneratedEndpoints(); };
        _tailscaleEnabled.Checked += (_, _) => { FillTailscale(); UpdateOptionalForms(); };
        _tailscaleEnabled.Unchecked += (_, _) => UpdateOptionalForms();
        _relayEnabled.Checked += (_, _) => UpdateOptionalForms();
        _relayEnabled.Unchecked += (_, _) => { CancelRelayCodeInView(); UpdateOptionalForms(); };
        _relay.TextChanged += (_, _) => { if (_currentRelayCode is not null && _relay.Text.Trim().TrimEnd('/') != _manager.Settings.RelayUrl?.TrimEnd('/')) CancelRelayCodeInView(); };
        UpdateOptionalForms();
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
        if (visible) { LoadNetworks(); RefreshClients(); _timer.Start(); }
        else
        {
            _relayToken.Clear();
            _relayRegistrationCode.Clear();
            CancelRelayCodeInView();
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
        _automaticNetwork.IsChecked = saved.AutomaticNetwork ??
            (saved.ListenAddress == "127.0.0.1" && saved.PublicEndpoint == Endpoint(saved.ListenAddress, saved.Port));
        _endpointGenerated = saved.PublicEndpoint == Endpoint(saved.ListenAddress, saved.Port);
        _tailscaleEnabled.IsChecked = saved.UseTailscale; _relayEnabled.IsChecked = saved.UseRelay;
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true; _pairingVisible = false; _sessionOpen = false; Interlocked.Increment(ref _pairGeneration); _timer.Stop();
        SystemEvents.SessionSwitch -= SessionSwitched; _lan.Changed -= LanChanged;
        _lan.Cancel(); _manager.Security.CancelPairing(); ClearPairing(); _relayToken.Clear(); _relayRegistrationCode.Clear();
        CancelRelayCodeInView();
        await _relayCleanup;
        await _closeTask;
        await _activeAction;
        await _lan.DisposeAsync();
    }
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    {
        Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6)
    };
    private static void Field(Panel panel, string label, Control input)
    {
        var field = SettingsUi.Field(label, input);
        field.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(field);
    }
    private Button AddButton(Panel panel, string label, Func<Task> action, AppIconKind icon)
    {
        var button = new Button { Content = label };
        SettingsUi.Button(button);
        button.Margin = new Thickness(0, 0, 8, 6);
        AppIcons.SetButton(button, label, icon);
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
                "select_lan_address" or "no_lan_network" => "未识别到可用的局域网, 请连接 Wi-Fi 或以太网后刷新, 或在高级设置中手动配置.",
                "remote_disabled" => "请先启用手机控制。",
                "invalid_port" or "invalid_listener" => "请检查连接设置中的 IP 地址和端口（1024—65535）。",
                "invalid_endpoint" => "请填写有效的 HTTPS 连接地址。",
                "invalid_tailscale_listener" or "invalid_tailscale_endpoint" => "请检查 Tailscale IP 地址, 或关闭 Tailscale 后保存.",
                "relay_token_missing" => "请先使用 WebUI 六位注册码注册中继, 或关闭中继后保存.",
                "invalid_relay_code" => "请输入 WebUI 生成的六位数字注册码.",
                "relay_not_registered" => "请先注册中继并启用手机控制.",
                "relay_pairing_limited" => "生成配对码过于频繁, 请稍后再试.",
                _ when exception is RelayApiException => "中继请求未成功, 请检查注册码是否有效以及中继连接状态.",
                _ when exception is OperationCanceledException => "配对已停止，可重新开始连接。",
                _ => "操作未完成，请检查网络和连接设置后重试。"
            });
        }
        finally { _busy = false; if (!_closed) RenderPairing(); }
    }
    private void LoadNetworks(bool preferCurrent = true)
    {
        try { _networkChoices = _getNetworks(); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { _networkChoices = Array.Empty<LocalNetworkChoice>(); }
        _detectedLan = LocalNetworkChoices.SelectLan(_networkChoices, preferCurrent ? _listen.Text.Trim() : null);
        _updatingNetwork = true;
        try
        {
            _networks.ItemsSource = _networkChoices.Where(choice => !choice.Tailscale).ToArray();
            _networks.SelectedItem = _networkChoices.FirstOrDefault(choice => !choice.Tailscale && choice.Address == _listen.Text.Trim());
        }
        finally { _updatingNetwork = false; }
        if (_automaticNetwork.IsChecked == true && _detectedLan is { } lan)
        {
            _endpointGenerated = true;
            SetLanAddress(lan.Address);
        }
        FillTailscale();
        UpdateNetworkSummary();
    }
    private static string Endpoint(string address, int port) => new UriBuilder("https", address, port).Uri.GetLeftPart(UriPartial.Authority);
    private void SetLanAddress(string address)
    {
        _updatingNetwork = true;
        try
        {
            _listen.Text = address;
            _networks.SelectedItem = _networkChoices.FirstOrDefault(choice => !choice.Tailscale && choice.Address == address);
        }
        finally { _updatingNetwork = false; }
        UpdateGeneratedEndpoints(); UpdateNetworkSummary();
    }
    private void UpdateGeneratedEndpoints()
    {
        if (!int.TryParse(_port.Text, out var port) || port is < 1024 or > 65535) return;
        _updatingNetwork = true;
        try
        {
            if ((_automaticNetwork.IsChecked == true || _endpointGenerated) && IPAddress.TryParse(_listen.Text.Trim(), out var lan))
                _endpoint.Text = Endpoint(lan.ToString(), port);
            _tailscaleEndpoint.Text = IPAddress.TryParse(_tailscaleListen.Text.Trim(), out var tailscale) ? Endpoint(tailscale.ToString(), port) : "";
        }
        finally { _updatingNetwork = false; }
    }
    private void FillTailscale(bool force = false)
    {
        if (_tailscaleEnabled.IsChecked != true) return;
        var choice = LocalNetworkChoices.SelectTailscale(_networkChoices, force ? null : _tailscaleListen.Text.Trim());
        if (choice is not null && (force || string.IsNullOrWhiteSpace(_tailscaleListen.Text))) _tailscaleListen.Text = choice.Address;
        UpdateGeneratedEndpoints();
        _tailscaleStatus.Text = choice is null
            ? "未检测到 Tailscale 网络. 请连接 Tailscale 后重新检测, 或手动填写 IP."
            : $"已检测到: {choice.Name} / {choice.Address}. 保存后应用.";
    }
    private void UpdateOptionalForms()
    {
        _tailscaleFields.Visibility = _tailscaleEnabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _relayFields.Visibility = _relayEnabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateNetworkSummary()
    {
        var automatic = _automaticNetwork.IsChecked == true;
        _listen.IsReadOnly = automatic; _endpoint.IsReadOnly = automatic; _networks.IsEnabled = !automatic;
        var choice = _networkChoices.FirstOrDefault(candidate => !candidate.Tailscale && candidate.Address == _listen.Text.Trim());
        _networkSummary.Text = automatic && _detectedLan is null
            ? "未识别到可用局域网. 请连接 Wi-Fi 或以太网后刷新, 或在高级设置中手动配置."
            : $"{(automatic ? "自动选择" : "自定义网络")}: {choice?.Name ?? _listen.Text.Trim()}\n访问地址: {_endpoint.Text.Trim()}";
    }
    private RemoteSettings ReadSettings()
    {
        if (_enabled.IsChecked == true && _automaticNetwork.IsChecked == true)
        {
            LoadNetworks();
            if (_detectedLan is null) throw new InvalidOperationException("no_lan_network");
        }
        if (!int.TryParse(_port.Text, out var port)) throw new ArgumentException("invalid_port");
        var unchangedRelay = _relay.Text.Trim().TrimEnd('/') == _manager.Settings.RelayUrl?.TrimEnd('/');
        var secret = string.IsNullOrWhiteSpace(_relayToken.Password) ? unchangedRelay ? _manager.Settings.ProtectedRelayToken : null : _manager.ProtectedRelayToken(_relayToken.Password);
        var settings = new RemoteSettings(_enabled.IsChecked == true, _listen.Text.Trim(), port, _endpoint.Text.Trim(),
            string.IsNullOrWhiteSpace(_relay.Text) ? null : _relay.Text.Trim(), secret,
            string.IsNullOrWhiteSpace(_tailscaleEndpoint.Text) ? null : _tailscaleEndpoint.Text.Trim(),
            string.IsNullOrWhiteSpace(_tailscaleListen.Text) ? null : _tailscaleListen.Text.Trim(),
            AutomaticNetwork: _automaticNetwork.IsChecked == true,
            TailscaleEnabled: _tailscaleEnabled.IsChecked == true, RelayEnabled: _relayEnabled.IsChecked == true);
        settings.Validate();
        return settings;
    }
    private async Task SaveAsync()
    {
        var settings = ReadSettings();
        CancelRelayCodeInView();
        await _relayCleanup;
        _sessionOpen = false;
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
    private async Task RegisterRelayAsync()
    {
        var code = _relayRegistrationCode.Password;
        if (!DeviceSecurity.IsSixDigitCode(code)) throw new ArgumentException("invalid_relay_code");
        var settings = ReadSettings() with { RelayEnabled = true };
        CancelRelayCodeInView(); await _relayCleanup;
        await _lan.StopAsync(); ClearPairing();
        try { await _manager.RegisterRelayAsync(settings, code); }
        finally { _relayRegistrationCode.Clear(); }
        if (!_closed)
        {
            LoadSavedSettings(); _relayToken.Clear();
            Notice("已注册到中继. 已配对手机可在局域网自动获取中继配置.", transient: true);
        }
    }
    private async Task GenerateRelayCodeAsync()
    {
        CancelRelayCodeInView(); await _relayCleanup;
        if (_closed || !_settingsVisible) return;
        var cancellation = new CancellationTokenSource();
        _relayCodeCancellation = cancellation;
        try
        {
            var code = await _manager.BeginRelayPairingAsync(cancellation.Token);
            if (_closed || !_settingsVisible || cancellation.IsCancellationRequested)
            {
                await _manager.CancelRelayPairingAsync(); return;
            }
            _currentRelayCode = code;
            RenderRelayCode();
        }
        finally
        {
            if (ReferenceEquals(_relayCodeCancellation, cancellation)) _relayCodeCancellation = null;
            cancellation.Dispose();
        }
    }
    private async Task CancelRelayCodeAsync()
    {
        CancelRelayCodeInView(); await _relayCleanup;
        if (!_closed) Notice("Relay 配对已取消.", transient: true);
    }
    private void CancelRelayCodeInView()
    {
        var active = _relayCodeCancellation is not null || _currentRelayCode is not null || _manager.Security.RelayPairingActive;
        _relayCodeCancellation?.Cancel();
        _currentRelayCode = null;
        _relayCodeText.Text = ""; _relayCodeInfo.Text = "";
        if (active) _relayCleanup = _manager.CancelRelayPairingAsync();
    }
    private void RenderRelayCode()
    {
        if (_currentRelayCode is { } code && _settingsVisible && _manager.Security.RelayPairingActive)
        {
            var seconds = Math.Max(0, (int)(code.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            _relayCodeText.Text = code.Code[..3] + " " + code.Code[3..];
            _relayCodeInfo.Text = $"手机 Relay 配对码, 剩余 {seconds / 60}:{seconds % 60:00}. 仅使用一次.";
        }
        else
        {
            _relayCodeText.Text = "";
            if (_currentRelayCode is not null)
            {
                _currentRelayCode = null; _relayCodeInfo.Text = "配对码已使用或已失效, 可重新生成.";
                RefreshClients();
            }
        }
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
        RenderRelayCode();
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
        _status.Text = _busy ? "正在处理，请稍候……" : _notice ?? (_manager.Running ? "手机控制已启用。" : "手机控制未启用. 启用并保存后即可连接手机.");
        var tailscale = _manager.TailscaleStatus switch { "listening" => "已启用", "unavailable" => "暂不可用", _ => "未启用" };
        _status.ToolTip = $"当前地址: {_manager.Settings.PublicEndpoint}\nTailscale: {tailscale}";
        _relayCodeText.Visibility = string.IsNullOrEmpty(_relayCodeText.Text) ? Visibility.Collapsed : Visibility.Visible;
        _relayCodeInfo.Visibility = string.IsNullOrEmpty(_relayCodeInfo.Text) ? Visibility.Collapsed : Visibility.Visible;
        RefreshActions();
    }
    private void RefreshActions()
    {
        if (_closed) return;
        var ready = !_busy && !_stopping && !Handshake;
        foreach (var button in _actionButtons) button.IsEnabled = ready;
        _configuration.IsEnabled = ready;
        _primary.IsEnabled = ready && _manager.Running;
        _copy.IsEnabled = ready && _manager.Running;
        _generateRelayCode.IsEnabled = ready && _manager.Running && _manager.Settings.UseRelay;
        _cancelRelayCode.IsEnabled = ready && _currentRelayCode is not null;
        _cancelRelayCode.Visibility = _currentRelayCode is not null ? Visibility.Visible : Visibility.Collapsed;
        _revoke.IsEnabled = ready && _clients.SelectedItem is ClientItem;
    }
    private void SessionSwitched(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason != SessionSwitchReason.SessionLock) return;
        _relayCleanup = _manager.CancelRelayPairingAsync();
        _sessionOpen = false; Interlocked.Increment(ref _pairGeneration); _lan.Cancel(); _manager.Security.CancelPairing();
        if (!_closed && !_owner.Dispatcher.HasShutdownStarted) _owner.Dispatcher.BeginInvoke(new Action(async () =>
        {
            CancelRelayCodeInView(); _relayToken.Clear(); _relayRegistrationCode.Clear(); await ClosePairingAsync();
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
    private sealed class ClientItem : ListBoxItem
    {
        internal PairedClient Client { get; }
        public string Label => $"{Client.Name}\n授权于 {Client.PairedAt.ToLocalTime():yyyy/MM/dd HH:mm}";
        internal ClientItem(PairedClient client)
        {
            Client = client;
            Content = SettingsUi.DeviceRow(client.Name, $"授权于 {client.PairedAt.ToLocalTime():yyyy-MM-dd HH:mm}", AppIconKind.Phone, "已授权");
            System.Windows.Automation.AutomationProperties.SetName(this, Label);
        }
    }
    private void RefreshClients()
    {
        var authorized = _manager.Security.Clients;
        _clients.Visibility = authorized.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _clientsEmpty.Visibility = authorized.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_clients.Items.OfType<ClientItem>().Select(item => item.Client).SequenceEqual(authorized)) return;
        var selected = (_clients.SelectedItem as ClientItem)?.Client.ClientId;
        var clients = authorized.Select(client => new ClientItem(client)).ToArray();
        _clients.ItemsSource = clients;
        _clients.SelectedItem = clients.FirstOrDefault(item => item.Client.ClientId == selected);
    }
}
