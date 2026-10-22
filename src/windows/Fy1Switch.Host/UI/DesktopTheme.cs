using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace Fy1Switch.Host.UI;

internal static class DesktopTheme
{
    internal const string WindowBrush = "Fy1Switch.WindowBrush";
    internal const string SurfaceBrush = "Fy1Switch.SurfaceBrush";
    internal const string SubtleBrush = "Fy1Switch.SubtleBrush";
    internal const string ForegroundBrush = "Fy1Switch.ForegroundBrush";
    internal const string SecondaryBrush = "Fy1Switch.SecondaryBrush";
    internal const string BorderBrush = "Fy1Switch.BorderBrush";
    internal const string AccentBrush = "Fy1Switch.AccentBrush";
    internal const string AccentForegroundBrush = "Fy1Switch.AccentForegroundBrush";
    internal const string WarningBrush = "Fy1Switch.WarningBrush";
    internal const string WarningForegroundBrush = "Fy1Switch.WarningForegroundBrush";
    internal const string SuccessBrush = "Fy1Switch.SuccessBrush";
    internal const string OffBrush = "Fy1Switch.OffBrush";

    internal static ResourceDictionary CreateResources()
    {
        var resources = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SubtleBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          CornerRadius="10" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.86"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.7"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter TargetName="Surface" Property="BorderThickness" Value="2"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.5"/>
                      <Setter Property="Cursor" Value="Arrow"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="Expander">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Padding" Value="0,6"/>
          </Style>
          <Style TargetType="ListBox">
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SubtleBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
            <Setter Property="Padding" Value="6"/>
          </Style>
          <Style TargetType="CheckBox">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SurfaceBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                  <Grid x:Name="Root">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Border x:Name="Box" Width="18" Height="18" Margin="0,0,8,0" VerticalAlignment="Center"
                            Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1" CornerRadius="3">
                      <Grid>
                        <Path x:Name="Check" Data="M 3,8 L 7,12 L 14,4" Stroke="{DynamicResource Fy1Switch.AccentForegroundBrush}"
                              StrokeThickness="2" Visibility="Collapsed"/>
                        <Border x:Name="Mixed" Height="2" Margin="4,0" VerticalAlignment="Center"
                                Background="{DynamicResource Fy1Switch.AccentForegroundBrush}" Visibility="Collapsed"/>
                      </Grid>
                    </Border>
                    <ContentPresenter Grid.Column="1" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" RecognizesAccessKey="True"/>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Box" Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="{x:Null}">
                      <Setter TargetName="Box" Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter TargetName="Mixed" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter TargetName="Box" Property="BorderThickness" Value="2"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Root" Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="TextBox">
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SurfaceBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
          </Style>
          <Style TargetType="ComboBox">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SurfaceBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
            <Setter Property="Padding" Value="10,6,34,6"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                  <Grid x:Name="Root">
                    <ToggleButton x:Name="DropDownToggle" Focusable="False" ClickMode="Press"
                                  Background="{TemplateBinding Background}" Foreground="{TemplateBinding Foreground}"
                                  BorderBrush="{TemplateBinding BorderBrush}"
                                  IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                      <ToggleButton.Template>
                        <ControlTemplate TargetType="ToggleButton">
                          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                  BorderThickness="1" CornerRadius="6">
                            <Path Data="M 0,0 L 5,5 L 10,0" Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5"
                                  HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"/>
                          </Border>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter Margin="{TemplateBinding Padding}" IsHitTestVisible="False"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                      Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                      ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                                      ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"/>
                    <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                           IsOpen="{TemplateBinding IsDropDownOpen}">
                      <Border Background="{DynamicResource Fy1Switch.SurfaceBrush}" BorderBrush="{DynamicResource Fy1Switch.BorderBrush}"
                              BorderThickness="1" CornerRadius="6" Padding="4"
                              MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                              MaxHeight="{TemplateBinding MaxDropDownHeight}">
                        <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                          <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                        </ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True">
                      <Setter TargetName="DropDownToggle" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                    </Trigger>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="DropDownToggle" Property="BorderBrush" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Root" Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ComboBoxItem">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="Padding" Value="8,7"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                  <Border x:Name="Surface" Background="Transparent" CornerRadius="3" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Surface" Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                      <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ListBoxItem">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Style.Triggers>
              <Trigger Property="IsSelected" Value="True">
                <Setter Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
              </Trigger>
            </Style.Triggers>
          </Style>
          <Style TargetType="ToolTip">
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SurfaceBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
          </Style>
        </ResourceDictionary>
        """);
        UpdatePalette(resources);
        return resources;
    }

    internal static void Attach(Window window)
    {
        var resources = CreateResources();
        window.Resources.MergedDictionaries.Add(resources);
        window.SetResourceReference(Control.BackgroundProperty, WindowBrush);
        window.SetResourceReference(Control.ForegroundProperty, ForegroundBrush);
        void Update()
        {
            if (!window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.BeginInvoke(new Action(() => UpdatePalette(resources)));
        }
        UserPreferenceChangedEventHandler preferencesChanged = (_, _) => Update();
        PropertyChangedEventHandler parametersChanged = (_, _) => Update();
        SystemEvents.UserPreferenceChanged += preferencesChanged;
        SystemParameters.StaticPropertyChanged += parametersChanged;
        window.Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= preferencesChanged;
            SystemParameters.StaticPropertyChanged -= parametersChanged;
        };
    }

    private static void UpdatePalette(ResourceDictionary resources)
    {
        if (SystemParameters.HighContrast)
        {
            resources[WindowBrush] = resources[SurfaceBrush] = resources[SubtleBrush] = SystemColors.WindowBrush;
            resources[ForegroundBrush] = resources[SecondaryBrush] = resources[WarningForegroundBrush] =
                resources[SuccessBrush] = resources[OffBrush] = SystemColors.WindowTextBrush;
            resources[BorderBrush] = SystemColors.WindowTextBrush;
            resources[AccentBrush] = SystemColors.HighlightBrush;
            resources[AccentForegroundBrush] = SystemColors.HighlightTextBrush;
            resources[WarningBrush] = SystemColors.WindowBrush;
            return;
        }
        var dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        Set(WindowBrush, dark ? "#171A20" : "#F6F8FA");
        Set(SurfaceBrush, dark ? "#222730" : "#FFFFFF");
        Set(SubtleBrush, dark ? "#2C3440" : "#EEF2F8");
        Set(ForegroundBrush, dark ? "#F1F4F8" : "#1F2937");
        Set(SecondaryBrush, dark ? "#B7C2D2" : "#4B5563");
        Set(BorderBrush, dark ? "#566173" : "#D5DBE2");
        Set(AccentBrush, dark ? "#9EC3FF" : "#2D5CB4");
        Set(AccentForegroundBrush, dark ? "#102241" : "#FFFFFF");
        Set(WarningBrush, dark ? "#44351F" : "#FFF4E0");
        Set(WarningForegroundBrush, dark ? "#FFD98B" : "#83560F");
        Set(SuccessBrush, dark ? "#79D5B9" : "#1B755D");
        Set(OffBrush, dark ? "#ACBFFF" : "#425589");

        void Set(string key, string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            resources[key] = brush;
        }
    }
}
