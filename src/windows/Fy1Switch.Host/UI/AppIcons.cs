using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace Fy1Switch.Host.UI;

internal enum AppIconKind
{
    Monitor, MonitorOff, Question, Power, Settings, Back, Refresh, Check, Phone,
    Save, Scan, Link, Unlink, Copy, Revoke, Close, Minimize, Maximize, Restore
}

/// <summary>Single-color Fluent paths, rendered with the surrounding control's foreground.</summary>
internal sealed class VectorIcon : FrameworkElement
{
    private sealed record Artwork(double Size, Geometry[] Paths);
    private static readonly Dictionary<AppIconKind, Artwork> Cache = new();

    internal static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(AppIconKind), typeof(VectorIcon),
        new FrameworkPropertyMetadata(AppIconKind.Monitor, FrameworkPropertyMetadataOptions.AffectsRender));
    internal static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(VectorIcon), new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    internal AppIconKind Kind { get => (AppIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    internal Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    internal VectorIcon()
    {
        Width = Height = 20;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var artwork = Load(Kind);
        var scale = Math.Min(ActualWidth, ActualHeight) / artwork.Size;
        if (scale <= 0) return;
        context.PushTransform(new TranslateTransform((ActualWidth - artwork.Size * scale) / 2,
            (ActualHeight - artwork.Size * scale) / 2));
        context.PushTransform(new ScaleTransform(scale, scale));
        foreach (var path in artwork.Paths) context.DrawGeometry(Foreground, null, path);
        context.Pop();
        context.Pop();
    }

    private static Artwork Load(AppIconKind kind)
    {
        if (Cache.TryGetValue(kind, out var artwork)) return artwork;
        var (slug, size) = kind switch
        {
            AppIconKind.Monitor => ("desktop", 20),
            AppIconKind.MonitorOff => ("desktop_off", 20),
            AppIconKind.Question => ("question_circle", 20),
            AppIconKind.Power => ("power", 20),
            AppIconKind.Settings => ("settings", 20),
            AppIconKind.Back => ("arrow_left", 20),
            AppIconKind.Refresh => ("arrow_sync", 20),
            AppIconKind.Check => ("checkmark", 20),
            AppIconKind.Phone => ("phone", 20),
            AppIconKind.Save => ("save", 20),
            AppIconKind.Scan => ("scan", 20),
            AppIconKind.Link => ("link", 20),
            AppIconKind.Unlink => ("link_dismiss", 20),
            AppIconKind.Copy => ("copy", 20),
            AppIconKind.Revoke => ("shield_dismiss", 20),
            AppIconKind.Close => ("dismiss", 16),
            AppIconKind.Minimize => ("subtract", 16),
            AppIconKind.Maximize => ("square", 16),
            AppIconKind.Restore => ("square_multiple", 16),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var stream = Application.GetResourceStream(new Uri(
            $"/Fy1Switch.Host;component/Assets/Fluent/ic_fluent_{slug}_{size}_regular.svg", UriKind.Relative))!.Stream;
        var svg = XElement.Load(stream);
        var paths = svg.Elements().Select(path =>
        {
            var geometry = Geometry.Parse((path.Attribute("fill-rule")?.Value == "evenodd" ? "F0 " : "F1 ") + path.Attribute("d")!.Value);
            geometry.Freeze();
            return geometry;
        }).ToArray();
        artwork = new Artwork(size, paths);
        Cache.Add(kind, artwork);
        return artwork;
    }
}

internal static class AppIcons
{
    private sealed class IconLabel : StackPanel
    {
        internal readonly VectorIcon Icon = new() { Margin = new Thickness(0, 0, 8, 0) };
        internal readonly TextBlock Label = new() { VerticalAlignment = VerticalAlignment.Center };
        internal IconLabel()
        {
            Orientation = Orientation.Horizontal;
            Children.Add(Icon);
            Children.Add(Label);
        }
    }

    internal static void SetButton(Button button, string label, AppIconKind kind)
    {
        if (button.Content is not IconLabel content) button.Content = content = new IconLabel();
        content.Icon.Kind = kind;
        content.Label.Text = label;
        AutomationProperties.SetName(button, label);
    }
}
