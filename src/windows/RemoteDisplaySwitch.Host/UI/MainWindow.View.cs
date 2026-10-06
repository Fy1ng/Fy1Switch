using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteDisplaySwitch.Contracts;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;

namespace RemoteDisplaySwitch.Host.UI;

public sealed partial class MainWindow
{
    private static readonly Brush Ink = FrozenBrush(31, 41, 55);
    private static readonly Brush Muted = FrozenBrush(75, 85, 99);
    private static readonly Brush Line = FrozenBrush(213, 219, 226);
    private readonly TextBlock _device = Text();
    private readonly TextBlock _binding = Text();
    private readonly TextBlock _power = Text(size: 34);
    private readonly TextBlock _source = Text();
    private readonly TextBlock _updated = Text();
    private readonly TextBlock _worker = Text();
    private readonly TextBlock _operation = Text();
    private readonly TextBlock _status = Text();
    private readonly TextBlock _details = Text();
    private readonly TextBlock _discoveryStatus = Text();
    private readonly TextBlock _identity = Text();
    private readonly Border _simulationBanner = new();
    private readonly Border _statusBox = new();
    private readonly Expander _detailsSection = new() { Header = "详细信息" };
    private readonly Expander _bindingSection = new() { Header = "显示器绑定" };
    private readonly ListBox _candidates = new() { MinHeight = 72, MaxHeight = 174 };
    private readonly CheckBox _startup = new() { Content = "登录 Windows 时自动启动", MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _on = Button("开启");
    private readonly Button _off = Button("关闭");
    private readonly Button _toggle = Button("刷新状态");
    private readonly Button _remote = Button("连接手机  →");
    private readonly Button _refresh = Button("刷新");
    private readonly Button _scan = Button("发现显示器");
    private readonly Button _bind = Button("绑定所选");
    private readonly Button _unbind = Button("取消绑定");

    private MonitorCandidate? SelectedCandidate => (_candidates.SelectedItem as ListBoxItem)?.Tag as MonitorCandidate;

    private void InitializeView()
    {
        Title = "RemoteDisplaySwitch";
        Width = 740;
        Height = 600;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 250));
        Foreground = Ink;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Resources.MergedDictionaries.Add(DesktopTheme.CreateResources());

        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        page.Children.Add(Text("显示器开关", 28, FontWeights.SemiBold));
        page.Children.Add(Text("RemoteDisplaySwitch · 关闭窗口后仍可从托盘打开", 12, margin: new Thickness(0, 4, 0, 20)));
        _simulationBanner.Background = new SolidColorBrush(Color.FromRgb(255, 242, 204));
        _simulationBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(171, 119, 20));
        _simulationBanner.BorderThickness = new Thickness(1);
        _simulationBanner.CornerRadius = new CornerRadius(6);
        _simulationBanner.Padding = new Thickness(12, 9, 12, 9);
        _simulationBanner.Margin = new Thickness(0, 0, 0, 12);
        _simulationBanner.Child = Text("模拟模式 · 不控制实体硬件，所有状态和操作结果仅用于演示。", weight: FontWeights.SemiBold);
        page.Children.Add(_simulationBanner);

        var state = new StackPanel();
        state.Children.Add(_device);
        _device.Foreground = Muted;
        _binding.FontSize = 20;
        _binding.FontWeight = FontWeights.SemiBold;
        state.Children.Add(_binding);
        _power.Margin = new Thickness(0, 20, 0, 18);
        _power.FontWeight = FontWeights.SemiBold;
        state.Children.Add(_power);
        _worker.Foreground = Muted;
        _operation.Margin = new Thickness(0, 7, 0, 0);
        _toggle.MinHeight = 56;
        _toggle.FontSize = 18;
        _toggle.FontWeight = FontWeights.SemiBold;
        _toggle.Margin = new Thickness(0, 0, 0, 8);
        _toggle.Background = FrozenBrush(45, 92, 180);
        _toggle.Foreground = Brushes.White;
        state.Children.Add(_toggle);
        state.Children.Add(Text("以最近可信读回为准 · 未知状态不会盲目切换", 12));
        page.Children.Add(Card(state));

        var commands = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        foreach (var button in new[] { _on, _off, _refresh })
            commands.Children.Add(button);
        _on.ToolTip = "请求开启当前绑定的显示器，等待控制结果，不提前改变状态。";
        _off.ToolTip = "请求关闭当前绑定的显示器，等待控制结果，不提前改变状态。";
        _toggle.ToolTip = "已知状态时发送明确的开启或关闭请求；未知时只刷新状态。";
        _refresh.ToolTip = "手动读取硬件状态；界面自动更新只读取缓存，不扫描硬件。";

        _statusBox.Padding = new Thickness(12, 9, 12, 9);
        _statusBox.CornerRadius = new CornerRadius(6);
        _statusBox.Margin = new Thickness(0, 0, 0, 12);
        var feedback = new StackPanel();
        feedback.Children.Add(_status);
        _details.Margin = new Thickness(0, 6, 0, 0);
        _details.FontSize = 12;
        _detailsSection.Content = _details;
        _detailsSection.Margin = new Thickness(0, 6, 0, 0);
        _statusBox.Child = feedback;
        _statusBox.Margin = new Thickness(0, 12, 0, 16);
        page.Children.Add(_statusBox);

        var phone = new StackPanel();
        phone.Children.Add(Text("手机也能控制", 18, FontWeights.SemiBold));
        phone.Children.Add(Text("配对手机，随时查看和控制这台显示器。", margin: new Thickness(0, 5, 0, 12)));
        _remote.HorizontalAlignment = HorizontalAlignment.Left;
        _remote.Click += (_, _) => { if (!IsBusy) RemoteSettingsRequested?.Invoke(); };
        phone.Children.Add(_remote);
        page.Children.Add(Card(phone));

        var settings = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var settingsSection = new Expander { Header = "设置与诊断", Content = settings, Margin = new Thickness(0, 18, 0, 0) };
        _bindingSection.Expanded += (_, _) => settingsSection.IsExpanded = true;

        var binding = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        binding.Children.Add(Text("1. 发现或重扫 → 2. 明确选择候选 → 3. 绑定所选", weight: FontWeights.SemiBold));
        binding.Children.Add(Text("唯一候选会预选，仍需你确认绑定；绑定不会开关显示器。", margin: new Thickness(0, 5, 0, 8)));
        var bindingCommands = new WrapPanel();
        foreach (var button in new[] { _scan, _bind, _unbind })
            bindingCommands.Children.Add(button);
        binding.Children.Add(bindingCommands);
        _discoveryStatus.Margin = new Thickness(0, 4, 0, 8);
        binding.Children.Add(_discoveryStatus);
        _candidates.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_candidates, ScrollBarVisibility.Disabled);
        _candidates.SelectionChanged += (_, _) => { if (!_rendering && !_closed) Render(); };
        System.Windows.Automation.AutomationProperties.SetName(_candidates, "候选显示器，确认选择后点击绑定所选");
        binding.Children.Add(_candidates);
        _bindingSection.Content = binding;
        settings.Children.Add(_bindingSection);

        _startup.Margin = new Thickness(0, 12, 0, 6);
        _startup.Click += StartupClicked;
        settings.Children.Add(_startup);
        settings.Children.Add(Text("高级控制", 16, FontWeights.SemiBold, new Thickness(0, 16, 0, 4)));
        settings.Children.Add(Text("状态未知但绑定有效时，可明确请求开启或关闭。结果未确认时，请勿连续重复操作。", 12));
        settings.Children.Add(commands);
        settings.Children.Add(Text("仅已验证的 G4Q + MTT VDD 配置；连接变化后需重新确认绑定。", 12));
        var diagnostics = new StackPanel();
        diagnostics.Children.Add(_source);
        diagnostics.Children.Add(_updated);
        diagnostics.Children.Add(_worker);
        diagnostics.Children.Add(_operation);
        diagnostics.Children.Add(_detailsSection);
        diagnostics.Children.Add(Text("首次无绑定时只读发现一次；仅对具体未确认请求进行最多 3 次查询和一次读回，不重发开关。系统级硬件验收仍需另行完成。", 12,
            margin: new Thickness(0, 10, 0, 0)));
        settings.Children.Add(new Expander { Header = "诊断记录", Content = diagnostics, Margin = new Thickness(0, 12, 0, 0) });
        var identitySection = new Expander { Header = "设备标识", Content = _identity, Margin = new Thickness(0, 10, 0, 0) };
        _identity.FontSize = 12;
        _identity.Margin = new Thickness(0, 6, 0, 0);
        settings.Children.Add(identitySection);
        page.Children.Add(settingsSection);

        _on.Click += async (_, _) => await ExecuteAsync(PowerIntent.On);
        _off.Click += async (_, _) => await ExecuteAsync(PowerIntent.Off);
        _toggle.Click += async (_, _) => await PrimaryActionAsync();
        _refresh.Click += async (_, _) => await RefreshHardwareAsync();
        _scan.Click += async (_, _) => await DiscoverAsync();
        _bind.Click += async (_, _) => await BindSelectedAsync();
        _unbind.Click += async (_, _) => await UnbindAsync();

        Content = new ScrollViewer
        {
            Background = Background,
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private void Render()
    {
        if (_closed)
            return;
        _rendering = true;
        try
        {
            var snapshot = _snapshot;
            var binding = snapshot?.Binding;
            var simulation = snapshot?.Simulation == true || _controller.Simulation;
            var needsConfirmation = binding?.NeedsConfirmation == true || snapshot?.ErrorCode == "topology_changed";
            var busy = IsBusy;
            var canControl = _snapshotAvailable && binding is not null && !needsConfirmation && !busy;
            _simulationBanner.Visibility = simulation ? Visibility.Visible : Visibility.Collapsed;
            _device.Text = simulation ? "当前设备：模拟 Windows 设备" : $"当前设备：{Environment.MachineName}";
            _binding.Text = binding is null ? "还未连接显示器" : $"{binding.Name}" +
                (needsConfirmation ? " · 需要重新确认" : string.Empty);
            var power = PrimaryPower;
            _power.Text = (power switch { DisplayPower.On => "●  显示器已开启", DisplayPower.Off => "◐  显示器已关闭", _ => "?  状态待确认" }) +
                (simulation ? "（模拟）" : string.Empty);
            _power.Foreground = power switch
            {
                DisplayPower.On => FrozenBrush(27, 117, 93),
                DisplayPower.Off => FrozenBrush(66, 85, 137),
                _ => FrozenBrush(145, 96, 22)
            };
            _toggle.Content = busy ? "正在处理…" : power switch
            {
                DisplayPower.On => "关闭显示器",
                DisplayPower.Off => "开启显示器",
                _ => "刷新状态"
            };
            _source.Text = "状态来源：" + (snapshot is null || binding is null ? (simulation ? "模拟（非实体硬件）" : "未知")
                : UiText.Source(snapshot.Observation, simulation));
            _updated.Text = "状态更新时间：" + (snapshot is null || binding is null ? "尚无记录" : UiText.Time(snapshot.Observation.ObservedAt));
            _worker.Text = busy ? "运行状态：操作进行中，请等待…" : simulation ? "运行状态：模拟模式，不控制实体硬件"
                : snapshot?.WorkerRunning == true ? "运行状态：控制进程已就绪" : "运行状态：控制进程未运行（按需启动）";
            _operation.Visibility = _lastOperation is null ? Visibility.Collapsed : Visibility.Visible;
            _operation.Text = _lastOperation is null ? string.Empty
                : $"最近请求：{UiText.Intent(_lastOperation.Requested)} · {UiText.Time(_lastOperation.UpdatedAt)}\n" + UiText.Operation(_lastOperation, simulation);
            _on.IsEnabled = _off.IsEnabled = canControl && _lastOperation?.Outcome != OperationOutcome.Pending;
            _toggle.IsEnabled = canControl;
            _remote.IsEnabled = !busy;
            _refresh.IsEnabled = _snapshotAvailable && binding is not null && !needsConfirmation && !busy;
            _scan.IsEnabled = !busy;
            _bind.IsEnabled = !busy && _discovery is not null && SelectedCandidate is not null;
            _unbind.IsEnabled = _snapshotAvailable && binding is not null && !busy;
            _candidates.IsEnabled = !busy;
            _startup.IsEnabled = _snapshotAvailable && !busy;
            _startup.IsChecked = snapshot?.StartupEnabled == true;
            _scan.Content = _discovery is null ? "发现显示器" : "重新扫描";
            _bind.Content = needsConfirmation ? "重新确认并绑定" : "绑定所选";
            _bindingSection.Header = binding is null ? "绑定显示器" : needsConfirmation ? "重新确认显示器绑定" : "更换或取消绑定";
            _discoveryStatus.Text = _discovery is null ? "点击“发现显示器”查找候选，不会自动绑定或开关。"
                : $"发现 {_discovery.Candidates.Count} 个候选 · {UiText.Time(_discovery.CapturedAt)} · " +
                    (SelectedCandidate is null ? "尚未选择" : $"已选择：{SelectedCandidate.Name}");
            _candidates.Visibility = _discovery is { Candidates.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            _identity.Text = snapshot is null ? "尚未加载设备标识。" : $"设备 ID：{snapshot.DeviceId}\n" +
                (binding is null ? "目标 ID：尚未绑定" : $"目标 ID：{binding.TargetId}\n目标键：{binding.TargetKey}");

            var error = _snapshotError ?? snapshot?.ErrorCode;
            var warning = _actionWarning || error is not null || needsConfirmation || HasUnconfirmedOperation;
            var message = _localBusy ? _actionMessage ?? "操作进行中，请等待…"
                : _snapshotError is not null ? "无法加载最新状态，请稍后重试。"
                : needsConfirmation ? "连接已变化，请在设置中重新确认绑定。"
                : error is not null ? UiText.BriefError(error)
                : snapshot?.Busy == true ? "控制器正在处理操作，请等待完成。"
                : HasUnconfirmedOperation ? UiText.Operation(_lastOperation!, simulation)
                : _actionMessage ?? (snapshot is null ? "正在加载本机已保存的状态，不扫描硬件…"
                    : binding is null ? "先发现并选择一台显示器，再绑定。未绑定前不会发送开关命令。"
                    : "连接变化后请重新确认绑定；未知时先刷新状态。");
            _status.Text = message;
            _statusBox.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 244, 224) : Color.FromRgb(234, 240, 246));
            _details.Text = string.Join("\n", new[] { _actionDetails, error is null ? null : $"当前错误代码：{error}\n{UiText.Error(error)}" });
            _detailsSection.Visibility = string.IsNullOrWhiteSpace(_details.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _rendering = false;
        }
    }

    private void ClearDiscovery()
    {
        _discovery = null;
        _candidates.Items.Clear();
        _candidates.SelectedIndex = -1;
    }

    private void PopulateCandidates(DiscoverySnapshot discovery)
    {
        _candidates.Items.Clear();
        foreach (var candidate in discovery.Candidates)
        {
            var label = new StackPanel { Margin = new Thickness(4) };
            label.Children.Add(Text($"{candidate.Name} · {candidate.Profile}", weight: FontWeights.SemiBold));
            label.Children.Add(Text($"目标：{candidate.TargetKey}", 12, margin: new Thickness(0, 3, 0, 0)));
            _candidates.Items.Add(new ListBoxItem { Content = label, Tag = candidate, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        _candidates.SelectedIndex = discovery.Candidates.Count == 1 ? 0 : -1;
    }

    private static TextBlock Text(string text = "", double size = 14, FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = Ink,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0, 2, 0, 2)
    };

    private static Button Button(string text) => new()
    {
        Content = text,
        MinWidth = 88,
        MinHeight = 36,
        Padding = new Thickness(14, 6, 14, 6),
        Margin = new Thickness(0, 0, 8, 6)
    };

    private static Border Card(UIElement content) => new()
    {
        Child = content,
        Background = Brushes.White,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(22, 20, 22, 20)
    };

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
