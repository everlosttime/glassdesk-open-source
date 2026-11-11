using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Platform;

namespace GlassDesk.UI.Controls;

/// <summary>Stable wallpaper pixels only. Never reads the composed screen or another application's pixels.</summary>
internal sealed class WallpaperBackdrop
{
    private readonly Func<string, int, string?> _cachedWallpaper;
    private readonly Func<DesktopWallpaperSnapshot?> _readDesktop;
    internal string SourceKind { get; private set; } = "Unavailable";

    internal WallpaperBackdrop(Func<string, int, string?>? cachedWallpaper = null,
        Func<DesktopWallpaperSnapshot?>? readDesktop = null)
    {
        _cachedWallpaper = cachedWallpaper ?? new WindowsWallpaperCache().Resolve;
        _readDesktop = readDesktop ?? DesktopWallpaperSource.TryRead;
    }
    private DesktopWallpaperSnapshot? _desktop;
    private DateTime _refreshAt;
    private int _revision;
    private sealed record WallpaperImage(BitmapSource Bitmap, int Width, int Height);
    private readonly Dictionary<string, WallpaperImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private (Rect Region, int Width, int Height, int Revision, double Radius) _last;
    public byte[]? Pixels { get; private set; }

    // A fixed-region capture worker retains the completed BGRA pixels. Keeping
    // the decoded monitor images too duplicates their backing WIC storage.
    // Preserve failed entries: their presence drives the bounded retry below.
    internal void ReleaseDecodedImages()
    {
        foreach (var path in _images.Where(entry => entry.Value is not null).Select(entry => entry.Key).ToArray())
            _images.Remove(path);
    }

    public bool Update(Rect region, int width, int height, double cornerRadius, bool renderPixels = true)
    {
        if (DateTime.UtcNow >= _refreshAt)
        {
            _refreshAt = DateTime.UtcNow.AddSeconds(3);
            if (_images.Values.Any(image => image is null))
            {
                _images.Clear();
                _last = default; // Retry temporarily unavailable files at the bounded refresh interval.
            }
            var desktop = _readDesktop();
            var hash = new HashCode();
            hash.Add(desktop?.Position);
            hash.Add(desktop?.BackgroundColor);
            foreach (var monitor in desktop?.Monitors ?? [])
            {
                hash.Add(monitor);
                hash.Add(GetWriteTime(monitor.Path));
                if (!string.IsNullOrWhiteSpace(monitor.Path) && !File.Exists(monitor.Path))
                {
                    var cachedPath = _cachedWallpaper(monitor.Path, desktop!.Monitors.Count);
                    hash.Add(cachedPath);
                    if (cachedPath is not null) hash.Add(FileState(cachedPath));
                }
            }
            var revision = hash.ToHashCode();
            if (revision != _revision) _images.Clear();
            _revision = revision;
            _desktop = desktop;
        }
        if (!renderPixels)
        {
            // A fully covering live source supplies every output pixel. Keep the
            // metadata polling/retry policy, but build static pixels only when a
            // gap or source removal makes them necessary again.
            Pixels = null;
            _last = default;
            if (_images.Count != 0) ReleaseDecodedImages();
            return false;
        }
        var key = (region, width, height, _revision, cornerRadius);
        if (key == _last) return false;
        _last = key;
        SourceKind = "Unavailable";
        if (_desktop is null || _desktop.Monitors.Count == 0)
        {
            Pixels = null;
            return true;
        }
        var color = _desktop.BackgroundColor;
        var background = new SolidColorBrush(Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16)));
        var visual = new DrawingVisual();
        var unavailable = false;
        var usedOriginal = false;
        var usedCache = false;
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
                if (image is null && !string.IsNullOrWhiteSpace(monitor.Path))
                {
                    image = LoadCached(monitor.Path, _desktop.Monitors.Count);
                    usedCache |= image is not null;
                }
                else usedOriginal |= image is not null;
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
        SourceKind = usedCache ? "DecodedWindowsCache" : usedOriginal ? "DecodedOriginal" : "SolidColor";
        return true;
    }

    private WallpaperImage? LoadCached(string originalPath, int monitorCount)
    {
        var cachedPath = _cachedWallpaper(originalPath, monitorCount);
        if (cachedPath is null) return null;
        var before = FileState(cachedPath);
        if (before.Length is <= 0 or > 32 * 1024 * 1024) return null;
        var image = Load(cachedPath);
        if (before != FileState(cachedPath)
            || !string.Equals(cachedPath, _cachedWallpaper(originalPath, monitorCount), StringComparison.OrdinalIgnoreCase))
        {
            _images.Remove(cachedPath);
            return null;
        }
        return image;
    }

    private static (long Length, DateTime Modified) FileState(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException) { return default; }
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
            if (width <= 0 || height <= 0 || (long)width * height > 64_000_000)
                throw new NotSupportedException("Wallpaper dimensions exceed the decode budget.");
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

