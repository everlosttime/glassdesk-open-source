using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

public enum GlassBackdropSource { Transparent, Wallpaper, LiveDesktop, Opaque, CompatibleScene, Basic }

/// <summary>A fixed BGRA snapshot for one glass frame, independent of reused capture buffers.</summary>
internal sealed class GlassBackgroundFrame : IDisposable
{
    private IDisposable? _lease;
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public GlassBackdropSource Source { get; }
    public long Sequence { get; internal init; }
    public long CaptureGeneration { get; internal init; }
    public bool IsCaptured { get; internal init; }

    public GlassBackgroundFrame(byte[] pixels, int width, int height, GlassBackdropSource source)
        : this(pixels, width, height, source, false) { }

    internal GlassBackgroundFrame(byte[] pixels, int width, int height, GlassBackdropSource source, bool ownsPixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Invalid BGRA background frame.", nameof(pixels));
        Pixels = ownsPixels ? pixels : (byte[])pixels.Clone();
        Width = width;
        Height = height;
        Source = source;
    }

    // Only newly allocated buffers may be transferred; capture buffers still require a snapshot.
    internal static GlassBackgroundFrame FromOwnedPixels(byte[] pixels, int width, int height, GlassBackdropSource source)
        => new(pixels, width, height, source, true);

    internal static GlassBackgroundFrame FromCapture(BackdropFrame captured)
    {
        if (!MemoryMarshal.TryGetArray(captured.Pixels, out var segment) || segment.Offset != 0
            || segment.Count != segment.Array!.Length)
        {
            using (captured) return FromOwnedPixels(captured.Pixels.ToArray(), captured.Request.Width, captured.Request.Height, captured.Source);
        }
        return new(segment.Array, captured.Request.Width, captured.Request.Height, captured.Source, true)
            { _lease = captured, Sequence = captured.ContentRevision, CaptureGeneration = captured.Request.Generation, IsCaptured = true };
    }
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    internal static GlassBackgroundFrame FromExtensionLease(SamplePixelLease lease, GlassBackgroundFrame original, int width, int height)
        => new(lease.Pixels, width, height, original.Source, true)
        { _lease = lease, Sequence = original.Sequence, CaptureGeneration = original.CaptureGeneration, IsCaptured = original.IsCaptured };
}

/// <summary>Background stage: acquire self-excluded desktop pixels or a wallpaper fallback.</summary>
internal sealed class GlassBackgroundLayer : IDisposable
{
    internal sealed record CaptureTiming(long SourceTicks, long StartTicks, long EndTicks, object PixelStatistics);
    internal CaptureTiming? LastCaptureTiming { get; private set; }
    public static TimeSpan LiveFrameInterval => TimeSpan.FromMilliseconds(16);
    public static TimeSpan TransitionFrameInterval => LiveFrameInterval;
    private readonly SharedBackdropFeed _compatible = new();
    private readonly SharedBackdropFeed _wallpaperFeed = new((region, _, trace) =>
        new IsolatedDesktopSceneCapture(region, GlassBackdropSource.Wallpaper, trace));
    private long _pendingSince;
    private GlassBackgroundFrame? _basic;
    private SamplePixelPool _extensionPool = new();
    private bool _sleeping;
    internal bool RequiresBasicBackground { get; private set; }
    internal object InspectPerformance() => new
    { live = _compatible.InspectPerformance(), wallpaper = _wallpaperFeed.InspectPerformance() };

    public GlassBackgroundFrame? Capture(PixelRect region, int width, int height, double radius, bool allowDesktop)
    {
        if (GlassDesk.UI.BackdropRecovery.RequiresBasicRendering)
        {
            RequiresBasicBackground = true;
            _compatible.Suspend(); _wallpaperFeed.Suspend(); return Basic(width, height);
        }
        if (allowDesktop) _wallpaperFeed.Suspend();
        if (allowDesktop && _compatible.TryRead(region, width, height, out var captured)
            && captured is not null)
        {
            _pendingSince = 0; RequiresBasicBackground = false;
            LastCaptureTiming = Describe(captured);
            return GlassBackgroundFrame.FromCapture(captured);
        }

        // Tooltips/menus deliberately stop screen capture. Replay only the previous safe
        // snapshot at the identical coordinates while exclusion remains verified.
        // This never samples the popup and never stretches a stale frame after a move.
        // The feed is the authority for scene/generation validity. A failed read does
        // not authorize replay of a snapshot after an exclusion or resume boundary.

        // Pending or failed live capture is not a wallpaper-mode switch.
        // Dynamic wallpapers can report a solid black Windows wallpaper underneath
        // the visible desktop. Publishing it here makes both glass surfaces flash
        // opaque black before the first live frame. Null keeps completed material,
        // or the surface's transparent waiting body, until capture is ready.
        // The feed retries transient failures; retain completed material while it
        // recovers instead of replacing live wallpaper with its hidden black bitmap.
        if (allowDesktop)
        {
            if (_pendingSince == 0) _pendingSince = System.Diagnostics.Stopwatch.GetTimestamp();
            // A recoverable worker failure is still a pending replacement. Keep the
            // surface's already validated material until this same deadline expires;
            // retries must not start another deadline or clear a completed texture.
            RequiresBasicBackground = System.Diagnostics.Stopwatch.GetElapsedTime(_pendingSince) >= TimeSpan.FromSeconds(3);
            if (!RequiresBasicBackground) return null;
            return Basic(width, height);
        }

        _compatible.Suspend();
        if (_wallpaperFeed.TryRead(region, width, height, out var wallpaper) && wallpaper is not null)
        {
            RequiresBasicBackground = false;
            LastCaptureTiming = Describe(wallpaper);
            return GlassBackgroundFrame.FromCapture(wallpaper);
        }
        RequiresBasicBackground = true;
        return Basic(width, height);
    }

