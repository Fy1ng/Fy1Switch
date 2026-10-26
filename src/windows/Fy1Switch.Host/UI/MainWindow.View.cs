using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fy1Switch.Contracts;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;

namespace Fy1Switch.Host.UI;

public sealed partial class MainWindow
{
    private readonly TextBlock _device = Text();
    private readonly TextBlock _binding = Text();
    private readonly TextBlock _power = Text(size: 28);
    private readonly VectorIcon _powerIcon = new() { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0) };
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
    private Border _powerCard = null!;
    private readonly ListBox _candidates = new() { MinHeight = 54, MaxHeight = 220 };
    private readonly TextBlock _candidateEmpty = SettingsUi.Hint("点击重新扫描, 查找可绑定的显示器.");
    private readonly CheckBox _startup = new() { Content = "开机自动启动", MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _silentStartup = new() { Content = "静默启动", MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _on = Button("开启", AppIconKind.Monitor);
    private readonly Button _off = Button("关闭", AppIconKind.MonitorOff);
    private readonly Button _toggle = Button("刷新状态", AppIconKind.Refresh);
    private readonly Button _remote = Button("连接手机", AppIconKind.Phone);
    private readonly Button _settingsButton = Button("设置", AppIconKind.Settings);
    private readonly Button _bindingGuide = Button("选择显示器", AppIconKind.Monitor);
    private Border _bindingGuideCard = null!;
    private readonly Button _refresh = Button("刷新", AppIconKind.Refresh);
    private readonly Button _scan = Button("发现显示器", AppIconKind.Scan);
    private readonly Button _bind = Button("绑定所选", AppIconKind.Link);
    private readonly Button _unbind = Button("取消绑定", AppIconKind.Unlink);

    private MonitorCandidate? SelectedCandidate => (_candidates.SelectedItem as ListBoxItem)?.Tag as MonitorCandidate;

    private void InitializeView()
    {
        Title = "Fy1Switch";
        Width = 640;
        Height = 570;
        MinWidth = 480;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        DesktopTheme.Attach(this);

        var page = new StackPanel { Margin = new Thickness(24, 12, 24, 20) };
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        _remote.Margin = new Thickness(0, 0, 8, 0);
        _settingsButton.Margin = new Thickness(0);
        navigation.Children.Add(_remote);
        navigation.Children.Add(_settingsButton);
        DockPanel.SetDock(navigation, Dock.Right);
        heading.Children.Add(navigation);
        heading.Children.Add(Text("显示器开关", 25, FontWeights.SemiBold));
        _remote.Click += async (_, _) => await ShowPairingAsync();
        _settingsButton.Click += async (_, _) => await ShowSettingsPageAsync();
        page.Children.Add(heading);
        _simulationBanner.SetResourceReference(Border.BackgroundProperty, DesktopTheme.WarningBrush);
        _simulationBanner.SetResourceReference(Border.BorderBrushProperty, DesktopTheme.WarningForegroundBrush);
        _simulationBanner.BorderThickness = new Thickness(1);
        _simulationBanner.CornerRadius = new CornerRadius(6);
        _simulationBanner.Padding = new Thickness(12, 9, 12, 9);
        _simulationBanner.Margin = new Thickness(0, 0, 0, 12);
        var simulationText = Text("模拟模式：所有状态和操作结果仅用于演示。", weight: FontWeights.SemiBold);
        simulationText.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.WarningForegroundBrush);
        _simulationBanner.Child = simulationText;
        page.Children.Add(_simulationBanner);

        var state = new StackPanel();
        state.Children.Add(_device);
        _device.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        _binding.FontSize = 20;
        _binding.FontWeight = FontWeights.SemiBold;
        state.Children.Add(_binding);
        _power.FontWeight = FontWeights.SemiBold;
        var powerState = new Grid { Margin = new Thickness(0, 20, 0, 18) };
        powerState.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        powerState.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_power, 1);
        powerState.Children.Add(_powerIcon);
        powerState.Children.Add(_power);
        state.Children.Add(powerState);
        _worker.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        _operation.Margin = new Thickness(0, 7, 0, 0);
        _toggle.MinHeight = 56;
        _toggle.FontSize = 18;
        _toggle.FontWeight = FontWeights.SemiBold;
        _toggle.Margin = new Thickness(0, 0, 0, 8);
        _toggle.SetResourceReference(Control.BackgroundProperty, DesktopTheme.AccentBrush);
        _toggle.SetResourceReference(Control.ForegroundProperty, DesktopTheme.AccentForegroundBrush);
        state.Children.Add(_toggle);
        _powerCard = Card(state);
        page.Children.Add(_powerCard);
        var bindingGuide = new StackPanel();
        bindingGuide.Children.Add(Text("先选择要控制的显示器", 20, FontWeights.SemiBold));
        bindingGuide.Children.Add(Text("确认绑定后，即可开启或关闭显示器。", margin: new Thickness(0, 6, 0, 12)));
        bindingGuide.Children.Add(_bindingGuide);
        _bindingGuideCard = Card(bindingGuide);
        _bindingGuideCard.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(_bindingGuideCard);
        _bindingGuide.Click += async (_, _) =>
        {
            await ShowSettingsPageAsync();
            _scan.Focus();
        };

        var commands = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        foreach (var button in new[] { _on, _off, _refresh })
            commands.Children.Add(button);
        state.Children.Add(commands);
        _on.ToolTip = "开启已绑定的显示器。";
        _off.ToolTip = "关闭已绑定的显示器。";
        _toggle.ToolTip = "状态待确认时，点击刷新。";
        _refresh.ToolTip = "重新检查显示器的当前状态。";

        _statusBox.Padding = new Thickness(12, 9, 12, 9);
        _statusBox.CornerRadius = new CornerRadius(6);
        _statusBox.Margin = new Thickness(0, 0, 0, 12);
        var feedback = new StackPanel();
        feedback.Children.Add(_status);
        _details.Margin = new Thickness(0, 6, 0, 0);
        _details.FontSize = 12;
        _statusBox.Child = feedback;
        _statusBox.Margin = new Thickness(24, 0, 24, 16);

        var settings = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        var settingsHeading = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        SettingsUi.Button(_settingsBack);
        _settingsBack.MinWidth = 32;
        _settingsBack.Width = 32;
        _settingsBack.Padding = new Thickness(7);
        _settingsBack.BorderThickness = new Thickness(0);
        _settingsBack.SetResourceReference(Control.BackgroundProperty, DesktopTheme.SurfaceBrush);
        _settingsBack.Content = new VectorIcon { Kind = AppIconKind.Back, Width = 18, Height = 18 };
        _settingsBack.ToolTip = "返回";
        _settingsBack.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(_settingsBack, Dock.Left);
        settingsHeading.Children.Add(_settingsBack);
        settingsHeading.Children.Add(Text("设置", 25, FontWeights.SemiBold));
        settings.Children.Add(settingsHeading);
        _settingsBack.Click += (_, _) => ShowHomePage();

        var binding = new StackPanel();
        var bindingCommands = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _bind, _unbind, _scan })
        {
            SettingsUi.Button(button, primary: ReferenceEquals(button, _bind));
            bindingCommands.Children.Add(button);
        }
        _bind.Content = "绑定所选";
        _unbind.Content = "取消绑定";
        _scan.Content = "重新扫描";
        binding.Children.Add(SettingsUi.Heading("绑定显示器", AppIconKind.Monitor, bindingCommands));
        _discoveryStatus.FontSize = 12;
        _discoveryStatus.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        _discoveryStatus.Margin = new Thickness(0, 10, 0, 12);
        binding.Children.Add(_discoveryStatus);
        SettingsUi.List(_candidates);
        _candidates.SelectionChanged += (_, _) => { if (!_rendering && !_closed) Render(); };
        System.Windows.Automation.AutomationProperties.SetName(_candidates, "候选显示器，确认选择后点击绑定所选");
        binding.Children.Add(_candidates);
        binding.Children.Add(_candidateEmpty);
        settings.Children.Add(SettingsUi.Card(binding));

        var startup = new StackPanel();
        startup.Children.Add(SettingsUi.Heading("启动选项", AppIconKind.Power));
        _startup.Margin = new Thickness(0, 12, 0, 0);
        _startup.Click += StartupClicked;
        startup.Children.Add(_startup);
        _silentStartup.Click += SilentStartupClicked;
        var silent = new StackPanel();
        silent.Children.Add(_silentStartup);
        var silentHint = SettingsUi.Hint("启动后驻留托盘, 不打开主窗口");
        silentHint.Margin = new Thickness(26, 3, 0, 0);
        silent.Children.Add(silentHint);
        var silentBranch = new Border
        {
            Child = silent, BorderThickness = new Thickness(1, 0, 0, 0),
            Margin = new Thickness(26, 10, 0, 0), Padding = new Thickness(14, 0, 0, 0)
        };
        silentBranch.SetResourceReference(Border.BorderBrushProperty, DesktopTheme.BorderBrush);
        startup.Children.Add(silentBranch);
        settings.Children.Add(SettingsUi.Card(startup));
        settings.Children.Add(_remoteSettingsHost);
        var diagnostics = new StackPanel();
        diagnostics.Margin = new Thickness(28, 10, 0, 0);
        diagnostics.Children.Add(_source);
        diagnostics.Children.Add(_updated);
        diagnostics.Children.Add(_worker);
        diagnostics.Children.Add(_operation);
        diagnostics.Children.Add(_details);
        foreach (var line in new[] { _source, _updated, _worker, _operation, _details, _identity })
        {
            line.FontSize = 12;
            line.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.SecondaryBrush);
        }
        var identity = new Expander { Header = "设备标识", Content = _identity, Margin = new Thickness(0, 8, 0, 0) };
        identity.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.Expander");
        diagnostics.Children.Add(identity);
        var diagnosticSection = new Expander
        {
            Header = SettingsUi.Heading("诊断记录", AppIconKind.Save), Content = diagnostics, IsExpanded = true
        };
        diagnosticSection.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.Expander");
        settings.Children.Add(SettingsUi.Card(diagnosticSection));

        _on.Click += async (_, _) => await ExecuteAsync(PowerIntent.On);
        _off.Click += async (_, _) => await ExecuteAsync(PowerIntent.Off);
        _toggle.Click += async (_, _) => await PrimaryActionAsync();
        _refresh.Click += async (_, _) => await RefreshHardwareAsync();
        _scan.Click += async (_, _) => await DiscoverAsync();
        _bind.Click += async (_, _) => await BindSelectedAsync();
        _unbind.Click += async (_, _) => await UnbindAsync();

        InitializeNavigation(page, settings);
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
            _powerCard.Visibility = binding is null && _snapshotAvailable ? Visibility.Collapsed : Visibility.Visible;
            _bindingGuideCard.Visibility = _snapshotAvailable && (binding is null || needsConfirmation) ? Visibility.Visible : Visibility.Collapsed;
            AppIcons.SetButton(_bindingGuide, needsConfirmation ? "重新确认显示器" : "选择显示器", AppIconKind.Monitor);
            _bindingGuide.IsEnabled = !busy;
            _simulationBanner.Visibility = simulation ? Visibility.Visible : Visibility.Collapsed;
            _device.Text = simulation ? "当前设备：模拟 Windows 设备" : $"当前设备：{Environment.MachineName}";
            _binding.Text = binding is null ? "尚未绑定显示器" : $"{binding.Name}" +
                (needsConfirmation ? " · 需要重新确认" : string.Empty);
            var power = PrimaryPower;
            _on.Visibility = _off.Visibility = power == DisplayPower.Unknown ? Visibility.Visible : Visibility.Collapsed;
            _refresh.Visibility = power == DisplayPower.Unknown ? Visibility.Collapsed : Visibility.Visible;
            var refreshing = !needsConfirmation && power == DisplayPower.Unknown &&
                (snapshot is null || IsAutomaticallyRefreshing || busy);
            _power.Text = (refreshing ? "正在刷新显示器状态…" : power switch { DisplayPower.On => "显示器已开启", DisplayPower.Off => "显示器已关闭", _ => "显示器状态待确认" }) +
                (simulation ? "（模拟）" : string.Empty);
            _powerIcon.Kind = refreshing ? AppIconKind.Refresh : power switch { DisplayPower.On => AppIconKind.Monitor, DisplayPower.Off => AppIconKind.MonitorOff, _ => AppIconKind.Question };
            var stateBrush = refreshing ? DesktopTheme.ForegroundBrush : power switch
            {
                DisplayPower.On => DesktopTheme.SuccessBrush,
                DisplayPower.Off => DesktopTheme.OffBrush,
                _ => DesktopTheme.WarningForegroundBrush
            };
            _power.SetResourceReference(TextBlock.ForegroundProperty, stateBrush);
            _powerIcon.SetResourceReference(VectorIcon.ForegroundProperty, stateBrush);
            AppIcons.SetButton(_toggle, refreshing ? "正在刷新状态…" : busy ? "正在处理…" : power switch
            {
                DisplayPower.On => "关闭显示器",
                DisplayPower.Off => "开启显示器",
                _ => "刷新状态"
            }, power == DisplayPower.Unknown ? AppIconKind.Refresh : AppIconKind.Power);
            _source.Text = "状态来源：" + (snapshot is null || binding is null ? (simulation ? "模拟（非实体硬件）" : "未知")
                : UiText.Source(snapshot.Observation, simulation));
            _updated.Text = "状态更新时间：" + (snapshot is null || binding is null ? "尚无记录" : UiText.Time(snapshot.Observation.ObservedAt));
            _updated.ToolTip = snapshot is null || binding is null ? null : snapshot.Observation.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            _worker.Text = busy ? "运行状态：操作进行中，请等待…" : simulation ? "运行状态：模拟模式，不控制实体硬件"
                : snapshot?.WorkerRunning == true ? "运行状态：控制进程已就绪" : "运行状态：控制进程未运行（按需启动）";
            _operation.Visibility = _lastOperation is null ? Visibility.Collapsed : Visibility.Visible;
            _operation.Text = _lastOperation is null ? string.Empty
                : $"最近请求：{UiText.Intent(_lastOperation.Requested)} · {UiText.Time(_lastOperation.UpdatedAt)}\n" + UiText.Operation(_lastOperation, simulation);
            _operation.ToolTip = _lastOperation?.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            _on.IsEnabled = _off.IsEnabled = canControl && !HasUnconfirmedOperation;
            _toggle.IsEnabled = canControl;
            _remote.IsEnabled = !busy;
            _refresh.IsEnabled = _snapshotAvailable && binding is not null && !needsConfirmation && !busy;
            _scan.IsEnabled = !busy;
            _bind.IsEnabled = !busy && _discovery is not null && SelectedCandidate is not null;
            _unbind.IsEnabled = _snapshotAvailable && binding is not null && !busy;
            _candidates.IsEnabled = !busy;
            _startup.IsEnabled = _snapshotAvailable && !busy;
            _startup.Content = _startupSaving ? "正在保存启动设置..." : "开机自动启动";
            _startup.IsChecked = _startupSaving ? null : snapshot?.StartupEnabled == true;
            _silentStartup.IsEnabled = _startup.IsEnabled && snapshot?.StartupEnabled == true && _setSilentStartup is not null;
            try { _silentStartup.IsChecked = _snapshotAvailable ? _getSilentStartup?.Invoke() ?? true : true; }
            catch (Fy1Switch.Runtime.ControlException exception)
            {
                _silentStartup.IsEnabled = false;
                _silentStartup.ToolTip = UiText.Error(exception.Code);
            }
            _bind.Content = needsConfirmation ? "重新确认" : "绑定所选";
            _discoveryStatus.Text = $"发现 {_discovery?.Candidates.Count ?? 0} 台显示器 · 已选择: {SelectedCandidate?.Name ?? "无"}";
            _discoveryStatus.ToolTip = _discovery?.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            _candidates.Visibility = _discovery is { Candidates.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            _candidateEmpty.Visibility = _candidates.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _candidateEmpty.Text = _discovery is { Candidates.Count: 0 } ? "未发现可绑定的显示器. 请检查连接后重新扫描."
                : binding is null ? "点击重新扫描, 查找可绑定的显示器." : $"当前绑定: {binding.Name}. 重新扫描可更换显示器.";
            _identity.Text = snapshot is null ? "尚未加载设备标识。" : $"设备 ID：{snapshot.DeviceId}\n" +
                (binding is null ? "目标 ID：尚未绑定" : $"目标 ID：{binding.TargetId}\n目标键：{binding.TargetKey}");

            var error = _snapshotError ?? snapshot?.ErrorCode;
            var warning = !refreshing && (_actionWarning || error is not null || needsConfirmation || HasUnconfirmedOperation);
            var message = _localBusy ? _actionMessage ?? "操作进行中，请等待…"
                : _snapshotError is not null ? "无法加载最新状态，请稍后重试。"
                : needsConfirmation ? "连接已变化，请在设置中重新确认显示器。"
                : refreshing ? "正在自动刷新显示器状态，请稍候。"
                : error is not null ? UiText.BriefError(error)
                : snapshot?.Busy == true ? "正在处理显示器操作，请稍候。"
                : HasUnconfirmedOperation ? UiText.Operation(_lastOperation!, simulation)
                : _actionMessage ?? (snapshot is null ? "正在加载状态…"
                    : binding is null ? ""
                    : "");
            _status.Text = message;
            _statusBox.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            _statusBox.SetResourceReference(Border.BackgroundProperty, warning ? DesktopTheme.WarningBrush : DesktopTheme.SubtleBrush);
            _status.SetResourceReference(TextBlock.ForegroundProperty, warning ? DesktopTheme.WarningForegroundBrush : DesktopTheme.ForegroundBrush);
            _statusBox.SetResourceReference(Border.BorderBrushProperty, warning ? DesktopTheme.WarningForegroundBrush : DesktopTheme.BorderBrush);
            _statusBox.BorderThickness = new Thickness(1);
            _details.Text = string.Join("\n", new[] { _actionDetails, error is null ? null : $"当前错误代码：{error}\n{UiText.Error(error)}" });
            _details.Visibility = string.IsNullOrWhiteSpace(_details.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _rendering = false;
        }
        PresentationChanged?.Invoke();
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
            var bound = _snapshot?.Binding?.TargetKey == candidate.TargetKey;
            var label = SettingsUi.DeviceRow(candidate.Name, bound ? "已绑定到当前设备" : "可选择绑定", AppIconKind.Monitor, bound ? "当前绑定" : null);
            label.ToolTip = $"{candidate.Profile}\n目标：{candidate.TargetKey}";
            var item = new ListBoxItem { Content = label, Tag = candidate, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(item, candidate.Name);
            _candidates.Items.Add(item);
        }
        _candidates.SelectedIndex = discovery.Candidates.Count == 1 ? 0 : -1;
    }

    private static TextBlock Text(string text = "", double size = 14, FontWeight? weight = null, Thickness? margin = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0, 2, 0, 2)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.ForegroundBrush);
        return block;
    }

    private static Button Button(string text, AppIconKind? icon = null)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 6)
        };
        if (icon is { } kind) AppIcons.SetButton(button, text, kind);
        return button;
    }

    private static Border Card(UIElement content)
    {
        var card = new Border
        {
            Child = content,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22, 20, 22, 20)
        };
        card.SetResourceReference(Border.BackgroundProperty, DesktopTheme.SurfaceBrush);
        card.SetResourceReference(Border.BorderBrushProperty, DesktopTheme.BorderBrush);
        return card;
    }
}
