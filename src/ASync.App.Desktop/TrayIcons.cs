using ASync.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ASync.App.Desktop;

/// <summary>
/// The tray and window icons, drawn at start-up rather than shipped as files: the logo (fuchsia rounded square with the
/// sync arrows) plus a status dot: green (up to date), white with a fuchsia ring (syncing), red with a bar (a problem).
/// Fixed colours, not the theme's: the tray's background is the desktop's, not ours.
/// </summary>
internal static class TrayIcons
{
    private const string Glyph = "M12 4a8 8 0 0 1 7.4 5h-2.2A6 6 0 0 0 7.3 8.7L9.6 11H3.5V4.9l2.4 2.4A8 8 0 0 1 12 4zM20.5 13v6.1l-2.4-2.4A8 8 0 0 1 4.6 15h2.2a6 6 0 0 0 9.9 1.3L14.4 13h6.1z";
    private static readonly Dictionary<int, WindowIcon> Cache = [];

    private static int Key(TrayStatus? status) => status is { } s ? (int)s : -1;

    public static WindowIcon For(TrayStatus? status)
    {
        if (Cache.TryGetValue(Key(status), out var icon)) return icon;
        using var bitmap = Render(status, 64);
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return Cache[Key(status)] = new WindowIcon(png);
    }

    /// <summary>The icon as a bitmap (<paramref name="status"/> null: the plain logo of the window).</summary>
    public static RenderTargetBitmap Render(TrayStatus? status, int size)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var dc = bitmap.CreateDrawingContext())
        {
            var s = size / 64.0;
            var logo = new Rect(2 * s, 2 * s, 60 * s, 60 * s);
            dc.DrawRectangle(new SolidColorBrush(Color.Parse("#a21caf")), null, new RoundedRect(logo, 14 * s));
            var glyph = Geometry.Parse(Glyph);
            using (dc.PushTransform(Matrix.CreateScale(2.1 * s, 2.1 * s) * Matrix.CreateTranslation(6.8 * s, 6.8 * s)))
                dc.DrawGeometry(Brushes.White, null, glyph);
            if (status is { } st)
            {
                var center = new Point(50 * s, 50 * s);
                var ring = new Pen(new SolidColorBrush(Color.Parse("#2a1530")), 3 * s);
                switch (st)
                {
                    case TrayStatus.Ok:
                        dc.DrawEllipse(new SolidColorBrush(Color.Parse("#22c55e")), ring, center, 12 * s, 12 * s);
                        break;
                    case TrayStatus.Syncing:
                        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.Parse("#d946ef")), 5 * s), center, 11 * s, 11 * s);
                        break;
                    default:
                        dc.DrawEllipse(new SolidColorBrush(Color.Parse("#ef4444")), ring, center, 12 * s, 12 * s);
                        dc.DrawRectangle(Brushes.White, null, new Rect(center.X - 2 * s, center.Y - 7 * s, 4 * s, 9 * s));
                        dc.DrawRectangle(Brushes.White, null, new Rect(center.X - 2 * s, center.Y + 3.5 * s, 4 * s, 4 * s));
                        break;
                }
            }
        }
        return bitmap;
    }
}
