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
    private readonly TextBlock _power = Text(size: 22);
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
    private readonly Expander _bindingSection = new() { Header = "显示器绑定", IsExpanded = true };
    private readonly ListBox _candidates = new() { MinHeight = 72, MaxHeight = 174 };
    private readonly CheckBox _startup = new() { Content = "登录 Windows 时自动启动", MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _on = Button("开启");
    private readonly Button _off = Button("关闭");
    private readonly Button _toggle = Button("切换");
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

        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        page.Children.Add(Text("RemoteDisplaySwitch", 26, FontWeights.SemiBold));
        page.Children.Add(Text("显示器电源控制 · 关闭窗口后保留在托盘", margin: new Thickness(0, 3, 0, 14)));
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
        state.Children.Add(_binding);
        _power.Margin = new Thickness(0, 9, 0, 7);
        _power.FontWeight = FontWeights.SemiBold;
        state.Children.Add(_power);
        state.Children.Add(_source);
        state.Children.Add(_updated);
        _worker.Foreground = Muted;
        state.Children.Add(_worker);
        _operation.Margin = new Thickness(0, 7, 0, 0);
        state.Children.Add(_operation);
        page.Children.Add(Card(state));

        var commands = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        foreach (var button in new[] { _on, _off, _toggle, _refresh })
            commands.Children.Add(button);
        _on.ToolTip = "请求开启当前绑定的显示器，等待控制结果，不提前改变状态。";
        _off.ToolTip = "请求关闭当前绑定的显示器，等待控制结果，不提前改变状态。";
        _toggle.ToolTip = "由控制器根据实际状态决定开启或关闭；状态未知时可能无法切换。";
        _refresh.ToolTip = "手动读取硬件状态；界面自动更新只读取缓存，不扫描硬件。";
        page.Children.Add(commands);

        _statusBox.Padding = new Thickness(12, 9, 12, 9);
        _statusBox.CornerRadius = new CornerRadius(6);
        _statusBox.Margin = new Thickness(0, 0, 0, 12);
        var feedback = new StackPanel();
        feedback.Children.Add(_status);
        _details.Margin = new Thickness(0, 6, 0, 0);
        _details.FontSize = 12;
        _detailsSection.Content = _details;
        _detailsSection.Margin = new Thickness(0, 6, 0, 0);
        feedback.Children.Add(_detailsSection);
        _statusBox.Child = feedback;
        page.Children.Add(_statusBox);

        var binding = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        binding.Children.Add(Text("1. 发现或重扫 → 2. 明确选择候选 → 3. 绑定所选", weight: FontWeights.SemiBold));
        binding.Children.Add(Text("发现不会自动选择目标；重新绑定会替换当前绑定，不发送电源命令。", margin: new Thickness(0, 5, 0, 8)));
        var bindingCommands = new WrapPanel();
        foreach (var button in new[] { _scan, _bind, _unbind })
            bindingCommands.Children.Add(button);
        binding.Children.Add(bindingCommands);
        _discoveryStatus.Margin = new Thickness(0, 4, 0, 8);
        binding.Children.Add(_discoveryStatus);
        _candidates.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_candidates, ScrollBarVisibility.Disabled);
        _candidates.SelectionChanged += (_, _) => { if (!_rendering && !_closed) Render(); };
        System.Windows.Automation.AutomationProperties.SetName(_candidates, "候选显示器，必须手动选择后绑定");
        binding.Children.Add(_candidates);
        _bindingSection.Content = binding;
        page.Children.Add(Card(_bindingSection));

        _startup.Margin = new Thickness(0, 12, 0, 6);
        _startup.Click += StartupClicked;
        page.Children.Add(_startup);
        page.Children.Add(Text("适用范围：仅当前已验证的 G4Q + MTT VDD 配置。系统级验收后置；显示器连接或排列变化后，必须重新确认绑定。", 12));
        page.Children.Add(Text("不会定时轮询或自动扫描硬件。需要发现时请点击按钮，需要主动读回时请点击“刷新”。", 12,
            margin: new Thickness(0, 5, 0, 0)));
        var identitySection = new Expander { Header = "设备标识", Content = _identity, Margin = new Thickness(0, 10, 0, 0) };
        _identity.FontSize = 12;
        _identity.Margin = new Thickness(0, 6, 0, 0);
        page.Children.Add(identitySection);

        _on.Click += async (_, _) => await ExecuteAsync(PowerIntent.On);
        _off.Click += async (_, _) => await ExecuteAsync(PowerIntent.Off);
        _toggle.Click += async (_, _) => await ExecuteAsync(PowerIntent.Toggle);
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
            _binding.Text = binding is null ? "绑定：未绑定显示器" : $"绑定：{binding.Name} · {binding.Profile}" +
                (needsConfirmation ? " · 需要重新确认" : string.Empty);
            _power.Text = "开关状态：" + (snapshot is null || binding is null ? "未知" : UiText.Power(snapshot.Observation.State)) +
                (simulation ? "（模拟）" : needsConfirmation ? "（历史记录，需重新确认）" : string.Empty);
            _source.Text = "状态来源：" + (snapshot is null || binding is null ? (simulation ? "模拟（非实体硬件）" : "未知")
                : UiText.Source(snapshot.Observation, simulation));
            _updated.Text = "状态更新时间：" + (snapshot is null || binding is null ? "尚无记录" : UiText.Time(snapshot.Observation.ObservedAt));
            _worker.Text = busy ? "运行状态：操作进行中，请等待…" : simulation ? "运行状态：模拟模式，不控制实体硬件"
                : snapshot?.WorkerRunning == true ? "运行状态：控制进程已就绪" : "运行状态：控制进程未运行（按需启动）";
            _operation.Visibility = _lastOperation is null ? Visibility.Collapsed : Visibility.Visible;
            _operation.Text = _lastOperation is null ? string.Empty
                : $"最近请求：{UiText.Intent(_lastOperation.Requested)} · {UiText.Time(_lastOperation.UpdatedAt)}\n" + UiText.Operation(_lastOperation, simulation);
            _on.IsEnabled = _off.IsEnabled = _toggle.IsEnabled = canControl;
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
            _discoveryStatus.Text = _discovery is null ? "尚无候选列表。点击“发现显示器”才会扫描，不会默认操作硬件。"
                : $"发现 {_discovery.Candidates.Count} 个候选 · {UiText.Time(_discovery.CapturedAt)} · " +
                    (SelectedCandidate is null ? "尚未选择" : $"已选择：{SelectedCandidate.Name}");
            _candidates.Visibility = _discovery is { Candidates.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            _identity.Text = snapshot is null ? "尚未加载设备标识。" : $"设备 ID：{snapshot.DeviceId}\n" +
                (binding is null ? "目标 ID：尚未绑定" : $"目标 ID：{binding.TargetId}\n目标键：{binding.TargetKey}");

            var error = _snapshotError ?? snapshot?.ErrorCode;
            var warning = _actionWarning || error is not null || needsConfirmation;
            var message = _localBusy ? _actionMessage ?? "操作进行中，请等待…"
                : _snapshotError is not null ? "无法加载最新状态；显示的记录可能已过期。" + UiText.Error(_snapshotError)
                : needsConfirmation ? UiText.Error("topology_changed")
                : error is not null ? UiText.Error(error)
                : snapshot?.Busy == true ? "控制器正在处理操作，请等待完成。"
                : _actionMessage ?? (snapshot is null ? "正在加载本机已保存的状态，不扫描硬件…"
                    : binding is null ? "先发现并选择一台显示器，再绑定。未绑定前不会发送开关命令。"
                    : "状态来自最近一次记录。需要当前硬件状态时，请点击“刷新”。");
            _status.Text = message;
            _statusBox.Background = new SolidColorBrush(warning ? Color.FromRgb(255, 244, 224) : Color.FromRgb(234, 240, 246));
            _details.Text = string.Join("\n", new[] { _actionDetails, error is null ? null : $"当前错误代码：{error}" });
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
        _candidates.SelectedIndex = -1;
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
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(14, 12, 14, 12)
    };

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
