using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Fy1Switch.Host.UI;

internal static class SettingsUi
{
    internal static Border Card(UIElement content)
    {
        var card = new Border
        {
            Child = content, Padding = new Thickness(22, 18, 22, 20),
            Margin = new Thickness(0, 0, 0, 16), CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, DesktopTheme.SurfaceBrush);
        card.SetResourceReference(Border.BorderBrushProperty, DesktopTheme.SettingsBorderBrush);
        if (content is Expander section)
        {
            // The header owns the card's whitespace so its complete surface can toggle.
            var padding = card.Padding;
            card.Padding = new Thickness(0);
            section.Padding = padding;
            section.MinHeight = 32 + padding.Top + padding.Bottom;
            var body = (UIElement)section.Content;
            section.Content = null;
            section.Content = new Border
            {
                Padding = new Thickness(padding.Left, 0, padding.Right, padding.Bottom),
                Child = body
            };
        }
        return card;
    }

    internal static Grid Heading(string title, AppIconKind icon, FrameworkElement? actions = null)
    {
        var heading = new Grid { MinHeight = 32 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new VectorIcon { Kind = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0) };
        glyph.SetResourceReference(VectorIcon.ForegroundProperty, DesktopTheme.SecondaryBrush);
        label.Children.Add(glyph);
        label.Children.Add(Label(title, 16, bold: true));
        heading.Children.Add(label);
        if (actions is not null)
        {
            Grid.SetColumn(actions, 1);
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            actions.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(actions);
        }
        return heading;
    }

    internal static TextBlock Label(string text, double size = 13, bool secondary = false, bool bold = false)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, secondary ? DesktopTheme.SecondaryBrush : DesktopTheme.ForegroundBrush);
        return label;
    }

    internal static TextBlock Hint(string text)
    {
        var hint = Label(text, 12, secondary: true);
        hint.Margin = new Thickness(0, 8, 0, 0);
        return hint;
    }

    internal static Border Separator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 18, 0, 16) };
        line.SetResourceReference(Border.BackgroundProperty, DesktopTheme.BorderBrush);
        return line;
    }

    internal static void Button(Button button, bool primary = false)
    {
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "Fy1Switch.PrimaryButton" : "Fy1Switch.Settings.Button");
        button.MinWidth = 0;
        button.MinHeight = 32;
        button.FontSize = 12;
        button.Padding = new Thickness(12, 6, 12, 6);
        button.Margin = new Thickness(8, 0, 0, 0);
    }

    internal static void List(ListBox list)
    {
        list.SetResourceReference(FrameworkElement.StyleProperty, "Fy1Switch.Settings.ListBox");
        list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Fy1Switch.Settings.ListBoxItem");
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
    }

    internal static FrameworkElement Field(string title, Control input)
    {
        var field = new StackPanel();
        field.Children.Add(Label(title, 12, secondary: true));
        input.Margin = new Thickness(0, 6, 0, 0);
        input.MinHeight = 34;
        AutomationProperties.SetName(input, title);
        if (input is TextBox or PasswordBox)
        {
            input.SetResourceReference(FrameworkElement.StyleProperty,
                input is PasswordBox ? "Fy1Switch.Settings.PasswordBox" : "Fy1Switch.Settings.TextBox");
            input.PreviewMouseWheel += ScrollPage;
        }
        field.Children.Add(input);
        return field;
    }

    internal static Grid Fields(string firstTitle, Control first, string secondTitle, Control second, double secondWidth = 120)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(secondWidth) });
        row.Children.Add(Field(firstTitle, first));
        var right = Field(secondTitle, second);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }

    internal static Grid DeviceRow(string name, string detail, AppIconKind icon, string? badge = null)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new VectorIcon { Kind = icon, Width = 18, Height = 18 };
        glyph.SetResourceReference(VectorIcon.ForegroundProperty, DesktopTheme.SecondaryBrush);
        var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Child = glyph, Margin = new Thickness(0, 0, 12, 0) };
        tile.SetResourceReference(Border.BackgroundProperty, DesktopTheme.SubtleBrush);
        row.Children.Add(tile);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Label(name, 13, bold: true);
        title.ClearValue(TextBlock.ForegroundProperty);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        labels.Children.Add(title);
        var subtitle = Label(detail, 11, secondary: true);
        subtitle.ClearValue(TextBlock.ForegroundProperty);
        subtitle.Margin = new Thickness(0, 2, 0, 0);
        labels.Children.Add(subtitle);
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        if (badge is not null)
        {
            var status = new Border
            {
                Child = Label(badge, 11, secondary: true), Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            status.SetResourceReference(Border.BackgroundProperty, DesktopTheme.SubtleBrush);
            Grid.SetColumn(status, 2);
            row.Children.Add(status);
        }
        AutomationProperties.SetName(row, name + ", " + detail);
        return row;
    }

    private static void ScrollPage(object sender, MouseWheelEventArgs args)
    {
        // Route wheel input before a single-line field's internal viewer consumes it.
        for (var parent = VisualTreeHelper.GetParent((Control)sender); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer page) continue;
            args.Handled = true;
            page.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta) { RoutedEvent = Mouse.MouseWheelEvent });
            return;
        }
    }
}
