using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Runtime;

namespace RemoteDisplaySwitch.Host.UI;

public sealed partial class MainWindow : Window
{
    private readonly IDesktopController _controller;
    private readonly Func<bool>? _requestStartupReplacement;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private HostSnapshot? _snapshot;
    private DiscoverySnapshot? _discovery;
    private OperationRecord? _lastOperation;
    private Task? _viewRefreshTask;
    private bool _snapshotRequested;
    private bool _snapshotAvailable;
    private bool _localBusy;
    private bool _rendering;
    private bool _allowClose;
    private volatile bool _closed;
    private int _refreshQueued;
    private long _snapshotVersion;
    private string? _actionMessage;
    private string? _actionDetails;
    private string? _snapshotError;
    private bool _actionWarning;
    private bool _initialDiscoveryChecked;
    private bool _startupSaving;
    private bool _hasHiddenToTray;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _relativeTimeTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public MainWindow(IDesktopController controller) : this(controller, null) { }

    internal MainWindow(IDesktopController controller, Func<bool>? requestStartupReplacement)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _requestStartupReplacement = requestStartupReplacement;
        _lifetimeToken = _lifetime.Token;
        InitializeView();
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            if (!_actionWarning && !_localBusy)
            {
                _actionMessage = null;
                Render();
            }
        };
        _relativeTimeTimer.Tick += (_, _) => { if (IsVisible) Render(); };
        _relativeTimeTimer.Start();
        _controller.Changed += ControllerChanged;
        Loaded += WindowLoaded;
        Render();
    }

    public event Action? RemoteSettingsRequested;
    internal event Action? PresentationChanged;
    internal event Action? FirstHiddenToTray;
    internal DisplayPower ConfirmedPower => PrimaryPower;
    internal bool CanRefreshFromTray => _snapshotAvailable && _snapshot?.Binding is { NeedsConfirmation: false } &&
        _snapshot.ErrorCode != "topology_changed" && !IsBusy;
    internal bool CanControlFromTray => CanRefreshFromTray && PrimaryPower != DisplayPower.Unknown && !HasUnconfirmedOperation;
    internal void ObserveOperation(OperationRecord operation)
    {
        if (!_closed && _snapshot?.Binding?.TargetId == operation.TargetId)
        {
            _lastOperation = operation;
            SetOperationMessage(operation);
        }
    }
    internal string TrayStatus => !_snapshotAvailable ? "显示器状态待确认"
        : _snapshot?.Binding is null ? "尚未绑定显示器"
        : IsBusy ? "正在处理显示器操作…"
        : PrimaryPower switch
        {
            DisplayPower.On => "显示器已开启",
            DisplayPower.Off => "显示器已关闭",
            _ => "显示器状态待确认"
        };

    public Task RefreshViewAsync()
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return Task.CompletedTask;
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(RefreshViewAsync).Task.Unwrap();
        _snapshotRequested = true;
        if (_viewRefreshTask is { IsCompleted: false })
            return _viewRefreshTask;
        _viewRefreshTask = ReadSnapshotsAsync();
        return _viewRefreshTask;
    }

    public void AllowClose()
    {
        Dispatcher.VerifyAccess();
        _allowClose = true;
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        base.OnClosing(eventArgs);
        if (!_allowClose)
        {
            eventArgs.Cancel = true;
            Hide();
            if (!_hasHiddenToTray)
            {
                _hasHiddenToTray = true;
                FirstHiddenToTray?.Invoke();
            }
        }
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        _closed = true;
        _feedbackTimer.Stop();
        _relativeTimeTimer.Stop();
        _controller.Changed -= ControllerChanged;
        Loaded -= WindowLoaded;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(eventArgs);
    }

    private async void WindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_initialDiscoveryChecked)
        {
            await RefreshViewAsync();
            return;
        }
        _initialDiscoveryChecked = true;
        await RefreshViewAsync();
        if (!_closed && _snapshotAvailable && _snapshot?.Binding is null && !IsBusy)
            await DiscoverAsync();
    }

    private void ControllerChanged()
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_closed)
                    _ = RefreshViewAsync();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    private async Task ReadSnapshotsAsync()
    {
        while (_snapshotRequested && !_closed)
        {
            _snapshotRequested = false;
            var version = _snapshotVersion;
            try
            {
                var snapshot = await _controller.GetSnapshotAsync(_lifetimeToken);
                if (!_closed && version == _snapshotVersion)
                {
                    ApplySnapshot(snapshot);
                }
            }
            catch (OperationCanceledException) when (_closed)
            {
                return;
            }
            catch (Exception exception)
            {
                if (!_closed && version == _snapshotVersion)
                {
                    _snapshotAvailable = false;
                    _snapshotError = ErrorCode(exception);
                    Render();
                }
            }
        }
    }

    private void ApplySnapshot(HostSnapshot snapshot)
    {
        if (_closed)
            return;
        var operationWasUnconfirmed = HasUnconfirmedOperation;
        var bindingChanged = _snapshot?.Binding?.TargetId != snapshot.Binding?.TargetId;
        var newlyInvalidated =
            (snapshot.Binding?.NeedsConfirmation == true && _snapshot?.Binding?.NeedsConfirmation != true) ||
            (snapshot.ErrorCode == "topology_changed" && _snapshot?.ErrorCode != "topology_changed");
        if (bindingChanged || newlyInvalidated)
        {
            _lastOperation = null;
            _actionMessage = null;
            _actionDetails = null;
            _actionWarning = false;
            ClearDiscovery();
        }
        if (_snapshot is null || bindingChanged || newlyInvalidated)
            _bindingSection.IsExpanded = snapshot.Binding is null || snapshot.Binding.NeedsConfirmation || newlyInvalidated;
        _snapshot = snapshot;
        _snapshotAvailable = true;
        _snapshotError = null;
        _snapshotVersion++;
        if (operationWasUnconfirmed && !HasUnconfirmedOperation)
        {
            _actionMessage = null;
            _actionWarning = false;
            _feedbackTimer.Stop();
        }
        Render();
    }

    private bool IsBusy => _localBusy || _snapshot?.Busy == true;

    private bool HasUnconfirmedOperation => _lastOperation is { } operation &&
        (operation.Outcome == OperationOutcome.Pending ||
         operation.Outcome is OperationOutcome.AcceptedUnconfirmed or OperationOutcome.Unknown &&
         !(_snapshotAvailable && _snapshot is { ErrorCode: null, Binding.NeedsConfirmation: false } snapshot &&
           snapshot.Binding.TargetId == operation.TargetId && snapshot.Observation.ObservedAt > operation.UpdatedAt &&
           snapshot.Observation.State != DisplayPower.Unknown && UiText.IsTrusted(snapshot.Observation, snapshot.Simulation || _controller.Simulation)));

    private DisplayPower PrimaryPower => _snapshotAvailable && _snapshot is { ErrorCode: null, Binding.NeedsConfirmation: false } snapshot &&
        !HasUnconfirmedOperation &&
        UiText.IsTrusted(snapshot.Observation, snapshot.Simulation || _controller.Simulation)
            ? snapshot.Observation.State : DisplayPower.Unknown;

    private Task PrimaryActionAsync() => PrimaryPower switch
    {
        DisplayPower.On => ExecuteAsync(PowerIntent.Off),
        DisplayPower.Off => ExecuteAsync(PowerIntent.On),
        _ => RefreshHardwareAsync()
    };

    private async Task CheckOperationAsync(OperationRecord operation, CancellationToken cancellationToken, bool refresh = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellationToken = timeout.Token;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_closed || _lastOperation?.OperationId != operation.OperationId || !HasUnconfirmedOperation ||
                _snapshot?.Binding is not { NeedsConfirmation: false } binding || binding.TargetId != operation.TargetId ||
                _snapshot.ErrorCode == "topology_changed")
                return;
            await Task.Delay(350 * (attempt + 1), cancellationToken);
            var current = await _controller.GetOperationAsync(operation.OperationId, cancellationToken);
            if (_closed || _lastOperation?.OperationId != operation.OperationId ||
                _snapshot?.Binding?.TargetId != operation.TargetId)
                return;
            if (current is not null && current.OperationId == operation.OperationId && current.TargetId == operation.TargetId)
            {
                _lastOperation = current;
                SetOperationMessage(current);
            }
            await RefreshViewAsync();
        }
        if (refresh && !_closed && HasUnconfirmedOperation && _lastOperation?.OperationId == operation.OperationId &&
            _snapshot is { Busy: false, ErrorCode: null, Binding.NeedsConfirmation: false } snapshot &&
            snapshot.Binding.TargetId == operation.TargetId && _lastOperation.Outcome != OperationOutcome.Pending)
        {
            ApplySnapshot(await _controller.RefreshAsync(cancellationToken));
            if (_lastOperation is { } remaining)
                SetOperationMessage(remaining);
        }
    }

    private async Task RunActionAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_closed || IsBusy)
            return;
        _localBusy = true;
        _feedbackTimer.Stop();
        _actionMessage = message;
        _actionDetails = null;
        _actionWarning = false;
        Render();
        try
        {
            await action(_lifetimeToken);
        }
        catch (OperationCanceledException) when (_closed)
        {
            return;
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                var code = ErrorCode(exception);
                SetMessage(UiText.BriefError(code), $"{code}\n{UiText.Error(code)}", true);
                if (code == "topology_changed")
                    ClearDiscovery();
            }
        }
        finally
        {
            if (!_closed)
            {
                await RefreshViewAsync();
                if (!_closed)
                {
                    _localBusy = false;
                    Render();
                    StartFeedbackTimer();
                }
            }
        }
    }

    private Task DiscoverAsync() => RunActionAsync("正在发现显示器，请勿重复操作…", async cancellationToken =>
    {
        ClearDiscovery();
        var discovery = await _controller.DiscoverAsync(cancellationToken);
        if (_closed)
            return;
        _discovery = discovery;
        PopulateCandidates(discovery);
        SetMessage(discovery.Candidates.Count == 0
            ? "未发现可绑定的显示器。请检查连接和当前配置，再手动重扫。"
            : discovery.Candidates.Count == 1 ? "已选中显示器，请确认后点击“绑定所选”。"
            : "请选择显示器，再点击“绑定所选”。", warning: discovery.Candidates.Count == 0);
    });

    private Task BindSelectedAsync()
    {
        if (_discovery is not { } discovery || SelectedCandidate is not { } candidate)
            return Task.CompletedTask;
        return RunActionAsync("正在绑定所选显示器…", async cancellationToken =>
        {
            var snapshot = await _controller.BindAsync(candidate.TargetKey, discovery.TopologyFingerprint,
                candidate.Name, cancellationToken);
            ApplySnapshot(snapshot);
            if (_closed)
                return;
            ClearDiscovery();
            if (snapshot.Binding is { NeedsConfirmation: false } && !snapshot.Busy && snapshot.ErrorCode is null)
                ApplySnapshot(await _controller.RefreshAsync(cancellationToken));
            SetMessage(snapshot.Binding?.NeedsConfirmation == false
                ? "已绑定显示器。"
                : "绑定尚未确认。请查看当前状态，重新发现并确认目标。", warning: snapshot.Binding?.NeedsConfirmation != false);
        });
    }

    private Task UnbindAsync() => RunActionAsync("正在取消绑定…", async cancellationToken =>
    {
        var snapshot = await _controller.UnbindAsync(cancellationToken);
        ApplySnapshot(snapshot);
        if (_closed)
            return;
        ClearDiscovery();
        SetMessage(snapshot.Binding is null
            ? "已取消绑定。此操作不会开启或关闭显示器。"
            : "绑定仍然存在，请查看当前状态。", warning: snapshot.Binding is not null);
    });

    private Task RefreshHardwareAsync() => RunActionAsync("正在刷新显示器状态…", async cancellationToken =>
    {
        var snapshot = await _controller.RefreshAsync(cancellationToken);
        ApplySnapshot(snapshot);
        if (_closed)
            return;
        if (_lastOperation is { } operation && HasUnconfirmedOperation)
        {
            await CheckOperationAsync(operation, cancellationToken, refresh: false);
            if (_lastOperation is { } remaining)
                SetOperationMessage(remaining);
            return;
        }
        SetMessage(snapshot.ErrorCode is null
            ? "已刷新状态。"
            : UiText.BriefError(snapshot.ErrorCode), snapshot.ErrorCode, snapshot.ErrorCode is not null);
    });

    private Task ExecuteAsync(PowerIntent intent)
    {
        if (!_snapshotAvailable || _snapshot?.Binding is not { NeedsConfirmation: false } binding ||
            _snapshot.ErrorCode == "topology_changed" || HasUnconfirmedOperation)
            return Task.CompletedTask;
        var targetId = binding.TargetId;
        var operationId = Guid.NewGuid();
        return RunActionAsync($"正在{UiText.Intent(intent)}显示器…", async cancellationToken =>
        {
            var operation = new OperationRecord(operationId, targetId, intent, intent,
                OperationOutcome.Pending, null, null, DateTimeOffset.UtcNow);
            _lastOperation = operation;
            try
            {
                operation = await _controller.ExecuteAsync(targetId, intent, operationId, cancellationToken);
            }
            catch (Exception exception) when (!_closed)
            {
                operation = operation with { Outcome = OperationOutcome.Unknown, ErrorCode = ErrorCode(exception), UpdatedAt = DateTimeOffset.UtcNow };
            }
            if (_closed)
                return;
            if (_snapshot?.Binding?.TargetId == targetId)
            {
                _lastOperation = operation;
                SetOperationMessage(operation);
                if (HasUnconfirmedOperation)
                    await CheckOperationAsync(operation, cancellationToken);
            }
            else
                SetMessage("绑定已变化，本次请求针对之前的目标。请查看当前绑定后再操作。", warning: true);
        });
    }

    private void SetOperationMessage(OperationRecord operation)
    {
        var simulation = _snapshot?.Simulation == true || _controller.Simulation;
        var confirmed = operation.Outcome == OperationOutcome.Confirmed && operation.Observation is { } observation &&
            observation.State is DisplayPower.On or DisplayPower.Off && UiText.IsTrusted(observation, simulation);
        SetMessage(confirmed
                ? operation.Observation!.State switch
                {
                    DisplayPower.On => "显示器已开启。",
                    DisplayPower.Off => "显示器已关闭。",
                    _ => "显示器状态待确认，请刷新。"
                }
                : operation.Outcome == OperationOutcome.Confirmed ? "显示器状态待确认，请刷新。" : UiText.Operation(operation, simulation),
            $"操作 ID：{operation.OperationId}\n结果：{operation.Outcome}" +
            $"\n{UiText.Operation(operation, simulation)}" +
            (operation.ErrorCode is null ? string.Empty : $"\n错误代码：{operation.ErrorCode}"),
            !confirmed);
    }

    private async void StartupClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (_rendering || _closed)
            return;
        var enabled = _startup.IsChecked == true;
        if (!_snapshotAvailable || IsBusy)
        {
            Render();
            return;
        }
        _startupSaving = true;
        try
        {
            await RunActionAsync("正在保存自动启动设置…", async cancellationToken =>
            {
                HostSnapshot snapshot;
                try
                {
                    snapshot = await _controller.SetStartupAsync(enabled, cancellationToken);
                }
                catch (ControlException exception) when (enabled && exception.Code == "startup_owned_by_another_install" && _requestStartupReplacement is not null)
                {
                    if (!_requestStartupReplacement())
                    {
                        SetMessage("已保留原有自动启动设置。", warning: true);
                        return;
                    }
                    snapshot = await _controller.SetStartupAsync(true, cancellationToken);
                }
                ApplySnapshot(snapshot);
                if (!_closed)
                    SetMessage(snapshot.StartupEnabled == enabled
                        ? (enabled ? "已开启登录自动启动。" : "已关闭登录自动启动。")
                        : "自动启动设置未生效，请重试。", warning: snapshot.StartupEnabled != enabled);
            });
        }
        finally
        {
            _startupSaving = false;
            Render();
        }
    }

    private void SetMessage(string message, string? details = null, bool warning = false)
    {
        if (_closed)
            return;
        _actionMessage = message;
        _actionDetails = details;
        _actionWarning = warning;
        _feedbackTimer.Stop();
        StartFeedbackTimer();
        Render();
    }

    private void StartFeedbackTimer()
    {
        if (!_closed && !_localBusy && !_actionWarning && !string.IsNullOrEmpty(_actionMessage))
            _feedbackTimer.Start();
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        ControlException control => control.Code,
        TimeoutException => "timeout",
        OperationCanceledException => "operation_cancelled",
        _ => "unexpected_error"
    };
}
