using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Host.UI;
using RemoteDisplaySwitch.Runtime;
using RemoteDisplaySwitch.Remote;
using Forms = System.Windows.Forms;

namespace RemoteDisplaySwitch.Host;

internal sealed record HostOptions(bool Background, bool Simulation, string DataDirectory, string PipeName, string WorkerPath, string? PreviewPath, int? StopAfterSeconds, bool RemotePreview)
{
    public static HostOptions Parse(string[] args)
    {
        bool background = false, simulation = false, remotePreview = false;
        string? directory = null, pipe = null, worker = null, preview = null;
        int? stop = null;
        var seen = new HashSet<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!seen.Add(name)) throw new ArgumentException("Duplicate option.");
            string Value() => ++index < args.Length ? args[index] : throw new ArgumentException("Missing option value.");
            switch (name)
            {
                case "--background": background = true; break;
                case "--simulate": simulation = true; break;
                case "--data-dir": directory = Value(); break;
                case "--pipe": pipe = Value(); break;
                case "--worker-path": worker = Value(); break;
                case "--render-preview": preview = Value(); break;
                case "--remote-preview": remotePreview = true; break;
                case "--stop-after-seconds": stop = int.Parse(Value()); if (stop is < 1 or > 3600) throw new ArgumentException("Invalid stop interval."); break;
                default: throw new ArgumentException("Unknown option.");
            }
        }
        if (simulation && directory is null) throw new ArgumentException("Simulation requires an isolated data directory.");
        if (preview is not null && !simulation) throw new ArgumentException("Preview requires simulation.");
        if (remotePreview && preview is null) throw new ArgumentException("Remote preview requires a preview path.");
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDisplaySwitch", "Host");
        pipe ??= simulation ? "RemoteDisplaySwitch.Simulation." + Guid.NewGuid().ToString("N") : PipeIdentity.DefaultHostPipe;
        worker ??= Path.Combine(AppContext.BaseDirectory, "worker", "RemoteDisplaySwitch.Worker.exe");
        return new(background, simulation, Path.GetFullPath(directory), pipe, Path.GetFullPath(worker), preview is null ? null : Path.GetFullPath(preview), stop, remotePreview);
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { return new HostApplication(HostOptions.Parse(args)).Run(); }
        catch (Exception exception) { Console.Error.WriteLine(exception.GetType().Name); return 1; }
    }
}

