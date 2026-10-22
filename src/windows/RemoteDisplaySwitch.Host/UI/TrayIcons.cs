using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Host.UI;

internal sealed class TrayIcons : IDisposable
{
    private readonly Icon _on = Create(DisplayPower.On);
    private readonly Icon _off = Create(DisplayPower.Off);
    private readonly Icon _unknown = Create(DisplayPower.Unknown);

    internal Icon For(DisplayPower power) => power switch
    {
        DisplayPower.On => _on,
        DisplayPower.Off => _off,
        _ => _unknown
    };

    private static Icon Create(DisplayPower power)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        var color = SystemInformation.HighContrast ? SystemColors.WindowText : power switch
        {
            DisplayPower.On => Color.FromArgb(64, 201, 156),
            DisplayPower.Off => Color.FromArgb(139, 171, 240),
            _ => Color.FromArgb(244, 183, 75)
        };
        using var pen = new Pen(color, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawRectangle(pen, 3.5f, 4.5f, 25, 18);
        graphics.DrawLine(pen, 16, 23, 16, 27);
        graphics.DrawLine(pen, 10, 28, 22, 28);
        if (power == DisplayPower.On)
        {
            using var fill = new SolidBrush(color);
            graphics.FillRectangle(fill, 8, 9, 16, 9);
        }
        else if (power == DisplayPower.Off)
            graphics.DrawLine(pen, 10, 14, 22, 14);
        else
        {
            graphics.DrawArc(pen, 12, 8, 8, 7, 180, 245);
            graphics.DrawLine(pen, 16, 14, 16, 15);
            using var fill = new SolidBrush(color);
            graphics.FillEllipse(fill, 14.6f, 18, 2.8f, 2.8f);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { _ = DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _on.Dispose();
        _off.Dispose();
        _unknown.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
