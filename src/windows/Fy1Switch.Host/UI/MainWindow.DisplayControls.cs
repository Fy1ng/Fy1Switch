using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Fy1Switch.Contracts;

namespace Fy1Switch.Host.UI;

public sealed partial class MainWindow
{
    private sealed class ControlView(DisplayControl control, StackPanel row, TextBlock value)
    {
        internal DisplayControl Control { get; } = control;
        internal StackPanel Row { get; } = row;
        internal TextBlock Value { get; } = value;
        internal Slider? Slider { get; set; }
        internal ComboBox? Choices { get; set; }
        internal bool Editing { get; set; }
        internal int? Pending { get; set; }
    }

    private sealed record ControlChoice(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Dictionary<DisplayControl, ControlView> _controlViews = new();
    private readonly Dictionary<DisplayControl, CheckBox> _controlPreferences = new();
    private readonly CheckBox _showPowerPreference = new() { Content = "显示电源状态控制" };
    private readonly CheckBox _hideMonitorPreference = new() { Content = "隐藏监视器" };
    private readonly TextBlock _controlsTitle = Text("显示器功能", 18, FontWeights.SemiBold);
    private readonly TextBlock _controlsStatus = SettingsUi.Hint("绑定显示器后读取可用功能.");
    private readonly TextBlock _monitorSettingsTitle = Text("尚未绑定显示器", 14, FontWeights.SemiBold);
    private readonly TextBlock _monitorSettingsIdentity = SettingsUi.Hint("");
    private readonly TextBlock _preferencesStatus = SettingsUi.Hint("");
    private readonly TextBox _vcpText = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 130, MaxHeight = 330, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12
    };
    private readonly Button _readControls = Button("刷新功能", AppIconKind.Refresh);
    private Border _controlsCard = null!;
    private Border _hiddenMonitorCard = null!;
    private MonitorPreferences _monitorPreferences = new();
    private DisplayControlsSnapshot? _displayControls;
    private string? _preferencesTargetKey;
    private string? _controlsReadError;
    private bool _displayPreferencesLoaded;
    private bool _preferencesReading;
    private bool _displayControlsReading;
    private bool _displayControlsReadAttempted;
    private bool _displayControlsReadQueued;
    private long _displayControlsGeneration;

    private void InitializeDisplayControls(StackPanel home, StackPanel settings)
    {
        var controls = new StackPanel();
        controls.Children.Add(_controlsTitle);
        controls.Children.Add(_controlsStatus);
        foreach (var control in Enum.GetValues<DisplayControl>())
        {
            var row = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            var heading = new DockPanel();
            var value = SettingsUi.Label("尚未读取", 12, secondary: true);
            value.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(value, Dock.Right);
            heading.Children.Add(value);
            heading.Children.Add(SettingsUi.Label(ControlName(control), bold: true));
            row.Children.Add(heading);
            var view = new ControlView(control, row, value);
            if (control is DisplayControl.Brightness or DisplayControl.Contrast or DisplayControl.Volume)
            {
                var slider = new Slider
                {
                    Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true,
                    SmallChange = 1, LargeChange = 10, MinHeight = 30, Margin = new Thickness(0, 4, 0, 0)
                };
                slider.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Display.Slider");
                slider.SetResourceReference(Control.ForegroundProperty, DesktopTheme.ForegroundBrush);
                AutomationProperties.SetName(slider, ControlName(control));
                slider.PreviewMouseLeftButtonDown += (_, _) => view.Editing = true;
                slider.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(async (_, args) =>
                {
                    if (args.ChangedButton != MouseButton.Left) return;
                    view.Editing = false;
                    await WriteDisplayControlAsync(view, (int)Math.Round(slider.Value));
                }), handledEventsToo: true);
                slider.PreviewKeyDown += (_, args) => { if (IsSliderKey(args.Key)) view.Editing = true; };
                slider.KeyUp += async (_, args) =>
                {
                    if (!IsSliderKey(args.Key)) return;
                    view.Editing = false;
                    await WriteDisplayControlAsync(view, (int)Math.Round(slider.Value));
                };
                slider.LostKeyboardFocus += (_, _) => { view.Editing = false; Render(); };
                slider.ValueChanged += (_, _) =>
                {
                    if (!_rendering && view.Editing) value.Text = $"{(int)Math.Round(slider.Value)}%";
                };
                view.Slider = slider;
                row.Children.Add(slider);
            }
            else
            {
                var choices = new ComboBox { MinHeight = 34, Margin = new Thickness(0, 8, 0, 0) };
                AutomationProperties.SetName(choices, ControlName(control));
                choices.SelectionChanged += async (_, _) =>
                {
                    if (!_rendering && choices.SelectedItem is ControlChoice choice)
                        await WriteDisplayControlAsync(view, choice.Value);
                };
                view.Choices = choices;
                row.Children.Add(choices);
                if (control == DisplayControl.Rotation)
                    row.Children.Add(SettingsUi.Hint("复制模式共享显示源, 旋转可能同时影响实体屏和虚拟屏. 配置变化后需重新确认显示器."));
            }
            _controlViews.Add(control, view);
            controls.Children.Add(row);
        }
        _controlsCard = Card(controls);
        _controlsCard.Margin = new Thickness(0, 12, 0, 0);
        home.Children.Add(_controlsCard);
        var hidden = new StackPanel();
        hidden.Children.Add(Text("显示器控件已隐藏", 18, FontWeights.SemiBold));
        hidden.Children.Add(SettingsUi.Hint("可在设置的监视器分组中恢复显示. 手机和托盘控制仍然可用."));
        var restore = Button("打开监视器设置", AppIconKind.Settings);
        restore.Margin = new Thickness(0, 12, 0, 0);
        restore.Click += async (_, _) => await ShowSettingsPageAsync();
        hidden.Children.Add(restore);
        _hiddenMonitorCard = Card(hidden);
        _hiddenMonitorCard.Margin = new Thickness(0, 12, 0, 0);
        home.Children.Add(_hiddenMonitorCard);

