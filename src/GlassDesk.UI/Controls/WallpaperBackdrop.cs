using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Platform;

namespace GlassDesk.UI.Controls;

/// <summary>Stable wallpaper pixels only. Never reads the composed screen or another application's pixels.</summary>
internal sealed class WallpaperBackdrop
{
    private DesktopWallpaperSnapshot? _desktop;
    private DateTime _refreshAt;
    private int _revision;
    private sealed record WallpaperImage(BitmapSource Bitmap, int Width, int Height);
    private readonly Dictionary<string, WallpaperImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private (Rect Region, int Width, int Height, int Revision, double Radius) _last;
    public byte[]? Pixels { get; private set; }

    public bool Update(Rect region, int width, int height, double cornerRadius)
    {
        if (DateTime.UtcNow >= _refreshAt)
        {
            _refreshAt = DateTime.UtcNow.AddSeconds(3);
            if (_images.Values.Any(image => image is null))
            {
                _images.Clear();
                _last = default; // Retry temporarily unavailable files at the bounded refresh interval.
            }
            var desktop = DesktopWallpaperSource.TryRead();
            var hash = new HashCode();
            hash.Add(desktop?.Position);
            hash.Add(desktop?.BackgroundColor);
            foreach (var monitor in desktop?.Monitors ?? [])
            {
                hash.Add(monitor);
                hash.Add(GetWriteTime(monitor.Path));
            }
            var revision = hash.ToHashCode();
            if (revision != _revision) _images.Clear();
            _revision = revision;
            _desktop = desktop;
        }
        var key = (region, width, height, _revision, cornerRadius);
        if (key == _last) return false;
        _last = key;
        if (_desktop is null || _desktop.Monitors.Count == 0)
        {
            Pixels = null;
            return true;
        }
        var color = _desktop.BackgroundColor;
        var background = new SolidColorBrush(Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16)));
        var visual = new DrawingVisual();
        var unavailable = false;
        using (var context = visual.RenderOpen())
        {
            context.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            context.PushTransform(new MatrixTransform(width / region.Width, 0, 0, height / region.Height,
                -region.X * width / region.Width, -region.Y * height / region.Height));
            var virtualBounds = Rect.Empty;
            foreach (var monitor in _desktop.Monitors)
                virtualBounds.Union(new Rect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height));
            foreach (var monitor in _desktop.Monitors)
            {
                var bounds = new Rect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height);
                if (!bounds.IntersectsWith(region)) continue;
                var image = Load(monitor.Path);
                if (image is null)
                {
                    unavailable |= !string.IsNullOrWhiteSpace(monitor.Path);
                    continue;
                }
                var area = _desktop.Position == DesktopWallpaperPosition.Span ? virtualBounds : bounds;
                context.PushClip(new RectangleGeometry(bounds));
                var iw = image.Width; // Keep original physical dimensions even when decoding a smaller bitmap.
                var ih = image.Height;
                if (_desktop.Position == DesktopWallpaperPosition.Tile)
                {
                    var brush = new ImageBrush(image.Bitmap)
                    {
                        TileMode = TileMode.Tile,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(virtualBounds.X, virtualBounds.Y, iw, ih),
                        Stretch = Stretch.Fill
                    };
                    context.DrawRectangle(brush, null, bounds);
                }
                else
                {
                    var sx = area.Width / iw;
                    var sy = area.Height / ih;
                    var scale = _desktop.Position switch
                    {
                        DesktopWallpaperPosition.Center => 1,
                        DesktopWallpaperPosition.Fit => Math.Min(sx, sy),
                        _ => Math.Max(sx, sy)
                    };
                    var dest = _desktop.Position == DesktopWallpaperPosition.Stretch
                        ? area : new Rect(area.X + (area.Width - iw * scale) / 2,
                            area.Y + (area.Height - ih * scale) / 2, iw * scale, ih * scale);
                    context.DrawImage(image.Bitmap, dest);
                }
                context.Pop();
            }
            context.Pop();
            context.Pop();
        }
        if (unavailable)
        {
            Pixels = null;
            return true;
        }
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Pixels = new byte[width * height * 4];
        bitmap.CopyPixels(Pixels, width * 4, 0);
        return true;
    }

    private static DateTime GetWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return default; }
    }

    private WallpaperImage? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_images.TryGetValue(path, out var cached)) return cached;
        WallpaperImage? image = null;
        try
        {
            using var stream = File.OpenRead(path);
            var metadata = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var width = metadata.PixelWidth;
            var height = metadata.PixelHeight;
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * Math.Min(1, 2048.0 / Math.Max(width, height))));
            bitmap.EndInit();
            bitmap.Freeze();
            image = new WallpaperImage(bitmap, width, height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Runtime.InteropServices.COMException or ArgumentException or FileFormatException)
        {
            // Dynamic wallpaper engines and unavailable files retain native transparency.
        }
        _images[path] = image;
        return image;
    }
}