internal sealed class HostApplication(HostOptions options) : System.Windows.Application
{
    private readonly CancellationTokenSource _stop = new();
    private DesktopController? _controller;
    private RemoteManager? _remote;
    private RemoteWindow? _remoteWindow;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private FileStream? _instance;
    private Task? _serverTask;
    private bool _exiting;
    private DispatcherTimer? _exitTimer;
    private TrayIcons? _trayIcons;
    private Forms.ToolStripItem? _trayRefresh;
    private Forms.ToolStripItem? _trayOn;
    private Forms.ToolStripItem? _trayOff;
    private bool _trayBusy;

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            var locks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDisplaySwitch", "Locks");
            Directory.CreateDirectory(locks);
            var lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.PipeName))) + ".lock";
            try { _instance = new FileStream(Path.Combine(locks, lockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                try { await LocalPipeClient.SendAsync(options.PipeName, Environment.ProcessPath!, new(Guid.NewGuid(), "show")); } catch { }
                Shutdown(2); return;
            }
            var worker = new WorkerSupervisor(options.WorkerPath, Path.Combine(options.DataDirectory, "worker"), options.Simulation);
            _controller = new DesktopController(worker, new StartupRegistration(Environment.ProcessPath!, options.Simulation), options.DataDirectory, options.Simulation);
            _window = new MainWindow(_controller);
            _window.RemoteSettingsRequested += ShowRemoteSettings;
            _window.PresentationChanged += UpdateTray;
            _window.FirstHiddenToTray += () => _tray?.ShowBalloonTip(5000, "已最小化到托盘", "程序仍在运行。双击托盘图标可打开窗口；如需退出，请使用托盘菜单。", Forms.ToolTipIcon.Info);
            MainWindow = _window;
            if (options.PreviewPath is not null)
            {
                if (options.RemotePreview)
                {
                    _remote = new RemoteManager(_controller, (await _controller.GetSnapshotAsync()).DeviceId, Path.Combine(options.DataDirectory, "remote"));
                    _remoteWindow = new RemoteWindow(_remote);
                    await Dispatcher.InvokeAsync(() => RenderPreview(options.PreviewPath), DispatcherPriority.ApplicationIdle);
                    await ExitAsync();
                    return;
                }
                var discovered = await _controller.DiscoverAsync();
                var candidate = discovered.Candidates.First();
                await _controller.BindAsync(candidate.TargetKey, discovered.TopologyFingerprint, candidate.Name);
                await _controller.RefreshAsync();
                await _window.RefreshViewAsync();
                await Dispatcher.InvokeAsync(() => RenderPreview(options.PreviewPath), DispatcherPriority.ApplicationIdle);
                await ExitAsync();
                return;
            }
            try
            {
                _remote = new RemoteManager(_controller, (await _controller.GetSnapshotAsync()).DeviceId, Path.Combine(options.DataDirectory, "remote"));
                if (_remote.Settings.Enabled) await _remote.ApplyAsync(_remote.Settings, persist: false);
            }
            catch (Exception exception) { SaveHostError(exception); }
            _serverTask = new LocalPipeServer(options.PipeName, _controller, command => Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (command == "shutdown") await ExitAsync();
                else ShowSettings();
            }))).RunAsync(_stop.Token);
            _ = WatchServerAsync(_serverTask);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开设置", null, (_, _) => ShowSettings());
            menu.Items.Add("连接手机", null, (_, _) => ShowRemoteSettings());
            _trayRefresh = menu.Items.Add("刷新状态", null, async (_, _) => await TrayRefreshAsync());
            _trayOn = menu.Items.Add("开启显示器", null, async (_, _) => await TrayPowerAsync(PowerIntent.On));
            _trayOff = menu.Items.Add("关闭显示器", null, async (_, _) => await TrayPowerAsync(PowerIntent.Off));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
            _trayIcons = new TrayIcons();
            _tray = new Forms.NotifyIcon { Text = "RemoteDisplaySwitch", Icon = _trayIcons.For(DisplayPower.Unknown), ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (_, _) => ShowSettings();
            _tray.BalloonTipClicked += (_, _) => ShowSettings();
            menu.Opening += (_, _) => UpdateTray();
            SystemEvents.UserPreferenceChanged += ThemeChanged;
            await _window.RefreshViewAsync();
            UpdateTray();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            if (!options.Background) _window.Show();
            if (options.StopAfterSeconds is { } seconds)
            {
                _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                _exitTimer.Tick += async (_, _) => { _exitTimer.Stop(); await ExitAsync(); };
                _exitTimer.Start();
            }
        }
        catch (Exception exception)
        {
            SaveHostError(exception);
            if (!options.Background && options.PreviewPath is null) MessageBox.Show("程序启动失败，请检查本地诊断目录。", "RemoteDisplaySwitch");
            await ExitAsync(1);
        }
    }
    private async Task WatchServerAsync(Task server)
    {
        try { await server; }
        catch (Exception exception) { SaveHostError(exception); await Dispatcher.InvokeAsync(async () => await ExitAsync(1)); }
    }
    private void SaveHostError(Exception exception)
    {
        try { File.AppendAllText(Path.Combine(options.DataDirectory, "host.log"), $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name}{Environment.NewLine}"); } catch { }
    }
    private void ShowSettings() { if (_window is null || _exiting) return; BringToFront(_window); }
    private static void BringToFront(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(window);
        window.Activate();
    }
    private void ShowRemoteSettings()
    {
        if (_remote is null) { MessageBox.Show("远程身份存储未能初始化，请检查本程序诊断。", "RemoteDisplaySwitch"); return; }
        if (_remoteWindow is null)
        {
            _remoteWindow = new RemoteWindow(_remote);
            _remoteWindow.Closed += (_, _) => _remoteWindow = null;
        }
        BringToFront(_remoteWindow);
    }
    private async Task TrayRefreshAsync()
    {
        if (_controller is null || _trayBusy || _window?.CanRefreshFromTray != true) return;
        _trayBusy = true;
        UpdateTray();
        try
        {
            var snapshot = await _controller.RefreshAsync();
            if (snapshot.ErrorCode is { } error) TrayFailure(UiText.BriefError(error));
            else if (!UiText.IsTrusted(snapshot.Observation, snapshot.Simulation) || snapshot.Observation.State == DisplayPower.Unknown)
                TrayFailure("显示器状态待确认，请检查连接后刷新。");
        }
        catch (Exception exception) { TrayFailure(TrayError(exception)); }
        finally
        {
            if (_window is not null) await _window.RefreshViewAsync();
            _trayBusy = false;
            UpdateTray();
        }
    }
    private async Task TrayPowerAsync(PowerIntent intent)
    {
        if (_controller is null || _trayBusy || _window?.CanControlFromTray != true) return;
        _trayBusy = true;
        UpdateTray();
        try
        {
            var snapshot = await _controller.GetSnapshotAsync();
            if (snapshot.Busy || snapshot.Binding is null || snapshot.Binding.NeedsConfirmation || snapshot.ErrorCode is not null ||
                !UiText.IsTrusted(snapshot.Observation, snapshot.Simulation) || snapshot.Observation.State == DisplayPower.Unknown)
            {
                TrayFailure(snapshot.Binding is null ? "请先打开设置并绑定显示器。" : "显示器状态待确认，请先刷新。");
                return;
            }
            var result = await _controller.ExecuteAsync(snapshot.Binding.TargetId, intent, Guid.NewGuid());
            _window?.ObserveOperation(result);
            if (result.Outcome != OperationOutcome.Confirmed)
                TrayFailure(result.ErrorCode is { } error ? UiText.BriefError(error) : "暂时无法确认操作结果，请先刷新显示器状态。");
        }
        catch (Exception exception) { TrayFailure(TrayError(exception)); }
        finally
        {
            if (_window is not null) await _window.RefreshViewAsync();
            _trayBusy = false;
            UpdateTray();
        }
    }
    private void UpdateTray()
    {
        if (_tray is null || _trayIcons is null || _window is null || _exiting) return;
        _tray.Icon = _trayIcons.For(_window.ConfirmedPower);
        _tray.Text = "RemoteDisplaySwitch：" + (_trayBusy ? "正在处理…" : _window.TrayStatus) + (options.Simulation ? "（模拟）" : string.Empty);
        if (_trayRefresh is not null) _trayRefresh.Enabled = !_trayBusy && _window.CanRefreshFromTray;
        if (_trayOn is not null) _trayOn.Enabled = !_trayBusy && _window.CanControlFromTray && _window.ConfirmedPower != DisplayPower.On;
        if (_trayOff is not null) _trayOff.Enabled = !_trayBusy && _window.CanControlFromTray && _window.ConfirmedPower != DisplayPower.Off;
    }
    private void TrayFailure(string reason) => _tray?.ShowBalloonTip(6000, "操作未完成", reason, Forms.ToolTipIcon.Warning);
    private static string TrayError(Exception exception) => UiText.BriefError(exception switch
    {
        ControlException control => control.Code,
        TimeoutException => "timeout",
        OperationCanceledException => "operation_cancelled",
        _ => "unexpected_error"
    });
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (_exiting || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting) return;
            var previous = _trayIcons;
            _trayIcons = new TrayIcons();
            UpdateTray();
            previous?.Dispose();
        }));
    }
    private void DisplayChanged(object? sender, EventArgs args) { if (!options.Simulation) _ = RevalidateDisplaySettingsAsync(); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs args) { if (!options.Simulation && args.Mode == PowerModes.Resume) _ = RevalidateDisplaySettingsAsync(); }
    private async Task RevalidateDisplaySettingsAsync()
    {
        try { if (_controller is not null) await _controller.RevalidateTopologyAsync(); }
        catch (Exception exception) { SaveHostError(exception); }
    }
    private void RenderPreview(string target)
    {
        var root = (FrameworkElement)(options.RemotePreview ? _remoteWindow!.Content : _window!.Content);
        root.Measure(new Size(780, 840)); root.Arrange(new Rect(0, 0, 780, 840)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(780, 840, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var file = File.Create(target);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
    }
    private async Task ExitAsync(int code = 0)
    {
        if (_exiting) return;
        _exiting = true;
        _exitTimer?.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.UserPreferenceChanged -= ThemeChanged;
        _stop.Cancel();
        _remoteWindow?.Close();
        if (_remote is not null) { try { await _remote.DisposeAsync(); } catch (Exception exception) { SaveHostError(exception); } }
        if (_serverTask is not null) { try { await _serverTask; } catch { } }
        if (_controller is not null) { try { await _controller.DisposeAsync(); } catch (Exception exception) { SaveHostError(exception); } }
        _window?.AllowClose(); _window?.Close();
        _tray?.Dispose(); _trayIcons?.Dispose(); _instance?.Dispose();
        Shutdown(code);
    }
}