        var monitor = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        SettingsUi.Button(_readControls);
        DockPanel.SetDock(_readControls, Dock.Right);
        header.Children.Add(_readControls);
        header.Children.Add(_monitorSettingsTitle);
        monitor.Children.Add(header);
        monitor.Children.Add(_monitorSettingsIdentity);
        foreach (var control in Enum.GetValues<DisplayControl>())
        {
            var preference = new CheckBox
            {
                Content = control switch
                {
                    DisplayControl.Brightness => "显示亮度滑块",
                    DisplayControl.Contrast => "显示对比度滑块",
                    DisplayControl.Volume => "显示音量滑块",
                    DisplayControl.InputSource => "显示输入源控制",
                    DisplayControl.Rotation => "显示旋转控制",
                    _ => "显示色温切换器"
                }, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0)
            };
            preference.Click += async (_, _) =>
            {
                var show = preference.IsChecked == true;
                await SaveMonitorPreferenceAsync(current => WithControlPreference(current, control, show));
            };
            _controlPreferences.Add(control, preference);
            monitor.Children.Add(preference);
        }
        foreach (var preference in new[] { _showPowerPreference, _hideMonitorPreference })
        {
            preference.MinHeight = 34;
            preference.VerticalContentAlignment = VerticalAlignment.Center;
            preference.Margin = new Thickness(0, 3, 0, 0);
            monitor.Children.Add(preference);
        }
        _showPowerPreference.Click += async (_, _) =>
        {
            var show = _showPowerPreference.IsChecked == true;
            await SaveMonitorPreferenceAsync(current => current with { ShowPower = show });
        };
        _hideMonitorPreference.Click += async (_, _) =>
        {
            var hidden = _hideMonitorPreference.IsChecked == true;
            await SaveMonitorPreferenceAsync(current => current with { Hidden = hidden });
        };
        monitor.Children.Add(_preferencesStatus);
        monitor.Children.Add(SettingsUi.Separator());
        _vcpText.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.TextBox");
        AutomationProperties.SetName(_vcpText, "检测到的 VCP 代码和支持值, 只读");
        var vcp = new Expander { Header = "VCP 功能", Content = _vcpText };
        vcp.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.Expander");
        monitor.Children.Add(vcp);
        var section = new Expander
        {
            Header = SettingsUi.Heading("监视器", AppIconKind.Monitor), Content = monitor, IsExpanded = true
        };
        section.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.Expander");
        settings.Children.Add(SettingsUi.Card(section));
        _readControls.Click += async (_, _) => await RunActionAsync("正在读取显示器功能...", async cancellationToken =>
        {
            if (_snapshot?.Binding is { } binding)
                await ReadMonitorPreferencesAsync(binding.TargetKey, cancellationToken);
            await ReadDisplayControlsCoreAsync(cancellationToken);
            SetMessage("已刷新显示器功能.");
        });
    }

    private void UpdateDisplayControlsBinding(bool bindingChanged, bool invalidated)
    {
        if (!bindingChanged && !invalidated) return;
        _displayControlsGeneration++;
        _displayControls = null;
        _controlsReadError = null;
        _displayControlsReadAttempted = invalidated;
        if (bindingChanged)
        {
            _monitorPreferences = new();
            _preferencesTargetKey = null;
            _displayPreferencesLoaded = false;
        }
    }

    private void RenderDisplayControls()
    {
        var binding = _snapshot?.Binding;
        var valid = _snapshotAvailable && binding is { NeedsConfirmation: false } && _snapshot?.ErrorCode != "topology_changed";
        var hidden = binding is not null && _displayPreferencesLoaded && _monitorPreferences.Hidden;
        var showPower = !_displayPreferencesLoaded || _monitorPreferences.ShowPower;
        if (binding is not null && (hidden || !showPower)) _powerCard.Visibility = Visibility.Collapsed;
        var hasVisibleControl = _controlViews.Keys.Any(control => ShowControl(_monitorPreferences, control));
        _controlsCard.Visibility = binding is not null && !hidden && hasVisibleControl ? Visibility.Visible : Visibility.Collapsed;
        _hiddenMonitorCard.Visibility = binding is not null && (hidden || !showPower && !hasVisibleControl) ? Visibility.Visible : Visibility.Collapsed;
        _controlsTitle.Text = binding is null ? "显示器功能" : binding.Name + " - 显示器功能";
        _monitorSettingsTitle.Text = _displayControls?.MonitorName ?? binding?.Name ?? "尚未绑定显示器";
        _monitorSettingsIdentity.Text = _displayControls is { } identity ? identity.MonitorId + "\nDDC/CI"
            : binding is null ? "绑定显示器后可设置控制项." : "当前已绑定目标, 功能按实际检测结果启用.";
        _controlsStatus.Text = !valid ? "请先确认显示器绑定."
            : _displayControlsReading ? "正在读取可用功能和当前值..."
            : _controlsReadError is not null ? "功能读取失败, 请在设置中点击刷新功能. " + _controlsReadError
            : _displayControls is null ? "尚未读取显示器功能."
            : "最近读取: " + _displayControls.ObservedAt.ToLocalTime().ToString("HH:mm:ss") + (_controller.Simulation ? " (模拟)" : "");
        _readControls.IsEnabled = valid && !IsBusy;
        foreach (var view in _controlViews.Values)
        {
            var state = _displayControls?.Controls.FirstOrDefault(item => item.Control == view.Control);
            var supported = state?.Supported == true;
            view.Row.Visibility = ShowControl(_monitorPreferences, view.Control) ? Visibility.Visible : Visibility.Collapsed;
            var available = valid && !IsBusy && supported;
            var value = view.Pending ?? state?.Value;
            if (view.Slider is { } slider)
            {
                slider.IsEnabled = available && state?.Value is not null;
                if (!view.Editing) slider.Value = value ?? 0;
                if (!view.Editing) view.Value.Text = !supported ? (_displayControls is null ? "尚未读取" : "不支持")
                    : value is null ? "读回未知" : value + "%";
            }
            else if (view.Choices is { } choices)
            {
                var values = state?.SupportedValues ?? Array.Empty<int>();
                if (!choices.Items.Cast<ControlChoice>().Select(choice => choice.Value).SequenceEqual(values))
                {
                    choices.Items.Clear();
                    foreach (var choice in values) choices.Items.Add(new ControlChoice(choice, ChoiceName(view.Control, choice)));
                }
                choices.SelectedItem = choices.Items.Cast<ControlChoice>().FirstOrDefault(choice => choice.Value == value);
                choices.IsEnabled = available && values.Count > 0;
                view.Value.Text = !supported ? (_displayControls is null ? "尚未读取" : "不支持")
                    : value is null ? "读回未知" : ChoiceName(view.Control, value.Value);
            }
            var preference = _controlPreferences[view.Control];
            preference.IsChecked = ShowControl(_monitorPreferences, view.Control);
            preference.IsEnabled = _displayPreferencesLoaded && !IsBusy && supported;
            preference.ToolTip = supported ? null : _displayControls is null ? "读取显示器功能后可设置." : "当前显示器未报告此功能支持.";
        }
        _showPowerPreference.IsChecked = _monitorPreferences.ShowPower;
        _hideMonitorPreference.IsChecked = _monitorPreferences.Hidden;
        _showPowerPreference.IsEnabled = _hideMonitorPreference.IsEnabled = binding is not null && _displayPreferencesLoaded && !IsBusy;
        _preferencesStatus.Text = _preferencesReading ? "正在加载显示选项..."
            : binding is null ? "" : !_displayPreferencesLoaded ? "显示选项尚未加载, 可点击刷新功能重试."
            : "显示选项自动保存. 隐藏监视器只影响本地主页面.";
        _vcpText.Text = BuildVcpReport();
        QueueDisplayControlsRead();
    }

    private void QueueDisplayControlsRead()
    {
        if (_closed || !_snapshotAvailable || _snapshot?.Binding is not { } binding) return;
        if (_preferencesTargetKey != binding.TargetKey && !_preferencesReading)
        {
            _preferencesReading = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                await LoadMonitorPreferencesInBackgroundAsync(binding.TargetKey)));
        }
        if (_displayControlsReadQueued || _displayControlsReading || _displayControlsReadAttempted || IsBusy ||
            binding.NeedsConfirmation || _snapshot.ErrorCode == "topology_changed") return;
        _displayControlsReadQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            _displayControlsReadQueued = false;
            if (_closed || IsBusy || _displayControlsReadAttempted) return;
            try { await ReadDisplayControlsCoreAsync(_lifetimeToken); }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception exception)
            {
                if (!_closed) { _controlsReadError = ErrorCode(exception); Render(); }
            }
        }));
    }

    private async Task LoadMonitorPreferencesInBackgroundAsync(string targetKey)
    {
        try { await ReadMonitorPreferencesAsync(targetKey, _lifetimeToken); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception) { /* Keep the explicit refresh path available. */ }
        finally { _preferencesReading = false; if (!_closed) Render(); }
    }

    private async Task ReadMonitorPreferencesAsync(string targetKey, CancellationToken cancellationToken)
    {
        _preferencesTargetKey = targetKey;
        var preferences = await _controller.GetMonitorPreferencesAsync(targetKey, cancellationToken);
        if (_closed || _snapshot?.Binding?.TargetKey != targetKey) return;
        _monitorPreferences = preferences;
        _displayPreferencesLoaded = true;
    }

    private async Task ReadDisplayControlsCoreAsync(CancellationToken cancellationToken)
    {
        if (_closed || _displayControlsReading || _snapshot?.Binding is not { NeedsConfirmation: false } binding ||
            _snapshot.ErrorCode == "topology_changed") return;
        var generation = _displayControlsGeneration;
        _displayControlsReadAttempted = true;
        _displayControlsReading = true;
        _controlsReadError = null;
        Render();
        try
        {
            var controls = await _controller.GetDisplayControlsAsync(binding.TargetId, cancellationToken);
            if (_closed || generation != _displayControlsGeneration || _snapshot?.Binding?.TargetId != binding.TargetId) return;
            if (controls.TargetKey != binding.TargetKey) throw new InvalidOperationException("Unexpected display control target.");
            _displayControls = controls;
        }
        catch (Exception exception)
        {
            if (generation == _displayControlsGeneration)
            {
                _displayControls = null;
                _controlsReadError = ErrorCode(exception);
            }
            throw;
        }
        finally { _displayControlsReading = false; if (!_closed) Render(); }
    }

    private Task SaveMonitorPreferenceAsync(Func<MonitorPreferences, MonitorPreferences> update)
    {
        if (_rendering || !_displayPreferencesLoaded || _snapshot?.Binding is not { } binding) return Task.CompletedTask;
        return RunActionAsync("正在保存显示选项...", async cancellationToken =>
        {
            var preferences = await _controller.SetMonitorPreferencesAsync(binding.TargetKey, update(_monitorPreferences), cancellationToken);
            if (_closed || _snapshot?.Binding?.TargetKey != binding.TargetKey) return;
            _monitorPreferences = preferences;
            SetMessage("已保存显示选项.");
        });
    }

    private Task WriteDisplayControlAsync(ControlView view, int value)
    {
        if (_rendering || IsBusy || _snapshot?.Binding is not { NeedsConfirmation: false } binding ||
            _snapshot.ErrorCode == "topology_changed" ||
            _displayControls?.Controls.FirstOrDefault(item => item.Control == view.Control) is not { Supported: true } state ||
            state.Value == value) return Task.CompletedTask;
        var generation = _displayControlsGeneration;
        view.Pending = value;
        return RunActionAsync("正在设置" + ControlName(view.Control) + "...", async cancellationToken =>
        {
            DisplayControlWriteResult result;
            try
            {
                result = await _controller.SetDisplayControlAsync(binding.TargetId, view.Control, value, cancellationToken);
            }
            catch
            {
                SetDisplayControlValue(view.Control, null, binding.TargetId, generation);
                throw;
            }
            finally { view.Pending = null; }
            if (_closed || generation != _displayControlsGeneration || _snapshot?.Binding?.TargetId != binding.TargetId) return;
            SetDisplayControlValue(view.Control, result.Value, binding.TargetId, generation);
            var name = ControlName(view.Control);
            SetMessage(result.Confirmed ? name + "已设置并读回确认."
                : result.WriteAccepted == true ? name + "设置已接受, 读回尚未确认."
                : result.WriteAccepted == false ? name + "设置失败."
                : name + "设置结果未知, 请刷新功能确认.", result.ErrorCode, warning: !result.Confirmed);
        });
    }

    private void SetDisplayControlValue(DisplayControl control, int? value, Guid targetId, long generation)
    {
        if (_displayControls is null || _snapshot?.Binding?.TargetId != targetId || generation != _displayControlsGeneration) return;
        _displayControls = _displayControls with
        {
            Controls = _displayControls.Controls.Select(item => item.Control == control ? item with { Value = value } : item).ToArray(),
            ObservedAt = DateTimeOffset.UtcNow
        };
    }

    private string BuildVcpReport()
    {
        if (_displayControls is not { } controls) return _controlsReadError is null ? "尚未读取 VCP 功能." : "读取失败: " + _controlsReadError;
        var report = new StringBuilder();
        report.AppendLine("Monitor: " + controls.MonitorName).AppendLine("ID: " + controls.MonitorId).AppendLine();
        foreach (var code in controls.VcpCodes.OrderBy(code => code.Code))
        {
            report.Append($"0x{code.Code:X2}  {code.Name}");
            if (code.SupportedValues.Count > 0)
                report.Append("  Values: " + string.Join(", ", code.SupportedValues.Select(value => $"0x{value:X2}")));
            else report.Append("  Continuous range / no discrete values");
            report.AppendLine();
        }
        report.AppendLine().Append("Total: " + controls.VcpCodes.Count + " VCP codes");
        return report.ToString();
    }

    private static bool IsSliderKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private static string ControlName(DisplayControl control) => control switch
    {
        DisplayControl.Brightness => "亮度", DisplayControl.Contrast => "对比度", DisplayControl.Volume => "音量",
        DisplayControl.InputSource => "输入源", DisplayControl.ColorTemperature => "色温", _ => "旋转"
    };
    private static bool ShowControl(MonitorPreferences preferences, DisplayControl control) => control switch
    {
        DisplayControl.Brightness => preferences.ShowBrightness, DisplayControl.Contrast => preferences.ShowContrast,
        DisplayControl.Volume => preferences.ShowVolume, DisplayControl.InputSource => preferences.ShowInputSource,
        DisplayControl.ColorTemperature => preferences.ShowColorTemperature, _ => preferences.ShowRotation
    };
    private static MonitorPreferences WithControlPreference(MonitorPreferences preferences, DisplayControl control, bool value) => control switch
    {
        DisplayControl.Brightness => preferences with { ShowBrightness = value }, DisplayControl.Contrast => preferences with { ShowContrast = value },
        DisplayControl.Volume => preferences with { ShowVolume = value }, DisplayControl.InputSource => preferences with { ShowInputSource = value },
        DisplayControl.ColorTemperature => preferences with { ShowColorTemperature = value }, _ => preferences with { ShowRotation = value }
    };
    private static string ChoiceName(DisplayControl control, int value) => control switch
    {
        DisplayControl.Rotation => value switch { 0 => "0 deg (横向)", 1 => "90 deg (纵向)", 2 => "180 deg (横向翻转)", 3 => "270 deg (纵向翻转)", _ => $"0x{value:X2}" },
        DisplayControl.ColorTemperature => value switch { 1 => "sRGB", 2 => "Display Native", 4 => "5000K", 5 => "6500K", 6 => "7500K", 8 => "9300K", 11 => "User 1", _ => $"0x{value:X2}" },
        DisplayControl.InputSource => value switch { 1 => "VGA-1", 3 => "DVI-1", 4 => "DVI-2", 15 => "DisplayPort-1", 16 => "DisplayPort-2", 17 => "HDMI-1", 18 => "HDMI-2", _ => $"0x{value:X2}" },
        _ => value.ToString()
    };
}
