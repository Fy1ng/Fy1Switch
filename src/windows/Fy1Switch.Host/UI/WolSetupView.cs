using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Fy1Switch.Contracts;
using Fy1Switch.Remote;

namespace Fy1Switch.Host.UI;

internal sealed class WolSetupView
{
    private readonly Guid _deviceId;
    private readonly string _deviceName;
    private readonly WolSetupSettingsStore _store;
    private readonly Func<IReadOnlyList<WolAdapterChoice>> _getAdapters;
    private readonly Action<string> _copy;
    private readonly ComboBox _adapters = new() { DisplayMemberPath = "Label", MinHeight = 34 };
    private readonly TextBlock _status = SettingsUi.Hint("");
    private readonly TextBlock _saved = SettingsUi.Hint("");
    private readonly Button _save = new() { Content = "确认并保存网卡" };
    private readonly Button _copyConfiguration = new() { Content = "复制设备配置" };
    private readonly Button _clear = new() { Content = "清除选择" };
    private WolSetupSettings _settings = new();
    private bool _loaded;

    internal WolSetupView(Guid deviceId, string deviceName, WolSetupSettingsStore store,
        Func<IReadOnlyList<WolAdapterChoice>>? getAdapters = null, Action<string>? copy = null)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _store = store;
        _getAdapters = getAdapters ?? WolAdapterChoices.Get;
        _copy = copy ?? Clipboard.SetText;
        var body = new StackPanel();
        body.Children.Add(SettingsUi.Hint("选择用于远程开机的有线网卡. 此处只保存配置参考, 不修改系统设置或向电脑发送唤醒包."));
        body.Children.Add(SettingsUi.Hint($"电脑: {_deviceName}\n设备 ID: {_deviceId}"));
        var field = SettingsUi.Field("候选有线网卡", _adapters);
        field.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(field);
        body.Children.Add(_saved);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var refresh = new Button { Content = "刷新网卡" };
        foreach (var button in new[] { _save, refresh, _copyConfiguration, _clear })
        {
            SettingsUi.Button(button, primary: button == _save);
            button.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(button);
        }
        body.Children.Add(actions);
        body.Children.Add(_status);
        body.Children.Add(SettingsUi.Separator());
        body.Children.Add(SettingsUi.Label("配置检查", bold: true));
        body.Children.Add(SettingsUi.Hint("1. BIOS/UEFI: 按主板说明启用 Wake on LAN / PCIe 唤醒. ErP 或深度节能可能切断关机网卡供电."));
        body.Children.Add(SettingsUi.Hint("2. Windows/Realtek: 在设备管理器检查 Wake on Magic Packet, Shutdown Wake-On-Lan 及允许此设备唤醒计算机等选项. 具体名称和支持状态取决于驱动."));
        body.Children.Add(SettingsUi.Hint("3. 快速启动: 若关机后无法唤醒, 按 Windows 电源选项说明手动检查快速启动, 并分别测试睡眠和完全关机. 本应用不自动更改 BIOS, 电源策略或网卡属性."));
        body.Children.Add(SettingsUi.Hint("4. 在 VPS WOL Gateway 和 OpenWrt 上配置此设备 ID/MAC, 再在 Android 中独立授权. 复制的内容不包含 SSH 配置, 也不会自动更新服务器."));
        body.Children.Add(SettingsUi.Hint("5. 唤醒包发送成功不代表电脑已经启动. 当前 Windows Host 在用户登录后启动并驻留托盘, 之后才恢复显示器控制. 可在上方启用现有自动启动选项."));
        body.Children.Add(SettingsUi.Hint("候选筛选不能验证硬件和固件的 WOL 能力. USB 网卡, 断电和部分睡眠模式可能不支持唤醒, 请按设备文档实测."));
        var section = new Expander
        {
            Header = SettingsUi.Heading("远程开机辅助配置", AppIconKind.Power), Content = body
        };
        section.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.Expander");
        Content = SettingsUi.Card(section);
        section.Expanded += (_, _) => RefreshCandidates();
        refresh.Click += (_, _) => RefreshCandidates();
        _adapters.SelectionChanged += (_, _) => _save.IsEnabled = _loaded && _adapters.SelectedItem is WolAdapterChoice;
        _save.Click += (_, _) => Run(SaveSelection);
        _clear.Click += (_, _) => Run(() =>
        {
            _store.Clear();
            _settings = new();
            RefreshCandidates();
            _status.Text = "已清除本机网卡选择. Gateway 上的设备配置和手机授权需在 Gateway 单独管理.";
        });
        _copyConfiguration.Click += (_, _) => Run(CopyConfiguration);
        _save.IsEnabled = _copyConfiguration.IsEnabled = _clear.IsEnabled = false;
        Run(() =>
        {
            _settings = _store.Load();
            _loaded = _clear.IsEnabled = true;
            _saved.Text = _settings.AdapterId is null ? "尚未配置 WOL 网卡. 原有显示器控制不受影响."
                : $"已保存: {_settings.AdapterName} | {_settings.MacAddress}\n展开此区块后检查当前网卡.";
        });
    }

    internal FrameworkElement Content { get; }

    internal void RefreshCandidates() => Run(() =>
    {
        // Reload saved state, but never replace a missing adapter or changed MAC with another candidate.
        _settings = _store.Load();
        _loaded = _clear.IsEnabled = true;
        var current = WolAdapterChoices.Eligible(_getAdapters());
        _adapters.ItemsSource = current;
        _adapters.SelectedItem = _settings.Status(current) == WolSelectionStatus.Confirmed
            ? current.FirstOrDefault(item => item.AdapterId == _settings.AdapterId) : null;
        RenderSaved(current);
        _status.Text = current.Count == 0 ? "未发现候选有线网卡. 请检查连接或驱动, 然后刷新. 不会自动修改网卡." : "选择网卡后点击确认并保存. 未连接的有线网卡仍可能支持 WOL.";
        _save.IsEnabled = _adapters.SelectedItem is WolAdapterChoice;
    });

    private void SaveSelection()
    {
        if (_adapters.SelectedItem is not WolAdapterChoice selected) return;
        // Re-enumerate at the explicit confirmation boundary, not only when the settings page opened.
        var current = _getAdapters();
        _settings = _store.SaveSelection(selected, current);
        RenderSaved(current);
        _status.Text = "已保存所选网卡. 如服务器已有不同 MAC, 请手动更新 Gateway/OpenWrt 配置.";
    }

    private void CopyConfiguration()
    {
        var current = _getAdapters();
        if (_settings.Status(current) != WolSelectionStatus.Confirmed)
        {
            RenderSaved(current);
            _status.Text = "网卡已消失或 MAC 已变化. 请刷新后重新选择并确认保存, 再复制配置.";
            return;
        }
        _copy(WireJson.Serialize(new { deviceId = _deviceId, name = _deviceName, macAddress = _settings.MacAddress }));
        _status.Text = "已复制当前电脑的设备 ID, 名称和 MAC. 请在 Gateway/OpenWrt 配置中核对并补齐服务端参数.";
    }

    private void RenderSaved(IReadOnlyList<WolAdapterChoice> current)
    {
        var status = _settings.Status(current);
        _copyConfiguration.IsEnabled = status == WolSelectionStatus.Confirmed;
        _saved.Text = status switch
        {
            WolSelectionStatus.NotConfigured => "尚未配置 WOL 网卡. 原有显示器控制不受影响.",
            WolSelectionStatus.AdapterMissing => $"已保存: {_settings.AdapterName} | {_settings.MacAddress}\n未发现已保存的网卡. 请刷新列表并明确重新选择, 不会自动替换.",
            WolSelectionStatus.MacChanged => $"已保存: {_settings.AdapterName} | {_settings.MacAddress}\n此网卡的 MAC 已变化. 请明确重新选择并确认保存.",
            _ => $"已保存: {_settings.AdapterName} | {_settings.MacAddress}"
        };
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            _loaded = _save.IsEnabled = _copyConfiguration.IsEnabled = _clear.IsEnabled = false;
            _status.Text = exception.Message switch
            {
                "wol_adapter_missing" => "所选网卡已消失. 请刷新列表并重新选择, 原选择未修改.",
                "wol_mac_changed" => "所选网卡的 MAC 已变化. 请刷新列表并重新确认, 原选择未修改.",
                "unsupported_wol_setup_version" => "WOL 配置来自较新版本. 请升级程序, 不要覆盖已有配置.",
                _ => "无法读取网卡, 保存配置或访问剪贴板. 请检查本机权限后重试. 原有显示器控制继续运行."
            };
        }
    }
}
