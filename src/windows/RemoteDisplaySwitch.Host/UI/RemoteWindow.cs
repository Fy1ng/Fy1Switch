using System;
using System.Linq;
using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Remote;

namespace RemoteDisplaySwitch.Host.UI;

public sealed class RemoteWindow : Window
{
    private readonly RemoteManager _manager;
    private readonly CheckBox _enabled = new() { Content = "启用手机远程控制（HTTPS）", Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox _listen = new();
    private readonly TextBox _port = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _relay = new();
    private readonly PasswordBox _relayToken = new();
    private readonly TextBox _bundle = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 105, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListBox _clients = new() { DisplayMemberPath = "Name", MinHeight = 70 };
    private readonly StackPanel _page = new() { Margin = new Thickness(22) };
    private PairBundle? _currentBundle;
    private readonly Image _qr = new() { Width = 260, Height = 260, Margin = new Thickness(0, 10, 0, 10), Visibility = Visibility.Collapsed };
    private bool _busy;
    private readonly System.Windows.Threading.DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public RemoteWindow(RemoteManager manager)
    {
        _manager = manager;
        Resources.MergedDictionaries.Add(DesktopTheme.CreateResources());
        Title = "手机配对与远程连接"; Width = 680; Height = 790; MinWidth = 560; MinHeight = 500;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 250));
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        _page.Children.Add(new TextBlock { Text = "手机配对与远程连接", FontSize = 23, FontWeight = FontWeights.SemiBold });
        _page.Children.Add(new TextBlock { Text = "先在主窗口绑定显示器。默认只监听本机；要用局域网或 Tailscale，请填对应本机 IP，并在防火墙允许该端口。不会自动修改防火墙。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        _page.Children.Add(_enabled);
        Field("监听本机 IP（127.0.0.1 仅本机；0.0.0.0 所有 IPv4 接口）", _listen);
        Field("HTTPS 端口（1024–65535）", _port);
        Field("手机使用的地址，例如 https://100.x.x.x:48152", _endpoint);
        Field("自建 Relay 地址（可选，https://你的域名）", _relay);
        Field("Windows 接入 Relay 的凭据（留空保留；不是手机凭据）", _relayToken);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button(buttons, "保存并应用", async () =>
        {
            if (!int.TryParse(_port.Text, out var port)) throw new ArgumentException();
            var protectedToken = string.IsNullOrWhiteSpace(_relayToken.Password) ? _manager.Settings.ProtectedRelayToken : _manager.ProtectedRelayToken(_relayToken.Password);
            await _manager.ApplyAsync(new(_enabled.IsChecked == true, _listen.Text.Trim(), port, _endpoint.Text.Trim(), string.IsNullOrWhiteSpace(_relay.Text) ? null : _relay.Text.Trim(), protectedToken));
            _relayToken.Clear(); ClearPairing(); RefreshStatus();
        });
        Button(buttons, "刷新连接状态", () => { RefreshStatus(); return System.Threading.Tasks.Task.CompletedTask; });
        _page.Children.Add(buttons);
        _page.Children.Add(_status);
        _page.Children.Add(new TextBlock { Text = "配对信息包含临时秘密，只应交给自己的手机。配对后每台手机获得独立凭据；Relay 是可信服务器，可读取转发内容，不是端到端加密。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var pairButtons = new WrapPanel();
        Button(pairButtons, "显示配对二维码", () => { _currentBundle = _manager.BeginPairing(); _bundle.Text = WireJson.Serialize(_currentBundle); RenderQr(_bundle.Text); _status.Text = "用手机 App 扫码，五分钟内完成。旧二维码已失效。"; return System.Threading.Tasks.Task.CompletedTask; });
        Button(pairButtons, "复制配对信息", () =>
        {
            if (_currentBundle is null || _currentBundle.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException();
            Clipboard.SetText(_bundle.Text); _status.Text = "已复制；配对完成后请手动清除剪贴板中的配对信息。"; return System.Threading.Tasks.Task.CompletedTask;
        });
        _page.Children.Add(pairButtons); _page.Children.Add(_qr); _page.Children.Add(new Expander { Header = "无法扫码？复制配对信息", Content = _bundle });
        _page.Children.Add(new TextBlock { Text = "已配对手机", Margin = new Thickness(0, 12, 0, 6), FontWeight = FontWeights.SemiBold });
        _page.Children.Add(_clients);
        var revokeButtons = new WrapPanel();
        Button(revokeButtons, "刷新列表", () => { RefreshClients(); return System.Threading.Tasks.Task.CompletedTask; });
        Button(revokeButtons, "撤销所选手机", () => { if (_clients.SelectedItem is PairedClient client) _manager.Security.Revoke(client.ClientId); RefreshClients(); return System.Threading.Tasks.Task.CompletedTask; });
        _page.Children.Add(revokeButtons);
        Content = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Background };
        _enabled.IsChecked = manager.Settings.Enabled; _listen.Text = manager.Settings.ListenAddress; _port.Text = manager.Settings.Port.ToString(); _endpoint.Text = manager.Settings.PublicEndpoint; _relay.Text = manager.Settings.RelayUrl ?? string.Empty;
        RefreshStatus(); RefreshClients();
        _expiryTimer.Tick += (_, _) =>
        {
            if (_currentBundle is { } bundle && bundle.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                ClearPairing(); _status.Text = "二维码已过期，请重新生成。";
            }
        };
        _expiryTimer.Start();
        Closed += (_, _) => { _expiryTimer.Stop(); _manager.Security.CancelPairing(); ClearPairing(); _relayToken.Clear(); };
    }
    private void ClearPairing()
    {
        _currentBundle = null; _bundle.Clear(); _qr.Source = null; _qr.Visibility = Visibility.Collapsed;
    }
    private void RenderQr(string payload)
    {
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        using var stream = new MemoryStream(code.GetGraphic(6));
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        _qr.Source = image; _qr.Visibility = Visibility.Visible;
    }
    private void Field(string label, Control input)
    {
        _page.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 3) });
        input.MinHeight = 30; _page.Children.Add(input);
    }
    private void Button(Panel panel, string label, Func<System.Threading.Tasks.Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 5, 8, 5) };
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; _page.IsEnabled = false;
            try { await action(); }
            catch { _status.Text = "操作失败。请检查 IP、端口、HTTPS 地址或 Relay 凭据；服务若未成功启动，不会开放控制。"; }
            finally { _busy = false; _page.IsEnabled = true; }
        };
        panel.Children.Add(button);
    }
    private void RefreshStatus() => _status.Text = $"HTTPS：{(_manager.Running ? "已运行" : "未运行")}  ·  Relay：{(_manager.RelayStatus switch { "connected" => "已连接", "connecting" => "连接中", "unavailable" => "暂不可用", "disconnected" => "未连接", _ => "未启用" })}";
    private void RefreshClients() => _clients.ItemsSource = _manager.Security.Clients.Select(client => client).ToArray();
}
