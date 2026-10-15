using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;

namespace RemoteDisplaySwitch.Host.UI;

internal static class WindowFrame
{
    internal static FrameworkElement Wrap(Window window, UIElement content, string? closeHint = null)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false,
        });

        var root = new Grid { Background = window.Background };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var caption = new DockPanel { LastChildFill = true, Background = window.Background };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        caption.Children.Add(controls);
        var title = new TextBlock
        {
            Margin = new Thickness(20, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(46, 57, 75)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        caption.Children.Add(title);
        root.Children.Add(caption);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var style = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Background" Value="Transparent"/>
              <Setter Property="Foreground" Value="#344054"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Surface" Background="{TemplateBinding Background}">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Surface" Property="Background" Value="#E4E9F0"/>
                      </Trigger>
                      <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Surface" Property="Background" Value="#CDD7E3"/>
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="Surface" Property="BorderBrush" Value="#3974D2"/>
                        <Setter TargetName="Surface" Property="BorderThickness" Value="2"/>
                      </Trigger>
                      <Trigger Property="Tag" Value="close">
                        <Setter TargetName="Surface" Property="CornerRadius" Value="0,8,0,0"/>
                      </Trigger>
                      <MultiTrigger>
                        <MultiTrigger.Conditions>
                          <Condition Property="Tag" Value="close"/>
                          <Condition Property="IsMouseOver" Value="True"/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName="Surface" Property="Background" Value="#C42B1C"/>
                        <Setter Property="Foreground" Value="White"/>
                      </MultiTrigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);

        Button CaptionButton(string glyph, string name, Action action)
        {
            var button = new Button
            {
                Content = glyph, Width = 46, Height = 44, Padding = new Thickness(0),
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                Style = style, ToolTip = name,
            };
            AutomationProperties.SetName(button, name);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += (_, _) => action();
            controls.Children.Add(button);
            return button;
        }

        CaptionButton("\uE921", "最小化", () => SystemCommands.MinimizeWindow(window));
        var maximize = CaptionButton("\uE922", "最大化", () =>
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        });
        var close = CaptionButton("\uE8BB", "关闭", () => SystemCommands.CloseWindow(window));
        close.Tag = "close";
        close.ToolTip = closeHint ?? "关闭窗口";
        window.StateChanged += (_, _) =>
        {
            var maximized = window.WindowState == WindowState.Maximized;
            maximize.Content = maximized ? "\uE923" : "\uE922";
            maximize.ToolTip = maximized ? "还原" : "最大化";
            AutomationProperties.SetName(maximize, maximized ? "还原" : "最大化");
        };
        return root;
    }
}
