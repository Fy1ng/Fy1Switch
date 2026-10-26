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
    internal const string SettingsInputBrush = "Fy1Switch.SettingsInputBrush";
    internal const string SettingsBorderBrush = "Fy1Switch.SettingsBorderBrush";
    internal const string SettingsSelectionBrush = "Fy1Switch.SettingsSelectionBrush";
    internal const string SettingsSelectionForegroundBrush = "Fy1Switch.SettingsSelectionForegroundBrush";
    internal const string ButtonBrush = "Fy1Switch.ButtonBrush";
    internal const string ButtonHoverBrush = "Fy1Switch.ButtonHoverBrush";
    internal const string ButtonPressedBrush = "Fy1Switch.ButtonPressedBrush";
    internal const string ButtonActiveForegroundBrush = "Fy1Switch.ButtonActiveForegroundBrush";
    internal const string PrimaryButtonHoverBrush = "Fy1Switch.PrimaryButtonHoverBrush";
    internal const string PrimaryButtonPressedBrush = "Fy1Switch.PrimaryButtonPressedBrush";

    internal static ResourceDictionary CreateResources()
    {
        var resources = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.ButtonBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          CornerRadius="6" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.45"/>
                      <Setter Property="Cursor" Value="Arrow"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
            <Style.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Fy1Switch.ButtonHoverBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ButtonActiveForegroundBrush}"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter Property="Background" Value="{DynamicResource Fy1Switch.ButtonPressedBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ButtonActiveForegroundBrush}"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="{DynamicResource Fy1Switch.ButtonBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
              </Trigger>
            </Style.Triggers>
          </Style>
          <Style x:Key="Fy1Switch.PrimaryButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
            <Style.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource Fy1Switch.PrimaryButtonHoverBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
              </Trigger>
              <Trigger Property="IsPressed" Value="True">
                <Setter Property="Background" Value="{DynamicResource Fy1Switch.PrimaryButtonPressedBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
              </Trigger>
              <Trigger Property="IsEnabled" Value="False">
                <Setter Property="Background" Value="{DynamicResource Fy1Switch.AccentBrush}"/>
                <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.AccentForegroundBrush}"/>
              </Trigger>
            </Style.Triggers>
          </Style>
          <Style TargetType="CheckBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
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
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Root" Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="Fy1Switch.Display.Slider" TargetType="Slider">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Slider">
                  <Grid x:Name="Surface" Background="Transparent" MinHeight="28">
                    <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                           Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                           IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                      <Track.DecreaseRepeatButton>
                        <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                          <RepeatButton.Template>
                            <ControlTemplate TargetType="RepeatButton">
                              <Border Height="4" CornerRadius="2" Background="{DynamicResource Fy1Switch.AccentBrush}"/>
                            </ControlTemplate>
                          </RepeatButton.Template>
                        </RepeatButton>
                      </Track.DecreaseRepeatButton>
                      <Track.IncreaseRepeatButton>
                        <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                          <RepeatButton.Template>
                            <ControlTemplate TargetType="RepeatButton">
                              <Border Height="4" CornerRadius="2" Background="{DynamicResource Fy1Switch.BorderBrush}"/>
                            </ControlTemplate>
                          </RepeatButton.Template>
                        </RepeatButton>
                      </Track.IncreaseRepeatButton>
                      <Track.Thumb>
                        <Thumb Width="16" Height="20" FocusVisualStyle="{x:Null}">
                          <Thumb.Template>
                            <ControlTemplate TargetType="Thumb">
                              <Ellipse Width="14" Height="14" Fill="{DynamicResource Fy1Switch.AccentBrush}"
                                       Stroke="{DynamicResource Fy1Switch.SurfaceBrush}" StrokeThickness="2"/>
                            </ControlTemplate>
                          </Thumb.Template>
                        </Thumb>
                      </Track.Thumb>
                    </Track>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.4"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ComboBox">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SurfaceBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.BorderBrush}"/>
            <Setter Property="Padding" Value="10,6,34,6"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                  <Grid x:Name="Root" Background="Transparent">
                    <ToggleButton x:Name="DropDownToggle" Focusable="False" FocusVisualStyle="{x:Null}" ClickMode="Press"
                                  HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
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
                        <ScrollViewer CanContentScroll="True" Background="Transparent" HorizontalContentAlignment="Stretch"
                                      HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                          <ItemsPresenter HorizontalAlignment="Stretch" KeyboardNavigation.DirectionalNavigation="Contained"/>
                        </ScrollViewer>
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers>
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
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Padding" Value="8,7"/>
            <Setter Property="HorizontalAlignment" Value="Stretch"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}" HorizontalAlignment="Stretch"
                          CornerRadius="3" Padding="{TemplateBinding Padding}">
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
          <Style x:Key="Fy1Switch.Settings.TextBox" TargetType="TextBox">
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SettingsInputBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.SettingsBorderBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="10,6"/>
            <Setter Property="MinHeight" Value="34"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          CornerRadius="6" SnapsToDevicePixels="True">
                    <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                  HorizontalAlignment="Stretch" Focusable="False"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsReadOnly" Value="True">
                      <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.SecondaryBrush}"/>
                    </Trigger>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource Fy1Switch.SecondaryBrush}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="Fy1Switch.Settings.PasswordBox" TargetType="PasswordBox">
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SettingsInputBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="CaretBrush" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.SettingsBorderBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="10,6"/>
            <Setter Property="MinHeight" Value="34"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="PasswordBox">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          CornerRadius="6" SnapsToDevicePixels="True">
                    <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                  HorizontalAlignment="Stretch" Focusable="False"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource Fy1Switch.SecondaryBrush}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="Fy1Switch.Settings.Button" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Padding" Value="12,6"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="MinWidth" Value="72"/>
            <Setter Property="FontSize" Value="12"/>
          </Style>
          <Style x:Key="Fy1Switch.Settings.ListBoxItem" TargetType="ListBoxItem">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Padding" Value="13,9"/>
            <Setter Property="MinHeight" Value="54"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                  <Grid x:Name="Root" Background="{TemplateBinding Background}">
                    <Border BorderBrush="{DynamicResource Fy1Switch.SettingsBorderBrush}" BorderThickness="0,0,0,1"/>
                    <Border x:Name="SelectionMark" Width="3" HorizontalAlignment="Left"
                            Background="{DynamicResource Fy1Switch.AccentBrush}" Visibility="Collapsed"/>
                    <Border x:Name="FocusBorder" BorderBrush="Transparent" BorderThickness="1" Padding="{TemplateBinding Padding}">
                      <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                        VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Root" Property="Background" Value="{DynamicResource Fy1Switch.SubtleBrush}"/>
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Root" Property="Background" Value="{DynamicResource Fy1Switch.SettingsSelectionBrush}"/>
                      <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.SettingsSelectionForegroundBrush}"/>
                      <Setter TargetName="SelectionMark" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Root" Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="Fy1Switch.Settings.ListBox" TargetType="ListBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Background" Value="{DynamicResource Fy1Switch.SurfaceBrush}"/>
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Fy1Switch.SettingsBorderBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
            <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
            <Setter Property="ItemContainerStyle" Value="{StaticResource Fy1Switch.Settings.ListBoxItem}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ListBox">
                  <Border x:Name="Surface" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          CornerRadius="7" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                    <ScrollViewer x:Name="PART_ScrollViewer" Focusable="False" CanContentScroll="True">
                      <ItemsPresenter/>
                    </ScrollViewer>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="Fy1Switch.Settings.Expander" TargetType="Expander">
            <Setter Property="Foreground" Value="{DynamicResource Fy1Switch.ForegroundBrush}"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="MinHeight" Value="32"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Expander">
                  <StackPanel>
                    <ToggleButton x:Name="HeaderToggle" FocusVisualStyle="{x:Null}"
                                  Foreground="{TemplateBinding Foreground}" Background="Transparent"
                                  HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch" MinHeight="{TemplateBinding MinHeight}"
                                  Padding="{TemplateBinding Padding}" Cursor="Hand"
                                  Content="{TemplateBinding Header}" ContentTemplate="{TemplateBinding HeaderTemplate}"
                                  ContentTemplateSelector="{TemplateBinding HeaderTemplateSelector}"
                                  ContentStringFormat="{TemplateBinding HeaderStringFormat}"
                                  IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                      <ToggleButton.Template>
                        <ControlTemplate TargetType="ToggleButton">
                          <Border x:Name="HeaderBorder" Background="{TemplateBinding Background}" BorderBrush="Transparent"
                                  BorderThickness="0" CornerRadius="10" Padding="{TemplateBinding Padding}">
                            <Grid>
                              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                              <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                Margin="0,0,16,0" RecognizesAccessKey="True"/>
                              <Path x:Name="Chevron" Grid.Column="1" Data="M 0,0 L 5,5 L 0,10" Width="10" Height="10"
                                    Stroke="{DynamicResource Fy1Switch.SecondaryBrush}" StrokeThickness="1.4" StrokeLineJoin="Round"
                                    HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5">
                                <Path.RenderTransform><RotateTransform Angle="0"/></Path.RenderTransform>
                              </Path>
                            </Grid>
                          </Border>
                          <ControlTemplate.Triggers>
                            <Trigger Property="IsChecked" Value="True">
                              <Setter TargetName="Chevron" Property="RenderTransform"><Setter.Value><RotateTransform Angle="90"/></Setter.Value></Setter>
                            </Trigger>
                            <Trigger Property="IsMouseOver" Value="True">
                              <Setter TargetName="HeaderBorder" Property="Background" Value="{DynamicResource Fy1Switch.SubtleBrush}"/>
                            </Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter x:Name="ExpandSite" Visibility="Collapsed" ContentSource="Content"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
                  </StackPanel>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsExpanded" Value="True">
                      <Setter TargetName="ExpandSite" Property="Visibility" Value="Visible"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Opacity" Value="0.5"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="TextBox" BasedOn="{StaticResource Fy1Switch.Settings.TextBox}"/>
          <Style TargetType="PasswordBox" BasedOn="{StaticResource Fy1Switch.Settings.PasswordBox}"/>
          <Style TargetType="ListBox" BasedOn="{StaticResource Fy1Switch.Settings.ListBox}"/>
          <Style TargetType="ListBoxItem" BasedOn="{StaticResource Fy1Switch.Settings.ListBoxItem}"/>
          <Style TargetType="Expander" BasedOn="{StaticResource Fy1Switch.Settings.Expander}"/>
          <Style TargetType="ScrollViewer">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
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
            resources[SettingsInputBrush] = SystemColors.WindowBrush;
            resources[SettingsBorderBrush] = SystemColors.WindowTextBrush;
            resources[SettingsSelectionBrush] = SystemColors.HighlightBrush;
            resources[SettingsSelectionForegroundBrush] = SystemColors.HighlightTextBrush;
            resources[ButtonBrush] = SystemColors.ControlBrush;
            resources[ButtonHoverBrush] = resources[ButtonPressedBrush] = SystemColors.HighlightBrush;
            resources[ButtonActiveForegroundBrush] = SystemColors.HighlightTextBrush;
            resources[PrimaryButtonHoverBrush] = resources[PrimaryButtonPressedBrush] = SystemColors.HighlightBrush;
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
        Set(SettingsInputBrush, dark ? "#252D39" : "#FAFBFE");
        Set(SettingsBorderBrush, dark ? "#475365" : "#DFE4ED");
        Set(SettingsSelectionBrush, dark ? "#293C60" : "#F0F4FF");
        Set(SettingsSelectionForegroundBrush, dark ? "#F1F4F8" : "#1F2937");
        Set(ButtonBrush, dark ? "#303A49" : "#E2E8F2");
        Set(ButtonHoverBrush, dark ? "#465872" : "#C8D7EE");
        Set(ButtonPressedBrush, dark ? "#596E8D" : "#AFC5E6");
        Set(ButtonActiveForegroundBrush, dark ? "#FFFFFF" : "#17365F");
        Set(PrimaryButtonHoverBrush, dark ? "#BCD6FF" : "#224D9B");
        Set(PrimaryButtonPressedBrush, dark ? "#7BA4E2" : "#193B79");

        void Set(string key, string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            resources[key] = brush;
        }
    }
}