    private static CaptureTiming Describe(BackdropFrame frame)
    {
        var pixels = frame.Pixels.Span;
        var count = pixels.Length / 4; var step = Math.Max(1, count / 512);
        var samples = 0; var black = 0; var transparent = 0; long sum = 0;
        for (var pixel = 0; pixel < count; pixel += step)
        {
            var i = pixel * 4; var maximum = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            samples++; if (maximum <= 3) black++; if (pixels[i + 3] == 0) transparent++;
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        }
        return new(frame.SourceTicks, frame.StartTicks, frame.EndTicks, new
        { samples, nearBlackFraction = (double)black / Math.Max(1, samples),
            transparentFraction = (double)transparent / Math.Max(1, samples), meanChannel = (double)sum / Math.Max(1, samples * 3) });
    }

    private GlassBackgroundFrame Basic(int width, int height)
    {
        if (_basic is null || _basic.Width != width || _basic.Height != height)
        {
            var pixels = new byte[checked(width * height * 4)];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 40; pixels[i + 1] = 34; pixels[i + 2] = 30; pixels[i + 3] = 255; }
            _basic = GlassBackgroundFrame.FromOwnedPixels(pixels, width, height, GlassBackdropSource.Basic);
        }
        return _basic;
    }

    public void Suspend()
    {
        _compatible.Suspend();
        _wallpaperFeed.Suspend();
        _pendingSince = 0; RequiresBasicBackground = false;
        LastCaptureTiming = null;
    }

    public void SetPerformanceSleeping(bool sleeping)
    {
        _sleeping = sleeping;
        if (sleeping) { _extensionPool.Dispose(); _extensionPool = new(); }
        _compatible.SetPerformanceSleeping(sleeping);
        _wallpaperFeed.SetPerformanceSleeping(sleeping);
        _basic = null;
        LastCaptureTiming = null;
        _pendingSince = 0; RequiresBasicBackground = false;
    }

    public void Dispose()
    {
        _extensionPool.Dispose();
        _compatible.Dispose();
        _wallpaperFeed.Dispose();
        _basic = null;
        LastCaptureTiming = null;
        _pendingSince = 0; RequiresBasicBackground = false;
    }

    /// <summary>Extend only captured Primary pixels into the logical offscreen margin.</summary>
    internal static GlassBackgroundFrame ExtendCapture(GlassBackgroundFrame frame,
        PixelRect capturedRegion, PixelRect requestedRegion, int width, int height)
    {
        if (capturedRegion == requestedRegion && frame.Width == width && frame.Height == height)
            return frame;
        var pixels = new byte[checked(width * height * 4)];
        CopyExtendedPixels(frame, capturedRegion, requestedRegion, width, height, pixels);
        return new(pixels, width, height, frame.Source, true)
            { Sequence = frame.Sequence, CaptureGeneration = frame.CaptureGeneration, IsCaptured = frame.IsCaptured };
    }

    internal GlassBackgroundFrame? ExtendCapturedFrame(GlassBackgroundFrame frame,
        PixelRect capturedRegion, PixelRect requestedRegion, int width, int height)
    {
        if (_sleeping) return null;
        if (capturedRegion == requestedRegion && frame.Width == width && frame.Height == height) return frame;
        var lease = _extensionPool.TryRent(checked(width * height * 4));
        if (lease is null) return null;
        try
        {
            CopyExtendedPixels(frame, capturedRegion, requestedRegion, width, height, lease.Pixels);
            return GlassBackgroundFrame.FromExtensionLease(lease, frame, width, height);
        }
        catch { lease.Dispose(); throw; }
    }

    private static void CopyExtendedPixels(GlassBackgroundFrame frame,
        PixelRect capturedRegion, PixelRect requestedRegion, int width, int height, byte[] pixels)
    {
        var columns = ArrayPool<int>.Shared.Rent(width);
        try
        {
            for (var x = 0; x < width; x++)
            {
                var screenX = requestedRegion.X + (x + .5) * requestedRegion.Width / width;
                columns[x] = Math.Clamp((int)Math.Floor((screenX - capturedRegion.X)
                    * frame.Width / capturedRegion.Width), 0, frame.Width - 1);
            }
            var source = MemoryMarshal.Cast<byte, uint>(frame.Pixels.AsSpan());
            var target = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
            var previousSourceY = -1;
            for (var y = 0; y < height; y++)
            {
                var screenY = requestedRegion.Y + (y + .5) * requestedRegion.Height / height;
                var sourceY = Math.Clamp((int)Math.Floor((screenY - capturedRegion.Y)
                    * frame.Height / capturedRegion.Height), 0, frame.Height - 1);
                var row = target.Slice(y * width, width);
                if (sourceY == previousSourceY)
                    target.Slice((y - 1) * width, width).CopyTo(row);
                else
                    for (var x = 0; x < width; x++) row[x] = source[sourceY * frame.Width + columns[x]];
                previousSourceY = sourceY;
            }
        }
        finally { ArrayPool<int>.Shared.Return(columns); }
    }
}
