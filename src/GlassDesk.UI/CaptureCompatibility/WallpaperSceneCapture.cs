using System.Diagnostics;
using System.Windows;
using GlassDesk.UI.Controls;

/// <summary>File/COM wallpaper sampling without D3D/WGC; this still runs in a helper.</summary>
internal sealed class WallpaperSceneCapture(ScreenRect region) : IDesktopSceneCapture
{
    private WallpaperBackdrop _wallpaper = new();
    private CapturePixels? _frame;
    private bool _delivered, _disposed;
    public nint CarrierHandle => 0;
    public bool AllowsStaticContent => true;
    public long SceneRevision => _frame?.SourceQpcTicks ?? 0;
    public bool UpdateExclusions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_wallpaper.Update(new Rect(region.X, region.Y, region.Width, region.Height), region.Width, region.Height, 0)) return false;
        var pixels = _wallpaper.Pixels ?? throw new InvalidOperationException("Wallpaper pixels unavailable.");
        _frame = new(region.Width, region.Height, (byte[])pixels.Clone())
        { SourceQpcTicks = Stopwatch.GetTimestamp(), IsStatic = true, Source = GlassBackdropSource.Wallpaper };
        _delivered = false;
        return true;
    }
    public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false)
    {
        cancellation.ThrowIfCancellationRequested();
        // Resume actually rereads the wallpaper source; it does not relabel old pixels.
        if (_frame is not null && _frame.SourceQpcTicks < sourceNotBeforeTicks) _wallpaper = new();
        UpdateExclusions();
        if (_delivered || _frame is null) return null;
        _delivered = true; return _frame;
    }
    public void ReleaseReadbackBuffers() { _frame = null; _wallpaper = new(); _delivered = false; }
    public void Dispose() { _disposed = true; _frame = null; }
}
