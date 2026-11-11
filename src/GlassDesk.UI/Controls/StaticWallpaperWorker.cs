using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

/// <summary>One reusable STA, coalesced requests, no screen capture or product HWND sampling.</summary>
internal sealed class StaticWallpaperWorker : IDisposable
{
    internal sealed record Snapshot(Rect Region, int Width, int Height, byte[]? Pixels, string Source, string? Error);
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private sealed record RequestKey(Rect Region, int Width, int Height, Rect Capture, int CaptureWidth, int CaptureHeight);
    private RequestKey? _request;
    private readonly WallpaperBackdrop _wallpaper;
    private Snapshot? _snapshot;
    private long _nextPoll;
    private bool _stopped;

    public StaticWallpaperWorker() : this(new WallpaperBackdrop()) { }
    internal StaticWallpaperWorker(WallpaperBackdrop wallpaper)
    {
        _wallpaper = wallpaper;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlassDesk static wallpaper decode" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    public void Request(Rect region, int width, int height, Rect? capture = null, int captureWidth = 0, int captureHeight = 0)
    {
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0 || width <= 0 || height <= 0
            || (long)width * height > 1_000_000) throw new ArgumentOutOfRangeException(nameof(region));
        lock (_gate)
        {
            if (_stopped) return;
            var key = new RequestKey(region, width, height, capture ?? region,
                captureWidth > 0 ? captureWidth : width, captureHeight > 0 ? captureHeight : height);
            if (_request == key && Stopwatch.GetTimestamp() < _nextPoll) return;
            _request = key; _nextPoll = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
            _signal.Set();
        }
    }
    public Snapshot? Read(Rect region, int width, int height)
    {
        lock (_gate) return _snapshot is { } frame && frame.Region == region
            && frame.Width == width && frame.Height == height ? frame : null;
    }
    private void Run()
    {
        var wallpaper = _wallpaper;
        using var resize = new NativeFrameResize();
        RequestKey? processed = null;
        try
        {
            while (true)
            {
                _signal.WaitOne();
                RequestKey key;
                lock (_gate) { if (_stopped) return; key = _request!; }
                Snapshot? result = null;
                try
                {
                    var cw = checked((int)key.Capture.Width); var ch = checked((int)key.Capture.Height);
                    if ((long)cw * ch > 16_777_216) throw new NotSupportedException("Static decode exceeds the physical sampling budget.");
                    // Match legacy: physical WPF wallpaper composition, GDI COLORONCOLOR
                    // reduction, then Primary-edge extension. All happen only on change.
                    var changed = wallpaper.Update(key.Capture, cw, ch, 0);
                    if (changed || processed != key)
                    {
                        byte[]? pixels = null;
                        if (wallpaper.Pixels is { } physical)
                        {
                            var sample = resize.Resize(new CapturePixels(cw, ch, physical), key.CaptureWidth, key.CaptureHeight);
                            using var frame = GlassBackgroundFrame.FromOwnedPixels(sample, key.CaptureWidth, key.CaptureHeight, GlassBackdropSource.Wallpaper);
                            using var extended = GlassBackgroundLayer.ExtendCapture(frame,
                                new PixelRect((int)key.Capture.X, (int)key.Capture.Y,cw,ch),
                                new PixelRect((int)key.Region.X,(int)key.Region.Y,(int)key.Region.Width,(int)key.Region.Height),key.Width,key.Height);
                            pixels = extended.Pixels;
                        }
                        result = new(key.Region, key.Width, key.Height, pixels, wallpaper.SourceKind,
                            pixels is null ? "Static wallpaper unavailable" : null);
                        processed = key;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException
                    or InvalidOperationException or ArgumentException or NotSupportedException)
                { result = new(key.Region, key.Width, key.Height, null, "Unavailable", error.Message); }
                lock (_gate)
                {
                    if (_stopped) return;
                    if (_request == key && result is not null) _snapshot = result;
                }
            }
        }
        finally { _signal.Dispose(); }
    }
    public void Dispose()
    {
        lock (_gate) { if (_stopped) return; _stopped = true; _snapshot = null; _signal.Set(); }
    }
}
