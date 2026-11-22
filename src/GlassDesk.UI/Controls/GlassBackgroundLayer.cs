using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDesk.Core.Models;
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
    private readonly SharedBackdropFeed _compatible;
    private readonly SharedBackdropFeed _wallpaperFeed = new((region, _, trace) =>
        new IsolatedDesktopSceneCapture(region, GlassBackdropSource.Wallpaper, trace));
    private readonly SharedBackdropFeed _desktopOnlyFeed = new((region, _, trace) =>
        DesktopSceneCaptureFactory.CreateDesktopOnly(region, trace));
    private bool _desktopOnly;
    private long _pendingSince;
    private long _neutralFillSince;
    private byte[]? _backdropLuma;
    private long _brightFrameSince;
    private const uint NeutralDesktopFill = 0x00b2b2b2;
    private static readonly TimeSpan NeutralFillConfirmation = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StatusBarBrightnessConfirmation = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan LauncherBrightnessConfirmation = TimeSpan.FromMilliseconds(240);
    private GlassBackgroundFrame? _basic;
    private SamplePixelPool _extensionPool = new();
    private bool _sleeping;
    private readonly bool _useOpaqueCaptureFallback;
    private bool _rejectedWallpaper;
    private bool _usingLiveCapture;
    internal GlassBackgroundLayer() : this(WindowsCapabilities.BuildNumber) { }
    internal GlassBackgroundLayer(int windowsBuild, SharedBackdropFeed? compatible = null)
    {
        _useOpaqueCaptureFallback = windowsBuild >= 22000;
        _compatible = compatible ?? new();
    }
    internal bool RequiresBasicBackground { get; private set; }
    internal bool UsesTranslucentCaptureFallback => _desktopOnly || !_useOpaqueCaptureFallback;
    internal bool MustDiscardUnverifiedMaterial => _usingLiveCapture && UsesTranslucentCaptureFallback
        && (_rejectedWallpaper || (_desktopOnly ? _desktopOnlyFeed.Failure is not null
            : _compatible.Failure is not null));
    internal object InspectPerformance() => new
    { live = _compatible.InspectPerformance(), wallpaper = _wallpaperFeed.InspectPerformance(),
        desktopOnly = _desktopOnlyFeed.InspectPerformance(), desktopOnlySelected = _desktopOnly };

    internal void SetDisplayCadence(double? targetHertz)
    {
        _compatible.SetDisplayCadence(targetHertz);
        _wallpaperFeed.SetDisplayCadence(targetHertz);
        _desktopOnlyFeed.SetDisplayCadence(targetHertz);
    }

    internal void ObserveContentChanges(Action changed)
    {
        _compatible.ContentChanged = changed;
        _wallpaperFeed.ContentChanged = changed;
        _desktopOnlyFeed.ContentChanged = changed;
    }

    public GlassBackgroundFrame? Capture(PixelRect region, int width, int height, double radius, bool allowDesktop,
        bool desktopOnly = false)
    {
        if (_desktopOnly != desktopOnly) { Suspend(); _desktopOnly = desktopOnly; }
        _usingLiveCapture=allowDesktop || desktopOnly;
        if (GlassDesk.UI.BackdropRecovery.RequiresBasicRendering)
        {
            RequiresBasicBackground = true;
            _compatible.Suspend(); _wallpaperFeed.Suspend(); _desktopOnlyFeed.Suspend(); return Basic(width, height);
        }
        if (desktopOnly)
        {
            _compatible.Suspend(); _wallpaperFeed.Suspend();
            RequiresBasicBackground = false;
            if (_desktopOnlyFeed.TryRead(region, width, height, out var desktop) && desktop is not null)
            {
                // Recovery can supply the Windows wallpaper file/color after live
                // readback fails. Dynamic wallpaper providers often leave a black
                // backing image there; it is not the visible desktop. Reject by
                // provenance, so real black desktop frames remain valid.
                if (desktop.Source == GlassBackdropSource.Wallpaper)
                {
                    desktop.Dispose();
                    _rejectedWallpaper = true;
                    LastCaptureTiming = null;
                    return null;
                }
                _rejectedWallpaper = false;
                LastCaptureTiming = Describe(desktop);
                return GlassBackgroundFrame.FromCapture(desktop);
            }
            return null; // Await desktop pixels; never fall back to the composed screen.
        }
        _desktopOnlyFeed.Suspend();
        if (allowDesktop) _wallpaperFeed.Suspend();
        if (allowDesktop && _compatible.TryRead(region, width, height, out var captured)
            && captured is not null)
        {
            if(_useOpaqueCaptureFallback || captured.Source!=GlassBackdropSource.Wallpaper)
            {
                _rejectedWallpaper=false;
                _pendingSince = 0; RequiresBasicBackground = false;
                LastCaptureTiming = Describe(captured);
                return GlassBackgroundFrame.FromCapture(captured);
            }
            // Recovery may have sampled the hidden static wallpaper underneath a
            // dynamic desktop. That is not a validated live scene on Windows 10.
            // Release its reader lease and use the agreed translucent fallback.
            captured.Dispose();
            _rejectedWallpaper=true;
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
            // Win10 may have neither a working offscreen WGC scene nor readable
            // wallpaper metadata. Expiring a reveal snapshot must then return to
            // ambient glass, rather than turning the Dock into an opaque black bar.
            RequiresBasicBackground = _useOpaqueCaptureFallback
                && System.Diagnostics.Stopwatch.GetElapsedTime(_pendingSince) >= TimeSpan.FromSeconds(3);
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
        ResetBackdropConfirmation();
        _rejectedWallpaper=false;
        _compatible.Suspend();
        _wallpaperFeed.Suspend();
        _desktopOnlyFeed.Suspend();
        _pendingSince = 0; RequiresBasicBackground = false;
        LastCaptureTiming = null;
    }

    public void SetPerformanceSleeping(bool sleeping)
    {
        ResetBackdropConfirmation();
        _rejectedWallpaper=false;
        _sleeping = sleeping;
        if (sleeping) { _extensionPool.Dispose(); _extensionPool = new(); }
        _compatible.SetPerformanceSleeping(sleeping);
        _wallpaperFeed.SetPerformanceSleeping(sleeping);
        _desktopOnlyFeed.SetPerformanceSleeping(sleeping);
        _basic = null;
        LastCaptureTiming = null;
        _pendingSince = 0; RequiresBasicBackground = false;
    }

    public void Dispose()
    {
        ResetBackdropConfirmation();
        _extensionPool.Dispose();
        _compatible.Dispose();
        _wallpaperFeed.Dispose();
        _desktopOnlyFeed.Dispose();
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
        if (capturedRegion == requestedRegion && frame.Width == width && frame.Height == height)
            return ShouldHoldNeutralFill(frame.Source, frame.Pixels) ? null : frame;
        var lease = _extensionPool.TryRent(checked(width * height * 4));
        if (lease is null) return null;
        try
        {
            CopyExtendedPixels(frame, capturedRegion, requestedRegion, width, height, lease.Pixels);
            if (!ShouldHoldNeutralFill(frame.Source, lease.Pixels))
                return GlassBackgroundFrame.FromExtensionLease(lease, frame, width, height);
            lease.Dispose();
            return null;
        }
        catch { lease.Dispose(); throw; }
    }

    private void ResetBackdropConfirmation()
    {
        _neutralFillSince = 0;
        _backdropLuma = null;
        _brightFrameSince = 0;
    }

    internal bool ShouldHoldBrightBackdropFrame(GlassMaterialTarget target, GlassBackgroundFrame frame,
        GlassCaptureGeometry geometry, long now, bool samePose)
    {
        if (frame.Source is not (GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene))
        {
            _backdropLuma = null;
            _brightFrameSince = 0;
            return false;
        }

        const int columns = 16, rows = 8;
        Span<byte> sample = stackalloc byte[columns * rows];
        var bounds = geometry.GlassBoundsDip;
        var scaleX = frame.Width / geometry.SampleSizeDip.Width;
        var scaleY = frame.Height / geometry.SampleSizeDip.Height;
        var baseline = samePose ? _backdropLuma : null;
        var brighter = 0;
        for (var row = 0; row < rows; row++)
        {
            var y = Math.Clamp((int)((bounds.Y + (row + .5) * bounds.Height / rows) * scaleY), 0, frame.Height - 1);
            for (var column = 0; column < columns; column++)
            {
                var x = Math.Clamp((int)((bounds.X + (column + .5) * bounds.Width / columns) * scaleX), 0, frame.Width - 1);
                var pixel = (y * frame.Width + x) * 4;
                var luma = (54 * frame.Pixels[pixel + 2] + 183 * frame.Pixels[pixel + 1] + 19 * frame.Pixels[pixel]) >> 8;
                var index = row * columns + column;
                sample[index] = (byte)luma;
                if (baseline is not null && luma - baseline[index] >= 18) brighter++;
            }
        }

        // Readback can briefly brighten only the area under a glass panel.
        // Keep the previous frame while checking that the change persists.
        if (brighter >= sample.Length / 4)
        {
            if (_brightFrameSince == 0) _brightFrameSince = now;
            var launcher = target == GlassMaterialTarget.Launcher;
            var confirmation = launcher ? LauncherBrightnessConfirmation : StatusBarBrightnessConfirmation;
            if (Stopwatch.GetElapsedTime(_brightFrameSince, now) < confirmation)
            {
                var reason = launcher
                    ? "TransientLauncherBrightFrameHeld" : "TransientStatusBarBrightFrameHeld";
                BackdropRecovery.Record(new("BackgroundCapture", reason, now));
                return true;
            }
        }

        _brightFrameSince = 0;
        sample.CopyTo(_backdropLuma ??= new byte[sample.Length]);
        return false;
    }

    private bool ShouldHoldNeutralFill(GlassBackdropSource source, byte[] bytes)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        var step = Math.Max(1, pixels.Length / 701);
        var sampled = (pixels.Length - 1) / step + 1;
        var fill = 0;
        if (source is GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene)
            for (var index = 0; index < pixels.Length; index += step)
                if ((pixels[index] & 0x00ffffff) == NeutralDesktopFill) fill++;
        // A partial compositor fill can cover 38% of the input for a panel
        // while the remaining crop, including its overscan, still has detail.
        if (fill * 10 < sampled * 3)
        {
            _neutralFillSince = 0;
            return false;
        }

        // Keep the current material across a transient RGB 178 desktop fill.
        // Accept persistent colors after the confirmation window.
        var now = Stopwatch.GetTimestamp();
        if (_neutralFillSince == 0) _neutralFillSince = now;
        if (Stopwatch.GetElapsedTime(_neutralFillSince, now) >= NeutralFillConfirmation) return false;
        var reason = fill == sampled ? "TransientNeutralFillHeld" : "TransientPartialNeutralFillHeld";
        BackdropRecovery.Record(new("BackgroundCapture", reason, now));
        return true;
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
