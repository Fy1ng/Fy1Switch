using System.Windows;
using System.Windows.Markup;

namespace RemoteDisplaySwitch.Host.UI;

internal static class DesktopTheme
{
    internal static ResourceDictionary CreateResources() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="Button">
            <Setter Property="Background" Value="#EEF2F8"/>
            <Setter Property="Foreground" Value="#24344D"/>
            <Setter Property="BorderBrush" Value="#D7DFEA"/>
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
                      <Setter TargetName="Surface" Property="BorderBrush" Value="#3974D2"/>
                      <Setter TargetName="Surface" Property="BorderThickness" Value="2"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Surface" Property="Opacity" Value="0.42"/>
                      <Setter Property="Cursor" Value="Arrow"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="Expander">
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Padding" Value="0,6"/>
          </Style>
          <Style TargetType="ListBox">
            <Setter Property="Background" Value="#F7F9FC"/>
            <Setter Property="BorderBrush" Value="#D7DFEA"/>
            <Setter Property="Padding" Value="6"/>
          </Style>
        </ResourceDictionary>
        """);
}
