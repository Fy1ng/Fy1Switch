using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace Fy1Switch.Host.UI;

internal static class WindowFrame
{
    internal static FrameworkElement Wrap(Window window, UIElement content, string? closeHint = null)
    {
        window.Icon = new BitmapImage(new Uri("pack://application:,,,/Fy1Switch.Host;component/Assets/App.png"));
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        // Windows 10 keeps a rectangular HWND when DWM glass is enabled.
        // Only round the WPF content after the native window accepts rounding.
        var normalCornerRadius = new CornerRadius(0);
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(6),
            // A nonzero DWM frame keeps the native window shadow with a custom caption.
            GlassFrameThickness = new Thickness(1),
            CornerRadius = normalCornerRadius,
            UseAeroCaptionButtons = false,
        });

        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, DesktopTheme.WindowBrush);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var caption = new DockPanel { LastChildFill = true };
        caption.SetResourceReference(Panel.BackgroundProperty, DesktopTheme.WindowBrush);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        caption.Children.Add(controls);
        var title = new TextBlock
        {
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, DesktopTheme.ForegroundBrush);
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        var titleContent = new DockPanel { Margin = new Thickness(16, 0, 0, 0) };
        var logo = new Image
        {
            Source = window.Icon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true, UseLayoutRounding = true,
        };
        // WPF's default bilinear sampling aliases the 256px artwork at caption size.
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        titleContent.Children.Add(logo);
        titleContent.Children.Add(title);
        caption.Children.Add(titleContent);
        root.Children.Add(caption);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var style = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Background" Value="Transparent"/>
              <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Surface" Background="{TemplateBinding Background}">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fy1Switch.SubtleBrush}"/>
                      </Trigger>
                      <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                        <Setter TargetName="Surface" Property="BorderThickness" Value="2"/>
                      </Trigger>
                      <MultiTrigger>
                        <MultiTrigger.Conditions>
                          <Condition Property="Tag" Value="close"/>
                          <Condition Property="IsMouseOver" Value="True"/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                        <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
                      </MultiTrigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);

        Button CaptionButton(AppIconKind kind, string name, Action action)
        {
            var button = new Button
            {
                Content = new VectorIcon { Kind = kind, Width = 16, Height = 16 }, Width = 46, Height = 44, Padding = new Thickness(0),
                Style = style, ToolTip = name,
            };
            AutomationProperties.SetName(button, name);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += (_, _) => action();
            controls.Children.Add(button);
            return button;
        }

        CaptionButton(AppIconKind.Minimize, "最小化", () => SystemCommands.MinimizeWindow(window));
        var maximize = CaptionButton(AppIconKind.Maximize, "最大化", () =>
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        });
        var close = CaptionButton(AppIconKind.Close, "关闭", () => SystemCommands.CloseWindow(window));
        close.Tag = "close";
        close.ToolTip = closeHint ?? "关闭窗口";
        window.StateChanged += (_, _) =>
        {
            var maximized = window.WindowState == WindowState.Maximized;
            ((VectorIcon)maximize.Content).Kind = maximized ? AppIconKind.Restore : AppIconKind.Maximize;
            maximize.ToolTip = maximized ? "还原" : "最大化";
            AutomationProperties.SetName(maximize, maximized ? "还原" : "最大化");
        };
        var frame = new Border
        {
            CornerRadius = normalCornerRadius,
            Child = root,
            SnapsToDevicePixels = true,
        };
        frame.SetResourceReference(Border.BackgroundProperty, DesktopTheme.WindowBrush);
        frame.SetResourceReference(Border.BorderBrushProperty, DesktopTheme.BorderBrush);
        void UpdateContentClip()
        {
            // Clip the caption and content together so the close hover fills the
            // same inner corner as the frame, without a second rounded cutout.
            var clip = new RectangleGeometry(new Rect(root.RenderSize),
                Math.Max(0, frame.CornerRadius.TopLeft - frame.BorderThickness.Left),
                Math.Max(0, frame.CornerRadius.TopLeft - frame.BorderThickness.Top));
            clip.Freeze();
            root.Clip = clip;
        }
        void UpdateFrame()
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            frame.BorderThickness = new Thickness(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY, 1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
            frame.CornerRadius = window.WindowState == WindowState.Maximized ? new CornerRadius(0) : normalCornerRadius;
            WindowChrome.GetWindowChrome(window).CornerRadius = frame.CornerRadius;
            UpdateContentClip();
        }
        UpdateFrame();
        root.SizeChanged += (_, _) => UpdateContentClip();
        window.DpiChanged += (_, _) => UpdateFrame();
        window.StateChanged += (_, _) => UpdateFrame();
        window.SourceInitialized += (_, _) =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var handle = new WindowInteropHelper(window).Handle;
                var roundCorners = 2; // DWMWCP_ROUND
                if (DwmSetWindowAttribute(handle, 33, ref roundCorners, sizeof(int)) == 0)
                    normalCornerRadius = new CornerRadius(8); // DWMWA_WINDOW_CORNER_PREFERENCE
                // Windows 11 has its own 1px border; keep only the one drawn above.
                var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                _ = DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
                EnableSnapLayouts(window, maximize, handle);
            }
            UpdateFrame();
        };
        return frame;
    }

    private static void EnableSnapLayouts(Window window, Button maximize, IntPtr handle)
    {
        // Return the native maximize hit-test before WPF's WindowChrome consumes it.
        // DefSubclassProc continues receiving non-client hover messages, allowing the
        // Windows 11 shell to display Snap Layouts over our custom caption button.
        var pressed = false;
        bool IsOver(IntPtr point)
        {
            if (!maximize.IsVisible || !maximize.IsEnabled || window.WindowState == WindowState.Minimized ||
                PresentationSource.FromVisual(maximize) is null)
                return false;
            var value = point.ToInt64();
            var screen = new Point(unchecked((short)(value & 0xffff)), unchecked((short)((value >> 16) & 0xffff)));
            return new Rect(new Point(), maximize.RenderSize).Contains(maximize.PointFromScreen(screen));
        }
        SubclassProc procedure = (hwnd, message, wParam, lParam, _, _) =>
        {
            if (message == 0x0084 && IsOver(lParam)) // WM_NCHITTEST
                return new IntPtr(9); // HTMAXBUTTON
            if (message is 0x00A1 or 0x00A3 && wParam.ToInt32() == 9) // WM_NCLBUTTONDOWN / DBLCLK
            {
                pressed = true;
                return IntPtr.Zero;
            }
            if (message == 0x00A2) // WM_NCLBUTTONUP
            {
                var activate = pressed && IsOver(lParam);
                pressed = false;
                if (activate)
                {
                    window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    return IntPtr.Zero;
                }
            }
            if (message is 0x0008 or 0x0215) pressed = false; // focus/capture lost
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        if (!SetWindowSubclass(handle, procedure, new UIntPtr(1), UIntPtr.Zero))
            return;
        window.Closed += (_, _) =>
        {
            _ = RemoveWindowSubclass(handle, procedure, new UIntPtr(1));
            GC.KeepAlive(procedure);
        };
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
