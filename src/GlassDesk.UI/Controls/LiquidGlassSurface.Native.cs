using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDesk.Core.Models;
using GlassDesk.Rendering;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;

public sealed partial class LiquidGlassSurface
{
    // Only the production Dock explicitly opts in. This is deliberately not a saved default.
    public bool EnableNativeStaticWallpaper { get; set; }
    private NativeD3DImageBridge? _nativeBridge;
    private NativeSessionRecovery? _nativeSession;
    private StaticWallpaperWorker? _nativeWallpaper;
    private StaticWallpaperWorker.Snapshot? _nativeUploaded;
    private StaticGlassMaterial? _nativeMaterial;
    private Rect _nativeBounds;
    private Rect _nativeRegion;
    private bool _nativeReady, _nativeDisabled;
    private string _nativeState = "Legacy";
    private ulong _nativeGeneration;
    private long _nativePendingSince;

    private bool NativeStaticSelected => EnableNativeStaticWallpaper && !_nativeDisabled
        && string.Equals(Environment.GetEnvironmentVariable("GLASSDESK_DOCK_RENDERER"), "native-static", StringComparison.Ordinal)
        && !IsPlainGlass && ThemeService.Current?.UseOpaquePanels != true && !BackdropRecovery.RequiresBasicRendering
        && Material is GlassMaterialMode.AndroidLiquidGlass or GlassMaterialMode.FrostedGlass
        && LiquidGlassDebugState.View == LiquidGlassDebugView.None
        && Window.GetWindow(this) is GlassWindow { SurfaceMode: GlassSurfaceMode.SelfDrawn, UsesDesktopOnlyBackdrop: false };

    private bool TryNativeStaticWallpaper(GlassWindow owner, Rect region, Rect clipped, int captureWidth, int captureHeight,
        int width, int height, Rect bounds, double strength)
    {
        if (!NativeStaticSelected) { ReleaseNativeStatic(); return false; }
        try
        {
            if (_nativeSession is null)
            {
                _nativeSession = NativeSessionRecovery.TryBegin(Path.Combine(AppContext.BaseDirectory, "Data", "recovery", "native-static"));
                if (_nativeSession is null) throw new InvalidOperationException("Native disabled after an unclean session.");
            }
            _background.Suspend(); _worker.Invalidate(); _worker.ClearCachedBuffers();
            _frame = null; _transitionBackdrop = null; _compatibilityBackdrop = null; _lastSource = null;
            _wallpaperCancellation?.Cancel(); _wallpaperCancellation = null;
            _nativeWallpaper ??= new();
            if (_nativeRegion != region)
            { _nativeRegion = region; _nativeReady = false; _nativeUploaded = null; _nativeMaterial = null; _nativePendingSince = Stopwatch.GetTimestamp(); }
            _nativeWallpaper.Request(region, width, height, clipped, captureWidth, captureHeight);
            var snapshot = _nativeWallpaper.Read(region, width, height);
            if (snapshot?.Error is { } decodeError) throw new InvalidOperationException(decodeError);
            _nativeBridge ??= new(new System.Windows.Interop.WindowInteropHelper(owner).Handle, width, height);
            if (_nativePendingSince == 0) _nativePendingSince = Stopwatch.GetTimestamp();
            if (_nativeBridge.IsFallbackRequired) throw new InvalidOperationException(_nativeBridge.LastError);
            var stats = _nativeBridge.Statistics;
            if (stats.Width != width || stats.Height != height)
            {
                _transitionLayer.SetBackdrop(null);
                _nativeBridge.Resize(width, height); _nativeReady = false;
                _nativeUploaded = null; _nativeMaterial = null;
                stats = _nativeBridge.Statistics;
            }
            if (_nativeGeneration != stats.Generation)
            {
                _nativeGeneration = stats.Generation; _nativeUploaded = null; _nativeMaterial = null; _nativeReady = false;
            }
            if (_nativeBounds != bounds) { _nativeReady = false; _nativeMaterial = null; }
            _nativeBounds = bounds;
            if (snapshot?.Pixels is not null)
            {
                if (!ReferenceEquals(_nativeUploaded, snapshot))
                {
                    _nativeBridge.UploadWallpaper(snapshot.Pixels, width, height);
                    _nativeUploaded = snapshot; _nativeMaterial = null;
                }
                var material = new StaticGlassMaterial((float)bounds.Width, (float)bounds.Height, (float)strength,
                    (float)BackgroundClarity, (float)AndroidLiquidGlassRenderer.BodyOpacity,
                    Material == GlassMaterialMode.FrostedGlass, ThemeService.Current?.IsDark != false);
                if (_nativeMaterial != material)
                {
                    var before = _nativeBridge.Statistics.SubmittedFrames;
                    var displayed = _nativeBridge.TryRenderMaterial(material);
                    if (_nativeBridge.Statistics.SubmittedFrames > before)
                    { _nativeMaterial = material; _nativePendingSince = Stopwatch.GetTimestamp(); }
                    _nativeReady |= displayed;
                }
                else _nativeReady |= _nativeBridge.TryPresent();
            }
            if (_nativeBridge.IsFallbackRequired) throw new InvalidOperationException(_nativeBridge.LastError);
            if (!_nativeReady && _nativePendingSince != 0 && Stopwatch.GetElapsedTime(_nativePendingSince).TotalSeconds > 3)
                throw new InvalidOperationException("Static GPU frame did not become available within 3 seconds.");
            _nativeState = _nativeReady ? "NativeStaticWallpaper" : "NativePending";
            BackdropSource = _nativeReady ? GlassBackdropSource.Wallpaper : GlassBackdropSource.Transparent;
            SetTimerInterval(TimeSpan.FromMilliseconds(_nativeReady ? 100 : 16));
            UpdateTransitionLayer(); InvalidateArrange(); InvalidateVisual();
            return true;
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            _nativeDisabled = true; _nativeState = "LegacyFallback: " + error.Message;
            Trace.WriteLine(_nativeState); ReleaseNativeStatic(); return false;
        }
    }

