using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Fy1Switch.Host.UI;

public sealed partial class MainWindow
{
    private readonly Grid _shell = new();
    private readonly Grid _pages = new();
    private readonly Grid _pageContent = new();
    private readonly Grid _pairingOverlay = new() { Visibility = Visibility.Collapsed };
    private readonly Border _pairingBackdrop = new() { Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)) };
    private readonly ContentControl _pairingHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl _remoteSettingsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _pairingClose = Button("关闭配对");
    private readonly Button _settingsBack = Button("返回", AppIconKind.Back);
    private ScrollViewer _homePage = null!;
    private ScrollViewer _settingsPage = null!;
    private RemoteConnectionView? _remoteView;
    private IInputElement? _pairingReturnFocus;
    private Task _pairingCloseTask = Task.CompletedTask;
    private long _pairingRevision;
    private bool _pairingShowing;
    private bool _settingsShowing;

    private void InitializeNavigation(UIElement home, UIElement settings)
    {
        _homePage = Page(home);
        _settingsPage = Page(settings);
        _settingsPage.Visibility = Visibility.Collapsed;
        _pageContent.Children.Add(_homePage);
        _pageContent.Children.Add(_settingsPage);
        _pages.RowDefinitions.Add(new RowDefinition());
        _pages.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pages.Children.Add(_pageContent);
        Grid.SetRow(_statusBox, 1);
        _pages.Children.Add(_statusBox);
        _shell.Children.Add(_pages);

        _pairingBackdrop.MouseLeftButtonDown += async (sender, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, sender))
            {
                args.Handled = true;
                await ClosePairingAsync();
            }
        };
        _pairingOverlay.Children.Add(_pairingBackdrop);
        var body = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _pairingClose.MinWidth = 36;
        _pairingClose.Width = 36;
        _pairingClose.Height = 36;
        _pairingClose.Padding = new Thickness(0);
        _pairingClose.Margin = new Thickness(12, 0, 0, 0);
        _pairingClose.Content = new VectorIcon { Kind = AppIconKind.Close, Width = 16, Height = 16 };
        _pairingClose.ToolTip = "关闭配对";
        AutomationProperties.SetName(_pairingClose, "关闭配对");
        _pairingClose.Click += async (_, _) => await ClosePairingAsync();
        DockPanel.SetDock(_pairingClose, Dock.Right);
        heading.Children.Add(_pairingClose);
        heading.Children.Add(Text("连接手机", 24, FontWeights.SemiBold));
        body.Children.Add(heading);
        body.Children.Add(_pairingHost);
        var card = Card(Page(body));
        card.Margin = new Thickness(16);
        card.MaxWidth = 480;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pairingOverlay.Children.Add(card);
        KeyboardNavigation.SetTabNavigation(_pairingOverlay, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(_pairingOverlay, KeyboardNavigationMode.Cycle);
        FocusManager.SetIsFocusScope(_pairingOverlay, true);
        Panel.SetZIndex(_pairingOverlay, 10);
        _shell.Children.Add(_pairingOverlay);
        PreviewKeyDown += NavigationKeyDown;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _ = ClosePairingCoreAsync(restoreFocus: false);
            UpdateRemoteSettingsVisibility();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _ = ClosePairingCoreAsync(restoreFocus: false);
            UpdateRemoteSettingsVisibility();
        };
        Content = WindowFrame.Wrap(this, _shell, "关闭窗口后仍在托盘运行");
    }

    internal void AttachRemote(RemoteConnectionView view)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(view);
        if (_remoteView is not null)
            throw new InvalidOperationException("A remote connection view is already attached.");
        _remoteView = view;
        _pairingHost.Content = view.PairingContent;
        _remoteSettingsHost.Content = view.SettingsContent;
        view.SettingsRequested += HandleRemoteSettingsRequested;
        view.PairingRequested += HandleRemotePairingRequested;
        UpdateRemoteSettingsVisibility();
    }

    internal void DetachRemote(RemoteConnectionView view)
    {
        Dispatcher.VerifyAccess();
        if (!ReferenceEquals(_remoteView, view)) return;
        _pairingRevision++;
        _pairingShowing = false;
        _pairingOverlay.Visibility = Visibility.Collapsed;
        _pages.IsEnabled = true;
        view.SettingsRequested -= HandleRemoteSettingsRequested;
        view.PairingRequested -= HandleRemotePairingRequested;
        view.SetSettingsVisible(false);
        _pairingHost.Content = null;
        _remoteSettingsHost.Content = null;
        _remoteView = null;
    }

    internal Task ShowPairingAsync() => ShowPairingCoreAsync(allowHiddenPreview: false);

    // Called only by the --simulate --remote-preview path, which uses a loopback listener.
    internal Task ShowPairingPreviewAsync() => ShowPairingCoreAsync(allowHiddenPreview: true);

    private async Task ShowPairingCoreAsync(bool allowHiddenPreview)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => ShowPairingCoreAsync(allowHiddenPreview)).Task.Unwrap();
            return;
        }
        if (_closed || IsBusy || _pairingShowing ||
            (!allowHiddenPreview && (!IsVisible || WindowState == WindowState.Minimized))) return;
        var revision = ++_pairingRevision;
        await _pairingCloseTask;
        if (_closed || revision != _pairingRevision ||
            (!allowHiddenPreview && (!IsVisible || WindowState == WindowState.Minimized))) return;
        if (_remoteView is not { } view)
        {
            await ShowSettingsPageAsync();
            SetMessage("手机连接暂时不可用，请查看诊断记录。", warning: true);
            return;
        }
        ShowPairingLayer();
        try { await view.OpenPairingAsync(); }
        catch (Exception exception)
        {
            if (_closed || revision != _pairingRevision) return;
            await ShowSettingsPageAsync();
            SetMessage("无法生成配对码，请检查手机连接设置。", ErrorCode(exception), warning: true);
        }
    }

    private void ShowPairingLayer()
    {
        _pairingReturnFocus = Keyboard.FocusedElement;
        _pairingShowing = true;
        _pairingOverlay.Visibility = Visibility.Visible;
        _pages.IsEnabled = false;
        _remoteView?.SetSettingsVisible(false);
        _pairingClose.Focus();
    }

    internal Task ClosePairingAsync() => ClosePairingCoreAsync(restoreFocus: true);

    private Task ClosePairingCoreAsync(bool restoreFocus)
    {
        _pairingRevision++;
        if (!_pairingShowing) return _pairingCloseTask;
        _pairingShowing = false;
        _pairingOverlay.Visibility = Visibility.Collapsed;
        _pages.IsEnabled = true;
        var closing = _remoteView?.ClosePairingAsync() ?? Task.CompletedTask;
        _pairingCloseTask = CompletePairingCloseAsync(closing);
        if (restoreFocus && IsVisible && WindowState != WindowState.Minimized)
        {
            if (_pairingReturnFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
                previous.Focus();
            else if (_settingsShowing) _settingsBack.Focus();
            else _remote.Focus();
        }
        _pairingReturnFocus = null;
        return _pairingCloseTask;
    }

    private async Task CompletePairingCloseAsync(Task closing)
    {
        try { await closing; }
        catch (Exception exception)
        {
            if (!_closed)
                SetMessage("配对已结束，连接清理未完成。请稍后重试。", ErrorCode(exception), warning: true);
        }
        UpdateRemoteSettingsVisibility();
    }

    internal async Task ShowSettingsPageAsync()
    {
        if (_closed) return;
        var closing = ClosePairingCoreAsync(restoreFocus: false);
        _settingsShowing = true;
        _homePage.Visibility = Visibility.Collapsed;
        _settingsPage.Visibility = Visibility.Visible;
        _settingsBack.Focus();
        await closing;
        UpdateRemoteSettingsVisibility();
    }

    private void ShowHomePage()
    {
        if (_closed) return;
        _settingsShowing = false;
        _settingsPage.Visibility = Visibility.Collapsed;
        _homePage.Visibility = Visibility.Visible;
        UpdateRemoteSettingsVisibility();
        _settingsButton.Focus();
    }

    private void HandleRemoteSettingsRequested() => _ = ShowSettingsPageAsync();
    private void HandleRemotePairingRequested() => _ = ShowPairingAsync();

    private void UpdateRemoteSettingsVisibility() => _remoteView?.SetSettingsVisible(
        !_closed && _settingsShowing && !_pairingShowing && IsVisible && WindowState != WindowState.Minimized);

    private async void NavigationKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        if (_pairingShowing)
        {
            args.Handled = true;
            await ClosePairingAsync();
        }
        else if (_settingsShowing)
        {
            args.Handled = true;
            ShowHomePage();
        }
    }

    private static ScrollViewer Page(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
}