    private bool UpdateNativeLayer(Size size)
    {
        if (!NativeStaticSelected || _nativeBridge is null) return false;
        var panel = _panelShape ?? new Rect(size);
        var radius = _panelShape is null ? (Window.GetWindow(this) as GlassWindow)?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius;
        var enabled = _nativeReady && _nativeBounds.Contains(panel);
        _transitionLayer.SetBackdrop(enabled ? _nativeBridge.Image : null);
        _transitionLayer.Opacity = enabled ? 1 : 0;
        if (enabled) _transitionLayer.SetPanel(panel, _nativeBounds, radius, .6 + .4 * _transitionProgress,
            false, Material == GlassMaterialMode.FrostedGlass);
        _compatibilityLayer.SetBackdrop(null); _compatibilityLayer.Opacity = 0;
        return true;
    }

    private void ReleaseNativeStatic()
    {
        if (_nativeBridge is not null) _transitionLayer.SetBackdrop(null);
        _nativeReady = false; _nativeUploaded = null; _nativeMaterial = null; _nativePendingSince = 0;
        _nativeWallpaper?.Dispose(); _nativeWallpaper = null;
        _nativeBridge?.Dispose(); _nativeBridge = null;
        _nativeSession?.Dispose(); _nativeSession = null;
    }

    private object InspectNativeStatic() => new { requested = EnableNativeStaticWallpaper
        ? Environment.GetEnvironmentVariable("GLASSDESK_DOCK_RENDERER") ?? "legacy" : "legacy",
        selected = NativeStaticSelected, state = _nativeState, ready = _nativeReady,
        uploads = _nativeBridge?.WallpaperUploads, statistics = _nativeBridge?.Statistics,
        inputAndScratchTextureBytes = _nativeBridge is { } bridge
            ? (ulong)bridge.Statistics.Width * bridge.Statistics.Height * 4 * 3 : 0,
        recoveryAttempts = _nativeBridge?.RecoveryAttempts,
        lastError = _nativeBridge?.LastError, source = _nativeUploaded?.Source };
}
