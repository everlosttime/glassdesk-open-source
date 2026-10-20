using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;

/// <summary>Clear desktop transmission through a curved lens with independent reflection.</summary>
public sealed partial class LiquidGlassSurface : FrameworkElement, IDisposable
{
    private readonly GlassBackgroundLayer _background = new();
    private LiquidGlassOptics _optics = new();
    private AndroidLiquidGlassRenderer _androidGlass = new();
    private readonly GlassFrameWorker _worker = new();
    private readonly DispatcherTimer _timer;
    private TimeSpan _refreshInterval = GlassBackgroundLayer.LiveFrameInterval;
    private bool _renderClockAttached;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private long _lastRenderingTicks;
    private bool _renderFrameQueued;
    private DispatcherOperation? _renderFrameOperation;
    private DispatcherOperation? _refreshOperation;
    private bool _performanceSleeping;
    private long _lastRenderTicks, _renderCount;
    private bool? _firstVisibleRenderMaterialReady;
    private bool _lastRenderMaterialReady;
    private long _lifecycleGeneration;
    private long _lastCaptureSequence, _lastCaptureGeneration, _lastCaptureSourceTicks;
    private long _revealCaptureUntil;
    private int _renderFrameSignals;
    private readonly TransitionGlassLayer _transitionLayer = new();
    private readonly TransitionGlassLayer _compatibilityLayer = new();
    private BitmapSource? _compatibilityBackdrop;
    private Rect _compatibilityBounds;
    private bool _wallpaperPreparationPending;
    private CancellationTokenSource? _wallpaperCancellation;
    private long _handoffStarted;
    private (Rect Panel, double Radius) _compatibilityClipKey;
    private MaterialStamp? _materialStamp, _compatibilityStamp;
    private sealed record MaterialStamp(Point Origin, Rect CaptureBounds, DpiScale Dpi, GlassMaterialMode Material,
        bool Dark, double Clarity, bool Launchpad, LiquidGlassSamplingParameters Sampling,
        LiquidGlassWeightParameters Weights, LiquidGlassLodParameters Lod,
        LiquidGlassFinishingParameters Finishing, LiquidGlassBrdfParameters Brdf,
        LiquidGlassDispersionParameters Dispersion, long RecoveryGeneration);
    private ThemeService? _theme;
    private double _observedBackgroundClarity = .5;
    private double _transitionBlurRadius;
    private Rect? _panelShape;
    private double _panelRadius;
    private Geometry? _panelGeometry;
    private WriteableBitmap? _frame;
    private Rect _sampledPanelBounds;
    private Rect _sampledGlassPanelBounds;
    private double _sampledGlassPanelRadius;
    private Rect? _transitionCaptureBounds;
    private BitmapSource? _transitionBackdrop;
    private Rect _transitionBackdropBounds;
    private byte[]? _lastSource;
    private (int Width, int Height, Size Size, Rect GlassBounds, double Radius, bool Squircle, GlassBackdropSource Source,
        double Strength, double Softness, LiquidGlassDebugView DebugView,
        LiquidGlassSamplingParameters SamplingParameters, LiquidGlassWeightParameters WeightParameters,
        LiquidGlassLodParameters LodParameters, Color TintColor, double TintOpacity,
        double TintLuminanceInfluence, LiquidGlassBrdfParameters BrdfParameters,
        LiquidGlassDispersionParameters DispersionParameters, GlassMaterialMode Material, bool Dark, bool Transition,
        double BackgroundClarity, bool Launchpad) _frameKey;
    private bool _frameUsesAndroidGlass;
    private readonly Queue<long> _opticalFrameTimestamps = new();
    private double _opticalRefreshRate;
    private double _lastOpticsMilliseconds;
    private double _light = .35, _hover;
    private double _transitionProgress = 1;
    private bool _refreshPending;
    private GlassBackdropScene? _backdropScene;
    private GlassBackdropScene? _frameScene, _transitionBackdropScene;
    private long _materialRetentionUntil;
    private static int _nextDiagnosticId;
    private readonly int _diagnosticId = Interlocked.Increment(ref _nextDiagnosticId);
    private string? _diagnosticMaterial;
    internal Func<GlassBackdropScene?> ReadBackdropScene { get; set; }
    internal Func<PixelRect, int, int, byte[]?> CaptureRevealBackdrop { get; set; }
    private GlassBackdropScene? _compatibilityScene;
    private bool _compatibilityIsDesktopSnapshot;
    private Point? _revealOrigin;
    private PixelRect? _revealHost;
    private int _preparedDesktopSnapshots;
    private string? _firstVisibleMaterialSource;
    internal string? RevealMaterialInvalidationReason { get; private set; }
    public Func<bool>? IsBackdropActive { get; set; }
    /// <summary>Large launchpad profile; Dock retains its compact material.</summary>
    public bool UseLaunchpadOptics { get; set; }
    /// <summary>Selects launcher transparency independently of the shared optical material.</summary>
    public bool UseLauncherTransparency { get; set; }
    private double BackgroundClarity => AndroidLiquidGlassRenderer.NormalizeBackgroundClarity(
        UseLauncherTransparency
            ? ThemeService.Current?.Appearance.LauncherGlassTransparency ?? AppearanceSettings.DefaultLauncherGlassTransparency
            : ThemeService.Current?.Appearance.DockGlassTransparency ?? AppearanceSettings.DefaultDockGlassTransparency);
    public GlassBackdropSource BackdropSource { get; private set; }
    internal bool IsCurrentRevealMaterialReady
    {
        get
        {
            EnsureBackdropScene(revealing: true);
            if (ThemeService.Current?.UseOpaquePanels == true || BackdropRecovery.RequiresBasicRendering
                || _background.RequiresBasicBackground) return true;
            var stamp = ReadMaterialStamp();
            return _backdropScene is not null &&
                ((_frame is not null && _frameScene?.Equals(_backdropScene) == true
                    || _transitionBackdrop is not null && _transitionBackdropScene?.Equals(_backdropScene) == true) && _materialStamp == stamp
                || _compatibilityIsDesktopSnapshot && _compatibilityBackdrop is not null
                    && _compatibilityScene?.Equals(_backdropScene) == true && _compatibilityStamp == stamp);
        }
    }
    /// <summary>Stable capture rectangle in owner-window DIPs when the surface is visually transformed.</summary>
    public Rect? CaptureBoundsInWindow { get; set; }

    /// <summary>Capture the full animation path; only the shared live geometry masks it.</summary>
    public void SetContainerTransitionBounds(Rect? bounds)
    {
        if (_transitionCaptureBounds == bounds) return;
        _transitionCaptureBounds = bounds;
        _worker.Invalidate();
        _lastSource = null;
        SetTimerInterval(GlassBackgroundLayer.LiveFrameInterval);
        RequestBackdropRefresh();
        UpdateTransitionLayer();
        InvalidateVisual();
    }

    /// <summary>One physical panel inside an untransformed host; capture and drawing share its bounds.</summary>
    public void SetPanelShape(Rect bounds, double radius, Geometry? geometry = null)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        _panelShape = bounds;
        _panelRadius = double.IsFinite(radius) ? Math.Max(0, radius) : 0;
        _panelGeometry = geometry;
        Clip = geometry ?? (Window.GetWindow(this) is GlassWindow owner
            ? owner.CreateGlassPanelGeometry(bounds, _panelRadius)
            : SquircleGlassGeometry.Create(bounds, _panelRadius));
        CaptureBoundsInWindow = bounds;
        UpdateTransitionLayer();
        InvalidateVisual();
    }

    /// <summary>Normalized container-transition progress shared with the owning window and shadow.</summary>
    public double TransitionProgress
    {
        get => _transitionProgress;
        set
        {
            _transitionProgress = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
            UpdateTransitionLayer();
            if (_transitionProgress < 1 && _refreshInterval > GlassBackgroundLayer.TransitionFrameInterval)
                SetTimerInterval(GlassBackgroundLayer.TransitionFrameInterval);
            InvalidateVisual();
        }
    }

    /// <summary>Additional background filtering only; specular edges remain sharp.</summary>
    public double TransitionBlurRadius
    {
        get => _transitionBlurRadius;
        set => _transitionBlurRadius = double.IsFinite(value) ? Math.Clamp(value, 0, 6) : 0;
    }

    public LiquidGlassSurface()
    {
        ReadBackdropScene = CaptureBackdropScene;
        CaptureRevealBackdrop = CaptureHiddenDesktop;
        _backdropScene = ReadBackdropScene();
        IsHitTestVisible = false;
        AddVisualChild(_transitionLayer);
        AddVisualChild(_compatibilityLayer);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnWatchdogFrame;
        Loaded += (_, _) =>
        {
            _theme = ThemeService.Current;
            if (_theme is not null)
            {
                _theme.Changed -= OnAppearanceChanged;
                _theme.Changed += OnAppearanceChanged;
                _observedBackgroundClarity = BackgroundClarity;
            }
            RequestBackdropRefresh(revealing: true);
            if (!_performanceSleeping && IsVisible && IsBackdropActive?.Invoke() != false) _timer.Start();
        };
        Unloaded += (_, _) => Dispose();
        IsVisibleChanged += (_, _) =>
        {
            _firstVisibleRenderMaterialReady = null;
            _firstVisibleMaterialSource = null;
            UpdateRenderClock();
            if (!IsVisible)
            {
                _revealCaptureUntil = 0;
                // Keep material and suspend readback; the compatible session stays warm
                // while live geometry decides whether the previous mask fits.
                _worker.Invalidate(); _worker.ClearCachedBuffers(); _lastSource = null; _background.Suspend();
                _timer.Stop(); StopRenderClock(); CancelQueuedRefresh();
            }
            else RequestBackdropRefresh(revealing: true);
        };
    }

    /// <summary>Wake capture when a visible host reveals its previously inactive panel.</summary>
    public void RequestBackdropRefresh(bool revealing = false)
    {
        EnsureBackdropScene(revealing);
        if (_performanceSleeping) { InvalidateVisual(); return; }
        SetTimerInterval(GlassBackgroundLayer.LiveFrameInterval);
        if (_refreshPending) return;
        _refreshPending = true;
        var generation = _lifecycleGeneration;
        _refreshOperation = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _refreshOperation = null;
            _refreshPending = false;
            if (!_performanceSleeping && generation == _lifecycleGeneration && IsLoaded) OnFrame(null, EventArgs.Empty);
        }));
        InvalidateVisual();
    }

    /// <summary>A fully clipped panel must reacquire content, even when foreign HWNDs are unchanged.</summary>
    public void RetireConcealedMaterial()
    {
        RecordMaterialRetirement("Concealed", expected: true);
        ++_lifecycleGeneration;
        _worker.Invalidate(); _worker.ClearCachedBuffers();
        _lastSource = null; _lastCaptureSequence = _lastCaptureGeneration = _lastCaptureSourceTicks = 0;
        _frame = null; _frameScene = null; _materialStamp = null;
        _transitionBackdrop = null; _transitionBackdropScene = null;
        _compatibilityBackdrop = null; _compatibilityStamp = null;
        _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null;
        _handoffStarted = _materialRetentionUntil = _revealCaptureUntil = 0;
        _wallpaperCancellation?.Cancel(); _wallpaperCancellation = null;
        _wallpaperPreparationPending = false;
        _background.Suspend(); _timer.Stop(); StopRenderClock(); CancelQueuedRefresh();
        _transitionLayer.SetBackdrop(null); _transitionLayer.Opacity = 0;
        _compatibilityLayer.SetBackdrop(null); _compatibilityLayer.Opacity = 0;
        _firstVisibleRenderMaterialReady = null; _firstVisibleMaterialSource = null;
        BackdropSource = GlassBackdropSource.Transparent;
        InvalidateVisual();
    }

    /// <summary>Retire capture, optical work and large presentation resources until a fresh reveal.</summary>
    public void SetPerformanceSleeping(bool sleeping)
    {
        if (_performanceSleeping == sleeping) return;
        _performanceSleeping = sleeping;
        _revealCaptureUntil = 0;
        ++_lifecycleGeneration;
        _worker.Invalidate(); _worker.ClearCachedBuffers();
        _lastSource = null;
        _lastCaptureSequence = _lastCaptureGeneration = _lastCaptureSourceTicks = 0;
        _background.SetPerformanceSleeping(sleeping);
        _transitionLayer.SetPerformanceSleeping(sleeping);
        _compatibilityLayer.SetPerformanceSleeping(sleeping);
        if (sleeping)
        {
            _timer.Stop(); StopRenderClock(); CancelQueuedRefresh();
            _optics = new(); _androidGlass = new();
            _opticalFrameTimestamps.Clear(); _opticalRefreshRate = 0;
            _handoffStarted = 0;
            // Deep sleep reacquires the current desktop on the next reveal. Retaining
            // a hidden full-size texture also retains WPF's shader input resources.
            _frame = null; _frameScene = null;
            _transitionBackdrop = null; _transitionBackdropScene = null;
            _materialStamp = null;
            _compatibilityBackdrop = null; _compatibilityStamp = null;
            _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null;
            _compatibilityLayer.SetBackdrop(null); _compatibilityLayer.Opacity = 0;
            _wallpaperPreparationPending = false;
            _wallpaperCancellation?.Cancel(); _wallpaperCancellation = null;
        }
        else if (IsLoaded && IsVisible && IsBackdropActive?.Invoke() != false)
        {
            _timer.Start(); RequestBackdropRefresh(revealing: true);
        }
        InvalidateVisual();
    }

    public object InspectPerformance() => new
    {
        sleeping = _performanceSleeping, generation = _lifecycleGeneration,
        renderClockAttached = _renderClockAttached, watchdogEnabled = _timer.IsEnabled,
        renderQueued = _renderFrameQueued, refreshQueued = _refreshPending, opticsBusy = _worker.IsBusy,
        lastRenderTicks = _lastRenderTicks, renderCount = _renderCount, lastRenderMaterialReady = _lastRenderMaterialReady,
        firstVisibleRenderMaterialReady = _firstVisibleRenderMaterialReady,
        firstVisibleMaterialSource = _firstVisibleMaterialSource,
        materialReady = _frame is not null || _transitionBackdrop is not null || _compatibilityBackdrop is not null,
        compatibilityReady = _compatibilityBackdrop is not null, handoffActive = _handoffStarted != 0,
        compatibilityIsDesktopSnapshot = _compatibilityIsDesktopSnapshot,
        preparedDesktopSnapshots = _preparedDesktopSnapshots, revealMaterialInvalidationReason = RevealMaterialInvalidationReason,
        capture = _background.InspectPerformance(),
        source = BackdropSource.ToString(), retainedPixelBytes = (long)(_frame?.PixelWidth ?? 0) * (_frame?.PixelHeight ?? 0) * 4
            + (long)(_transitionBackdrop?.PixelWidth ?? 0) * (_transitionBackdrop?.PixelHeight ?? 0) * 4
            + (long)(_compatibilityBackdrop?.PixelWidth ?? 0) * (_compatibilityBackdrop?.PixelHeight ?? 0) * 4
    };

    private void CancelQueuedRefresh()
    {
        _refreshOperation?.Abort(); _refreshOperation = null; _refreshPending = false;
    }

    public void Dispose()
    {
        if (_theme is not null) _theme.Changed -= OnAppearanceChanged;
        _theme = null;
        _worker.Invalidate();
        _worker.ClearCachedBuffers();
        StopRenderClock();
        CancelQueuedRefresh(); ++_lifecycleGeneration;
        _revealCaptureUntil = 0;
        _wallpaperCancellation?.Cancel(); _wallpaperCancellation = null;
        _timer.Stop(); _background.Dispose(); _frame = null; _lastSource = null;
        _transitionBackdrop = null;
        _compatibilityBackdrop = null;
        _frameScene = null; _transitionBackdropScene = null;
        _transitionLayer.SetBackdrop(null); _transitionLayer.Opacity = 0;
        _compatibilityLayer.SetBackdrop(null); _compatibilityLayer.Opacity = 0;
        BackdropSource = GlassBackdropSource.Transparent;
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        RecordMaterialRetirement("AppearanceChanged", expected: true);
        // The notification can represent wallpaper/system changes even when the
        // resolved theme and transparency numbers remain identical.
        ++_lifecycleGeneration;
        _wallpaperCancellation?.Cancel(); _wallpaperCancellation = null;
        _wallpaperPreparationPending = false;
        _frame = null; _frameScene = null;
        _transitionBackdrop = null; _transitionBackdropScene = null;
        _compatibilityBackdrop = null; _compatibilityStamp = null;
        _materialStamp = null; _handoffStarted = 0;
        _background.Suspend();
        var clarity = BackgroundClarity;
        // Theme and material changes invalidate pixels even when clarity is unchanged.
        _observedBackgroundClarity = clarity;
        _worker.Invalidate();
        _lastSource = null;
        SetTimerInterval(GlassBackgroundLayer.LiveFrameInterval);
        // Theme updates may originate outside a Dispatcher callback (including startup).
        // Queue the capture so its awaited worker resumes in WPF's UI context.
        RequestBackdropRefresh();
        InvalidateVisual();
    }

    private async void OnFrame(object? sender, EventArgs e)
    {
        if (_performanceSleeping) return;
        var lifecycleGeneration = _lifecycleGeneration;
        if (!RenderCapability.IsPixelShaderVersionSupported(3, 0)) GlassDesk.UI.BackdropRecovery.UseBasicRendering();
        EnsureBackdropScene();
        if (Window.GetWindow(this) is not GlassWindow owner || !owner.IsVisible || !IsVisible
            || ActualWidth < 1 || ActualHeight < 1 || IsBackdropActive?.Invoke() == false)
        {
            _worker.Invalidate();
            _worker.ClearCachedBuffers();
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _lastSource = null;
            // Reveal preparation starts acquisition before the first slide pixel.
            // The queued inactive tick must not suspend that very same request.
            // A bounded watchdog retires preparation if the reveal is cancelled.
            if (_revealCaptureUntil > Stopwatch.GetTimestamp())
            {
                _timer.Interval = TimeSpan.FromMilliseconds(100); _timer.Start();
            }
            else
            {
                _revealCaptureUntil = 0; _background.Suspend(); _timer.Stop();
            }
            StopRenderClock();
            return;
        }
        _revealCaptureUntil = 0;
        if (owner.PrimaryDisplaySession is { IsAvailable: false })
        {
            _worker.Invalidate();
            _worker.ClearCachedBuffers();
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _background.Dispose(); _frame = null; _transitionBackdrop = null; _lastSource = null;
            UpdateTransitionLayer();
            return;
        }
        if (ThemeService.Current?.UseOpaquePanels == true)
        {
            _worker.Invalidate();
            _worker.ClearCachedBuffers();
            BackdropSource = GlassBackdropSource.Opaque;
            _frame = null; _lastSource = null; _background.Dispose();
            SetTimerInterval(TimeSpan.FromMilliseconds(200)); InvalidateVisual(); return;
        }
        // Capture and WPF state stay on the dispatcher. Do not acquire or enqueue another
        // frame while the immutable previous capture is being composed off the UI thread.
        if (_worker.IsBusy) return;
        var reduced = ThemeService.Current?.AnimationsEnabled == false;
        var hovering = owner.IsMouseOver && !reduced;
        var panelRect = _panelShape ?? new Rect(RenderSize);
        var target = hovering ? Math.Clamp((Mouse.GetPosition(this).X - panelRect.X) / panelRect.Width, .05, .95) : .35;
        _light += (target - _light) * (reduced ? 1 : .22);
        _hover += ((hovering ? 1 : 0) - _hover) * (reduced ? 1 : .2);
        var samplingParameters = LiquidGlassDebugState.SamplingParameters;
        var weightParameters = LiquidGlassDebugState.WeightParameters;
        var lodParameters = LiquidGlassDebugState.LodParameters;
        var debugView = LiquidGlassDebugState.View;
        var material = ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass;
        var dark = ThemeService.Current?.IsDark != false;
        var backgroundClarity = BackgroundClarity;
        var useAndroidGlass = (material is GlassMaterialMode.AndroidLiquidGlass or GlassMaterialMode.FrostedGlass)
            && debugView == LiquidGlassDebugView.None;
        var transitionCapture = useAndroidGlass ? _transitionCaptureBounds : null;
        var transitionBackdrop = transitionCapture is not null;
        var finishingParameters = LiquidGlassDebugState.FinishingParameters;
        var brdfParameters = LiquidGlassDebugState.BrdfParameters;
        var dispersionParameters = LiquidGlassDebugState.DispersionParameters;
        var opticsView = LiquidGlassFinishComposer.OpticsView(debugView);
        var usesMixedSampling = opticsView is LiquidGlassDebugView.None
            or LiquidGlassDebugView.FinalMixedSampling
            or LiquidGlassDebugView.LodOff
            or LiquidGlassDebugView.FaceLodOnly
            or LiquidGlassDebugView.InnerLodOnly
            or LiquidGlassDebugView.OuterLodOnly
            or LiquidGlassDebugView.FinalMixedWithLod
            or LiquidGlassDebugView.DispersionOff
            or LiquidGlassDebugView.InnerDispersionOnly
            or LiquidGlassDebugView.OuterDispersionOnly
            or LiquidGlassDebugView.FinalWithDispersion
            or LiquidGlassDebugView.DispersionTimesTenDebug;
        var usesOuterSampling = opticsView == LiquidGlassDebugView.OuterOnly || usesMixedSampling;
        var usesLodSampling = opticsView is LiquidGlassDebugView.None
            or LiquidGlassDebugView.FaceLodOnly
            or LiquidGlassDebugView.InnerLodOnly
            or LiquidGlassDebugView.OuterLodOnly
            or LiquidGlassDebugView.FinalMixedWithLod
            or LiquidGlassDebugView.DispersionOff
            or LiquidGlassDebugView.InnerDispersionOnly
            or LiquidGlassDebugView.OuterDispersionOnly
            or LiquidGlassDebugView.FinalWithDispersion
            or LiquidGlassDebugView.DispersionTimesTenDebug;
        var captureMarginDip = usesOuterSampling ? samplingParameters.OuterRimCaptureMarginDip : 0;
        if (usesLodSampling) captureMarginDip += lodParameters.MaximumActiveFootprintDip;
        // Three-box Gaussian support plus maximum inward lens displacement.
        if (useAndroidGlass) captureMarginDip = 48;
        var captureBounds = transitionCapture ?? CaptureBoundsInWindow;
        var topLeft = captureBounds is { } fixedBounds
            ? owner.PointToScreen(new Point(fixedBounds.X, fixedBounds.Y))
            : PointToScreen(new Point());
        var bottomRight = captureBounds is { } fixedBounds2
            ? owner.PointToScreen(new Point(fixedBounds2.Right, fixedBounds2.Bottom))
            : PointToScreen(new Point(ActualWidth, ActualHeight));
        var primaryDisplay = owner.PrimaryDisplaySession is { IsAvailable: true } primarySession
            ? primarySession.Display
            : DisplayService.Primary();
        if (primaryDisplay is null)
        {
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _background.Dispose(); _frame = null; _lastSource = null;
            return;
        }
        var dpiScale = primaryDisplay.ScaleFactor;
        var regionLeft = (int)Math.Floor(topLeft.X - captureMarginDip * dpiScale);
        var regionTop = (int)Math.Floor(topLeft.Y - captureMarginDip * dpiScale);
        var regionRight = (int)Math.Ceiling(bottomRight.X + captureMarginDip * dpiScale);
        var regionBottom = (int)Math.Ceiling(bottomRight.Y + captureMarginDip * dpiScale);
        var requestedRegion = new PixelRect(regionLeft, regionTop,
            Math.Max(1, regionRight - regionLeft), Math.Max(1, regionBottom - regionTop));
        var region = owner.PrimaryDisplaySession is { IsAvailable: true } session
            ? session.ClipToPrimaryBounds(requestedRegion)
            : ClipRegion(requestedRegion, primaryDisplay.Bounds);
        if (region.Width <= 0 || region.Height <= 0)
        {
            SetTimerInterval(TimeSpan.FromMilliseconds(200));
            _background.Dispose(); _frame = null; _lastSource = null;
            return;
        }
        var launchpadOptics = UseLaunchpadOptics;
        var pixelBudget = launchpadOptics
            ? 320_000 + 680_000 * Math.Clamp((backgroundClarity - .5) * 2, 0, 1)
            : !transitionBackdrop && _transitionProgress < 1 ? 100_000 : 320_000;
        var scale = Math.Min(1, Math.Min(2048.0 / Math.Max(requestedRegion.Width, requestedRegion.Height),
            Math.Sqrt(pixelBudget / ((double)requestedRegion.Width * requestedRegion.Height))));
        var width = Math.Max(1, (int)Math.Floor(requestedRegion.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(requestedRegion.Height * scale));
        var radius = _panelShape is null ? owner.PanelCornerRadius.TopLeft : _panelRadius;
        var squircle = owner.UsesSquirclePanelGeometry;
        var captureGeometry = GlassCaptureGeometry.Create(requestedRegion, region, topLeft,
            panelRect, dpiScale, captureMarginDip > 0);
        if (transitionBackdrop)
            captureGeometry = GlassCaptureGeometry.Create(requestedRegion, region,
                owner.PointToScreen(panelRect.Location), panelRect, dpiScale, true);
        var sampleSizeDip = captureGeometry.SampleSizeDip;
        var glassBoundsDip = captureGeometry.GlassBoundsDip;
        var sampledFrameBounds = captureGeometry.FrameBoundsDip;
        // Background is captured first. The glass stage below only reads this fixed frame;
        // WPF content is a separate visual above LiquidGlassSurface in each window.
        using var capturedBackground = _background.Capture(region,
            Math.Max(1, (int)Math.Floor(region.Width * scale)),
            Math.Max(1, (int)Math.Floor(region.Height * scale)), radius,
            owner.SurfaceMode == GlassSurfaceMode.OpticalGlass);
        var capturedScene = _backdropScene;
        var capturedStamp = ReadMaterialStamp();
        var capturedSourceTicks = _background.LastCaptureTiming?.SourceTicks ?? 0;
        using var background = capturedBackground is null ? null
            : _background.ExtendCapturedFrame(capturedBackground, region, requestedRegion, width, height);
        var source = background?.Pixels;
        var kind = background?.Source ?? GlassBackdropSource.Transparent;
        BackdropSource = kind;
        if (kind == GlassBackdropSource.Basic)
        {
            RecordMaterialRetirement("CaptureDeadlineExpired");
            // Capture, CPU optics and shader are all optional for this readable body.
            _worker.Invalidate(); _frame = null; _transitionBackdrop = null; _lastSource = null;
            UpdateTransitionLayer(); InvalidateVisual(); return;
        }
        var strength = .6 + .4 * _transitionProgress;
        // The unmasked transition prefilter depends on capture size/material, not the
        // moving silhouette. Its GPU lens receives the current shape every render.
        var key = (width, height, sampleSizeDip, transitionBackdrop ? Rect.Empty : glassBoundsDip,
            transitionBackdrop ? 0 : radius, squircle, kind,
            strength, _transitionBlurRadius, debugView, samplingParameters, weightParameters, lodParameters,
            finishingParameters.TintColor, finishingParameters.TintOpacity,
            finishingParameters.TintLuminanceInfluence, brdfParameters, dispersionParameters, material, dark, transitionBackdrop, backgroundClarity, launchpadOptics);
        // A cold native session may have no first frame yet. Keep the last completed
        // texture, masked by current geometry, until a fresh source replaces it.
        if (source is null) { _lastSource = null; }
        else if ((transitionBackdrop ? _transitionBackdrop is null : _frame is null)
            || _frameKey != key
            || (background!.IsCaptured ? background.Sequence != _lastCaptureSequence || background.CaptureGeneration != _lastCaptureGeneration
                : _lastSource is null || !ReferenceEquals(source, _lastSource)))
        {
            // Capture and optics follow the current panel, independently of content scale.
            // Cached geometry is rebuilt only when its physical sampling parameters change.
            var applyWallpaperShoulder = !useAndroidGlass && kind == GlassBackdropSource.Wallpaper
                && (debugView == LiquidGlassDebugView.None || LiquidGlassFinishComposer.IsComparisonView(debugView));
            var wallpaperWeights = applyWallpaperShoulder
                ? CaptureWallpaperShoulderWeights(owner, width, height, sampleSizeDip, glassBoundsDip, radius)
                : null;
            // Queued composition must retain its original renderer. Deep sleep replaces
            // those cache owners; an old task must not rebuild the new sleeping caches.
            var optics = _optics; var androidGlass = _androidGlass;
            using var result = transitionBackdrop
                ? await _worker.RenderBufferedAsync(source.Length, (pixels, scratch) =>
                    RenderMaterialBackdropInto(material, source, pixels, scratch, width, height, sampleSizeDip, dark, strength,
                        backgroundClarity, launchpadOptics))
                : await _worker.RenderAsync(() => LiquidGlassFrameComposer.Compose(optics, source, width, height,
                sampleSizeDip, radius, squircle, strength, _transitionBlurRadius, debugView,
                samplingParameters, weightParameters, glassBoundsDip, lodParameters,
                finishingParameters, brdfParameters, dispersionParameters,
                wallpaperWeights is not null
                    ? pixels => ApplyWallpaperShoulder(pixels, wallpaperWeights)
                : null, material, dark, androidGlass, backgroundClarity: backgroundClarity, launchpadOptics: launchpadOptics));
            EnsureBackdropScene();
            if (result is null || !_worker.IsCurrent(result) || _performanceSleeping || lifecycleGeneration != _lifecycleGeneration
                || !owner.IsVisible || !IsVisible || IsBackdropActive?.Invoke() == false
                || owner.PrimaryDisplaySession is { IsAvailable: false }
                || ThemeService.Current?.UseOpaquePanels == true
                || backgroundClarity != BackgroundClarity || capturedScene is null
                || !CanCommitCapturedScene(capturedScene, capturedStamp, capturedSourceTicks)) return;
            var refracted = result.Pixels;
            if (_compatibilityBackdrop is not null && _handoffStarted == 0) _handoffStarted = Stopwatch.GetTimestamp();
            if (transitionBackdrop)
            {
                if (!capturedScene.Equals(_frameScene)) { _frame = null; _frameScene = null; }
                if (!TryUpdateBackdropBitmap(_transitionBackdrop, refracted, width, height, out var submitted)) return;
                _transitionBackdrop = submitted;
                _transitionBackdropScene = capturedScene;
                if (_transitionBackdropBounds != sampledFrameBounds) InvalidateArrange();
                _transitionBackdropBounds = sampledFrameBounds;
            }
            else
            {
                if (!capturedScene.Equals(_transitionBackdropScene)) { _transitionBackdrop = null; _transitionBackdropScene = null; }
                if (!TryUpdateBackdropBitmap(_frame, refracted, width, height, out var submitted)) return;
                _frame = submitted;
                _frameScene = capturedScene;
                _sampledGlassPanelBounds = panelRect;
                _sampledGlassPanelRadius = radius;
                _sampledPanelBounds = sampledFrameBounds;
            }
            // A freshly committed scene replaces an older reveal snapshot in the
            // same bitmap transaction. Do not restart a fade that would extend
            // the snapshot's already bounded cross-scene retention deadline.
            if (_compatibilityIsDesktopSnapshot && !capturedScene.Equals(_compatibilityScene))
            {
                _compatibilityBackdrop = null; _compatibilityStamp = null;
                _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null; _handoffStarted = 0;
            }
            _lastOpticsMilliseconds = result.Milliseconds;
            if (!_compatibilityIsDesktopSnapshot || _compatibilityScene?.Equals(_backdropScene) == true)
                _materialRetentionUntil = 0;
            _frameUsesAndroidGlass = useAndroidGlass;
            _materialStamp = ReadMaterialStamp();
            _opticalFrameTimestamps.Enqueue(Stopwatch.GetTimestamp());
            _lastSource = background!.IsCaptured ? null : source; _frameKey = key;
            _lastCaptureSequence = background.Sequence; _lastCaptureGeneration = background.CaptureGeneration;
            _lastCaptureSourceTicks = capturedSourceTicks;
        }
        else if (background!.IsCaptured && capturedSourceTicks > _lastCaptureSourceTicks
            && CanCommitCapturedScene(capturedScene, capturedStamp, capturedSourceTicks))
        {
            if (transitionBackdrop) _transitionBackdropScene = capturedScene;
            else _frameScene = capturedScene;
            _lastCaptureSourceTicks = capturedSourceTicks;
            if (!_compatibilityIsDesktopSnapshot || _compatibilityScene?.Equals(_backdropScene) == true)
                _materialRetentionUntil = 0;
        }
        if (!transitionBackdrop && _frame is not null)
        {
            _sampledPanelBounds = sampledFrameBounds;
            _sampledGlassPanelBounds = panelRect;
            _sampledGlassPanelRadius = radius;
        }
        UpdateTransitionLayer();
        RefreshOpticalRefreshRate();
        SetTimerInterval(_transitionProgress < 1
            ? GlassBackgroundLayer.TransitionFrameInterval
            : source is null || kind is GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene || hovering
                ? GlassBackgroundLayer.LiveFrameInterval : TimeSpan.FromMilliseconds(160));
        InvalidateVisual();
    }

    // WPF owns the copied pixels. Keep the same native texture while its dimensions
    // match; WritePixels also notifies the existing drawing/shader of the new frame.
    internal static bool TryUpdateBackdropBitmap(BitmapSource? current, byte[] pixels, int width, int height, out WriteableBitmap bitmap)
    {
        if (width <= 0 || height <= 0 || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Invalid bitmap submission extent.");
        bitmap = current is WriteableBitmap retained && !retained.IsFrozen
            && retained.PixelWidth == width && retained.PixelHeight == height
            ? retained : new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        if (!bitmap.TryLock(new Duration(TimeSpan.Zero))) return false;
        try
        {
            var rowBytes = checked(width * 4);
            if (bitmap.BackBufferStride == rowBytes)
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.BackBuffer, pixels.Length);
            else
                for (var row = 0; row < height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, row * rowBytes,
                        bitmap.BackBuffer + row * bitmap.BackBufferStride, rowBytes);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height)); return true;
        }
        finally { bitmap.Unlock(); }
    }

    private static double[] CaptureWallpaperShoulderWeights(GlassWindow owner, int width, int height,
        Size sampleSizeDip, Rect glassBoundsDip, double radius)
    {
        // Snapshot owner-dependent geometry on its dispatcher; the worker only reads values.
        var weights = new double[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            owner.TryGetGlassPanelEdge(glassBoundsDip, radius,
                new Point((x + .5) * sampleSizeDip.Width / width,
                    (y + .5) * sampleSizeDip.Height / height), out var depth, out _);
            weights[y * width + x] = Math.Clamp(1 - depth / 12, 0, 1);
        }
        return weights;
    }

    private static void ApplyWallpaperShoulder(byte[] pixels, double[] weights)
    {
        // Same alpha expression and placement before Tint / BRDF / Dispersion.
        for (var pixel = 0; pixel < weights.Length; pixel++)
        {
            var weight = weights[pixel];
            var index = pixel * 4 + 3;
            pixels[index] = (byte)(pixels[index] * .32 * weight * weight);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        EnsureBackdropScene();
        UpdateTransitionLayer();
        SharedMaterialChanged?.Invoke(this, EventArgs.Empty);
        var dc = drawingContext;
        base.OnRender(dc);
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var rect = _panelShape ?? new Rect(0, 0, ActualWidth, ActualHeight);
        var glass = Window.GetWindow(this) as GlassWindow;
        var radius = _panelShape is null ? glass?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius;
        _lastRenderTicks = Stopwatch.GetTimestamp(); ++_renderCount;
        _lastRenderMaterialReady = ThemeService.Current?.UseOpaquePanels == true || _background.RequiresBasicBackground
            || _frame is not null && _sampledGlassPanelBounds == rect && _sampledGlassPanelRadius == radius
            || _transitionBackdrop is not null && _transitionBackdropBounds.Contains(rect)
            || _compatibilityBackdrop is not null && _compatibilityLayer.Opacity > 0
                && _compatibilityLayer.RenderSize.Width > 0 && _compatibilityLayer.RenderSize.Height > 0;
        _firstVisibleRenderMaterialReady ??= _lastRenderMaterialReady;
        var displayedSource = ThemeService.Current?.UseOpaquePanels == true ? "Opaque"
            : BackdropRecovery.RequiresBasicRendering || _background.RequiresBasicBackground ? "Basic"
            : _frame is not null && _sampledGlassPanelBounds == rect && _sampledGlassPanelRadius == radius
                || _transitionBackdrop is not null && _transitionBackdropBounds.Contains(rect) ? "Live"
            : _compatibilityIsDesktopSnapshot && _compatibilityBackdrop is not null ? "DesktopSnapshot" : "Ambient";
        if (_diagnosticMaterial != displayedSource)
        {
            var visibleLoss = _diagnosticMaterial == "Live" && (displayedSource is "Ambient" or "Basic")
                && Window.GetWindow(this)?.IsVisible == true && IsVisible && IsBackdropActive?.Invoke() != false;
            RecordSurfaceTrace(visibleLoss ? "MaterialLoss" : "MaterialState",
                (_diagnosticMaterial ?? "Initial") + "To" + displayedSource, displayedSource);
            _diagnosticMaterial = displayedSource;
        }
        _firstVisibleMaterialSource ??= _compatibilityIsDesktopSnapshot && _compatibilityBackdrop is not null
            && _compatibilityLayer.Opacity > 0 ? "CurrentDesktopSnapshot"
            : _frame is not null || _transitionBackdrop is not null ? "CompletedLiveMaterial"
            : ThemeService.Current?.UseOpaquePanels == true ? "OpaquePreference" : "AmbientCompatibility";
        var transition = Math.Clamp(_transitionProgress, 0, 1);
        var highlightScale = .48 + .52 * transition;
        var debugView = LiquidGlassDebugState.View;
        Geometry CreatePanel(Rect bounds, double cornerRadius)
        {
            if (_panelShape is not null && bounds == rect && cornerRadius == radius)
                return _panelGeometry ??= glass is null
                    ? SquircleGlassGeometry.Create(bounds, cornerRadius)
                    : glass.CreateGlassPanelGeometry(bounds, cornerRadius);
            return glass is null ? SquircleGlassGeometry.Create(bounds, cornerRadius)
                : glass.CreateGlassPanelGeometry(bounds, cornerRadius);
        }
        var dark = ThemeService.Current?.IsDark != false;
        if (debugView != LiquidGlassDebugView.None
            && (!LiquidGlassFinishComposer.IsComparisonView(debugView)
                || LiquidGlassFinishComposer.IsBrdfComponentView(debugView)))
        {
            if (_frame is not null) dc.DrawImage(_frame, _sampledPanelBounds);
            var outline = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1);
            dc.DrawGeometry(null, outline, CreatePanel(rect, radius));
            if (LiquidGlassDebugState.ShowPerformanceOverlay) DrawMetricsOverlay(dc, rect);
            return;
        }

        dc.PushClip(CreatePanel(rect, radius));
        if (ThemeService.Current?.UseOpaquePanels == true || BackdropRecovery.RequiresBasicRendering)
        {
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(28, 29, 34) : Color.FromRgb(246, 247, 250)), null, rect);
            if (_background.RequiresBasicBackground) DrawCompatibilityNotice(dc, rect, "基础背景 · 正在恢复实时效果", dark);
            dc.Pop(); return;
        }
        var compatibleChrome = debugView == LiquidGlassDebugView.None && (_frameUsesAndroidGlass
            || _frame is null && (ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass)
                is GlassMaterialMode.AndroidLiquidGlass or GlassMaterialMode.FrostedGlass);
        var opticalFrameMatches = _frame is not null && _sampledGlassPanelBounds == rect
            && _sampledGlassPanelRadius == radius;
        var useTransitionMaterial = _transitionCaptureBounds is not null || !opticalFrameMatches;
        var hasTransitionBackdrop = compatibleChrome && _transitionBackdrop is not null
            && _transitionBackdropBounds.Contains(rect);
        if (compatibleChrome && useTransitionMaterial && !hasTransitionBackdrop)
        {
            // During first capture/expansion, paint a complete low-opacity body. A stale
            // tiny frame must never leave just an outline on the expanding launcher.
            var tint = dark ? (byte)18 : (byte)250;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(255 * AndroidLiquidGlassRenderer.BodyOpacity * AndroidLiquidGlassRenderer.BodyTintScale(
                    BackgroundClarity, UseLaunchpadOptics)), tint, tint, tint)), null, rect);
        }
        if (opticalFrameMatches && (!compatibleChrome || _transitionCaptureBounds is null))
        {
            dc.DrawImage(_frame, _sampledPanelBounds);
        }
        if (compatibleChrome)
        {
            if (!opticalFrameMatches && !hasTransitionBackdrop)
                DrawWaitingRim(dc, CreatePanel(rect, radius), highlightScale);
            if (LiquidGlassDebugState.ShowPerformanceOverlay) DrawMetricsOverlay(dc, rect);
            if (BackdropSource == GlassBackdropSource.CompatibleScene)
                DrawCompatibilityNotice(dc, rect, "兼容场景玻璃 · 壁纸为静态", dark);
            else if (BackdropSource == GlassBackdropSource.Wallpaper)
                DrawCompatibilityNotice(dc, rect, "壁纸玻璃 · 不反映后方窗口", dark);
            dc.Pop();
            return;
        }
        var body = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(ScaleAlpha(5, highlightScale),255,255,255),0), new(Colors.Transparent,.35),
            new(Color.FromArgb(ScaleAlpha(dark ? (byte)6 : (byte)3, highlightScale),12,18,30),1)
        }, new Point(0,0), new Point(0,1));
        dc.DrawRectangle(body, null, rect);
        var reflection = new RadialGradientBrush
        {
            Center = new Point(_light, -.12), GradientOrigin = new Point(_light, -.12), RadiusX = .38, RadiusY = .58,
            GradientStops = new GradientStopCollection { new(Color.FromArgb(ScaleAlpha(7 + _hover * 4, highlightScale),255,255,255),0), new(Colors.Transparent,1) }
        };
        dc.DrawRectangle(reflection, null, rect);
        var rim = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(ScaleAlpha(70, highlightScale),255,255,255),0), new(Color.FromArgb(ScaleAlpha(16, highlightScale),255,255,255),.24),
            new(Color.FromArgb(ScaleAlpha(24, highlightScale),18,28,44),.58), new(Color.FromArgb(ScaleAlpha(42, highlightScale),255,255,255),1)
        }, new Point(_light * .5,0), new Point(1 - _light * .5,1));
        var rimBounds = rect;
        rimBounds.Inflate(-.55,-.55);
        if (rimBounds.Width > 0 && rimBounds.Height > 0)
        {
            dc.DrawGeometry(null, new Pen(rim,.8 * highlightScale), CreatePanel(rimBounds, Math.Max(0,radius-.55)));
            // Unequal light lobes on the same curved rim: bright upper shoulder,
            // a narrow left glint and a weaker lower-right reflected light.
            DrawGlint(new Point(.12 + _light * .35, .015), .40, .23, 198, 1.15);
            DrawGlint(new Point(.015, .27), .10, .33, 104, 1.0);
            DrawGlint(new Point(.96, .80), .17, .30, 118, .85);
        }
        var innerBounds = rect;
        innerBounds.Inflate(-2.05,-2.05);
        if (innerBounds.Width > 0 && innerBounds.Height > 0)
        {
            dc.DrawGeometry(null,new Pen(reflection,2.4 * highlightScale),CreatePanel(innerBounds,Math.Max(0,radius-2.05)));
            var inner = new LinearGradientBrush(Color.FromArgb(ScaleAlpha(16, highlightScale),20,32,48),Color.FromArgb(ScaleAlpha(18, highlightScale),255,255,255),90);
            dc.DrawGeometry(null,new Pen(inner,.75),CreatePanel(innerBounds,Math.Max(0,radius-2.05)));
        }
        if (LiquidGlassDebugState.ShowPerformanceOverlay) DrawMetricsOverlay(dc, rect);
        dc.Pop();

        void DrawGlint(Point center, double rx, double ry, byte alpha, double thickness)
        {
            var brush = new RadialGradientBrush
            {
                Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(ScaleAlpha(alpha, highlightScale),255,255,255),0),
                    new(Color.FromArgb(ScaleAlpha(alpha * .4, highlightScale),235,245,255),.45),
                    new(Colors.Transparent,1)
                }
            };
            dc.DrawGeometry(null, new Pen(brush, thickness), CreatePanel(rimBounds, Math.Max(0,radius-.55)));
        }
    }

    private void DrawCompatibilityNotice(DrawingContext dc, Rect rect, string text, bool dark)
    {
        if (rect.Height < 120 || rect.Width < 300) return;
        var label = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 11,
            dark ? Brushes.WhiteSmoke : Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(Math.Max(rect.Left + 12, rect.Right - label.Width - 16), rect.Bottom - label.Height - 12));
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index switch
        { 0 => _transitionLayer, 1 => _compatibilityLayer, _ => throw new ArgumentOutOfRangeException(nameof(index)) };

    protected override Size MeasureOverride(Size availableSize)
    {
        _transitionLayer.Measure(availableSize);
        _compatibilityLayer.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateTransitionLayer(finalSize);
        _transitionLayer.Arrange(_transitionBackdropBounds.Width > 0
            ? _transitionBackdropBounds : new Rect(finalSize));
        _compatibilityLayer.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void UpdateTransitionLayer(Size? layoutSize = null)
    {
        var size = layoutSize ?? RenderSize;
        var panel = _panelShape ?? new Rect(size);
        var radius = _panelShape is null ? (Window.GetWindow(this) as GlassWindow)?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius;
        var opticalMatches = _frame is not null && _sampledGlassPanelBounds == panel && _sampledGlassPanelRadius == radius;
        var enabled = (_transitionCaptureBounds is not null || !opticalMatches)
            && !_background.RequiresBasicBackground
            && _transitionBackdrop is not null && _transitionBackdropBounds.Contains(panel)
            && LiquidGlassDebugState.View == LiquidGlassDebugView.None
            && ThemeService.Current?.UseOpaquePanels != true
            && (ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass)
                is GlassMaterialMode.AndroidLiquidGlass or GlassMaterialMode.FrostedGlass;
        _transitionLayer.SetBackdrop(enabled ? _transitionBackdrop : null);
        _transitionLayer.Opacity = enabled ? 1 : 0;
        var frosted = ThemeService.Current?.Appearance.GlassMaterial == GlassMaterialMode.FrostedGlass;
        if (enabled) _transitionLayer.SetPanel(panel, _transitionBackdropBounds, radius, .6 + .4 * _transitionProgress, UseLaunchpadOptics, frosted);
        var liveReady = !_background.RequiresBasicBackground && (enabled || opticalMatches);
        var needsCompatibility = ThemeService.Current?.UseOpaquePanels != true
            && !BackdropRecovery.RequiresBasicRendering && LiquidGlassDebugState.View == LiquidGlassDebugView.None
            && (!liveReady || _handoffStarted != 0);
        if (needsCompatibility && size.Width > 0 && size.Height > 0)
        {
            PrepareCompatibleMaterial(size);
            var opacity = !liveReady || _handoffStarted == 0 ? 1
                : Math.Clamp(1 - Stopwatch.GetElapsedTime(_handoffStarted).TotalMilliseconds / 100, 0, 1);
            _compatibilityLayer.SetBackdrop(_compatibilityBackdrop);
            if (_compatibilityLayer.Clip is null || _compatibilityClipKey != (panel, radius))
            {
                _compatibilityClipKey = (panel, radius);
                _compatibilityLayer.Clip = _panelGeometry ?? (Window.GetWindow(this) is GlassWindow glass
                    ? glass.CreateGlassPanelGeometry(panel, radius) : SquircleGlassGeometry.Create(panel, radius));
            }
            _compatibilityLayer.SetPanel(panel, _compatibilityBounds, radius, .6 + .4 * _transitionProgress, UseLaunchpadOptics, frosted);
            _compatibilityLayer.Opacity = opacity;
            if (opacity == 0)
            {
                _handoffStarted = 0; _compatibilityBackdrop = null; _compatibilityStamp = null;
                _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null;
                _compatibilityLayer.SetBackdrop(null);
            }
        }
        else { _compatibilityLayer.Opacity = 0; _compatibilityLayer.SetBackdrop(null); }
    }

    private static void RenderMaterialBackdropInto(GlassMaterialMode material, byte[] source, byte[] pixels,
        byte[] scratch, int width, int height, Size size, bool dark, double strength, double clarity, bool launchpad)
    {
        if (material == GlassMaterialMode.FrostedGlass)
            AndroidLiquidGlassRenderer.RenderFrostedBackdropInto(source, pixels, scratch, width, height, size, dark, strength, clarity);
        else
            AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source, pixels, scratch, width, height, size, dark, strength,
                backgroundClarity: clarity, launchpadOptics: launchpad);
    }

    private MaterialStamp ReadMaterialStamp()
    {
        var origin = new Point();
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_revealOrigin is { } preparedOrigin && Window.GetWindow(this) is { IsVisible: false }) origin = preparedOrigin;
        else if (PresentationSource.FromVisual(this) is not null) origin = PointToScreen(new Point());
        else if (Window.GetWindow(this) is { } owner && PresentationSource.FromVisual(owner) is not null)
        {
            // Before the first Show the surface is not connected yet, but its
            // hidden HWND already has final screen coordinates and monitor DPI.
            origin = owner.PointToScreen(new Point()); dpi = VisualTreeHelper.GetDpi(owner);
        }
        return new(origin, _transitionCaptureBounds ?? CaptureBoundsInWindow ?? new Rect(RenderSize),
            dpi, ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass,
            ThemeService.Current?.IsDark != false, BackgroundClarity, UseLaunchpadOptics,
            LiquidGlassDebugState.SamplingParameters, LiquidGlassDebugState.WeightParameters, LiquidGlassDebugState.LodParameters,
            LiquidGlassDebugState.FinishingParameters, LiquidGlassDebugState.BrdfParameters,
            LiquidGlassDebugState.DispersionParameters, GlassDesk.UI.BackdropRecovery.Generation);
    }

    private void PrepareCompatibleMaterial(Size size)
    {
        var stamp = ReadMaterialStamp();
        var bounds = new Rect(size);
        if (_compatibilityBackdrop is not null && _compatibilityStamp == stamp && _compatibilityBounds == bounds) return;
        if (_compatibilityIsDesktopSnapshot) RevealMaterialInvalidationReason = $"origin {_compatibilityStamp?.Origin} => {stamp.Origin}; dpi {_compatibilityStamp?.Dpi.DpiScaleX} => {stamp.Dpi.DpiScaleX}; bounds {_compatibilityBounds} => {bounds}; capture {_compatibilityStamp?.CaptureBounds} => {stamp.CaptureBounds}";
        _compatibilityStamp = stamp; _compatibilityBounds = bounds;
        // A small, fully opaque ambient texture is immediately available. It contains
        // no screen pixels and enters the exact production lens, tint and blur path.
        const int width = 32, height = 24;
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var variation = (byte)(12 * x / width + 14 * y / height);
            source[i] = (byte)((stamp.Dark ? 51 : 208) + variation);
            source[i + 1] = (byte)((stamp.Dark ? 43 : 205) + variation);
            source[i + 2] = (byte)((stamp.Dark ? 37 : 200) + variation);
            source[i + 3] = 255;
        }
        var filtered = new byte[source.Length]; var scratch = new byte[source.Length];
        RenderMaterialBackdropInto(stamp.Material, source, filtered, scratch, width, height,
            bounds.Size, stamp.Dark, 1, stamp.Clarity, stamp.Launchpad);
        var compatible = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, filtered, width * 4);
        compatible.Freeze(); _compatibilityBackdrop = compatible;
        _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null;
        _handoffStarted = 0;
        // A static Windows wallpaper can differ completely from the composed scene
        // (including dynamic wallpapers). Do not flash it over the launchpad.
        if (UseLaunchpadOptics || IsBackdropActive is not null) return;
        if (_performanceSleeping || _wallpaperPreparationPending || PresentationSource.FromVisual(this) is null) return;
        PrepareWallpaperMaterial(stamp, bounds);
    }

    /// <summary>Prepare current, fully filtered glass before a hidden launcher becomes visible.</summary>
    public void PrepareRevealMaterial(PixelRect host, PixelRect panel, double dpi)
    {
        if (Window.GetWindow(this) is not { } revealOwner
            || revealOwner.IsVisible && IsBackdropActive?.Invoke() != false || ThemeService.Current?.UseOpaquePanels == true
            || BackdropRecovery.RequiresBasicRendering || host.Width <= 0 || host.Height <= 0) return;
        var size = new Size(host.Width * 96d / dpi, host.Height * 96d / dpi);
        _revealOrigin = new Point(host.X, host.Y);
        _revealHost = host;
        Measure(size); Arrange(new Rect(size));
        EnsureBackdropScene(revealing: true);
        using var primed = PrimeRevealCapture(host, dpi, out var primedRegion);
        if (_frame is not null || _transitionBackdrop is not null) return;
        if (_compatibilityIsDesktopSnapshot && _compatibilityScene?.Equals(_backdropScene) == true
            && _compatibilityStamp == ReadMaterialStamp()) return;
        var region = new PixelRect(Math.Max(host.X, panel.X), Math.Max(host.Y, panel.Y),
            Math.Min(host.Right, panel.Right) - Math.Max(host.X, panel.X),
            Math.Min(host.Bottom, panel.Bottom) - Math.Max(host.Y, panel.Y));
        if (region.Width <= 0 || region.Height <= 0) return;
        var scale = Math.Min(1, Math.Min(2048d / Math.Max(host.Width, host.Height),
            Math.Sqrt(320_000d / ((double)host.Width * host.Height))));
        var width = Math.Max(1, (int)Math.Floor(host.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(host.Height * scale));
        var captureWidth = Math.Max(1, (int)Math.Floor(region.Width * scale));
        var captureHeight = Math.Max(1, (int)Math.Floor(region.Height * scale));
        var scene = _backdropScene;
        var pixels = CaptureRevealBackdrop(region, captureWidth, captureHeight);
        var usedNative = false;
        // The existing prewarm owner already excludes every product window. A
        // launcher/morph can make GDI unsafe while this native frame is available;
        // consume it instead of dropping it and exposing an ambient first frame.
        if ((pixels is null || pixels.Length != captureWidth * captureHeight * 4 || IsSolidBlack(pixels))
            && !UseLaunchpadOptics && primed is { IsCaptured: true }
            && primed.Source is GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene
            && _background.LastCaptureTiming is { } timing
            && Stopwatch.GetTimestamp() - timing.SourceTicks is var age && age >= 0 && age <= Stopwatch.Frequency * 120 / 1000)
        {
            usedNative = true;
            pixels = new byte[captureWidth * captureHeight * 4];
            for (var y = 0; y < captureHeight; y++)
            {
                var sy = Math.Clamp((int)((region.Y + (y + .5) * region.Height / captureHeight - primedRegion.Y)
                    * primed.Height / primedRegion.Height), 0, primed.Height - 1);
                for (var x = 0; x < captureWidth; x++)
                {
                    var sx = Math.Clamp((int)((region.X + (x + .5) * region.Width / captureWidth - primedRegion.X)
                        * primed.Width / primedRegion.Width), 0, primed.Width - 1);
                    primed.Pixels.AsSpan((sy * primed.Width + sx) * 4, 4).CopyTo(pixels.AsSpan((y * captureWidth + x) * 4, 4));
                }
            }
        }
        if (pixels is null || pixels.Length != captureWidth * captureHeight * 4 || (!usedNative && IsSolidBlack(pixels))
            || scene is null || !scene.Equals(ReadBackdropScene())) return;
        // Extend only outside the destination panel. The Dock lies outside this
        // capture and cannot be burnt into the expanding or resting launchpad.
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((int)((host.Y + (y + .5) * host.Height / height - region.Y)
                * captureHeight / region.Height), 0, captureHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((int)((host.X + (x + .5) * host.Width / width - region.X)
                    * captureWidth / region.Width), 0, captureWidth - 1);
                pixels.AsSpan((sy * captureWidth + sx) * 4, 4).CopyTo(source.AsSpan((y * width + x) * 4, 4));
            }
        }
        var stamp = ReadMaterialStamp();
        var filtered = new byte[source.Length]; var scratch = new byte[source.Length];
        RenderMaterialBackdropInto(stamp.Material, source, filtered, scratch, width, height,
            size, stamp.Dark, 1, stamp.Clarity, stamp.Launchpad);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, filtered, width * 4);
        bitmap.Freeze();
        _wallpaperCancellation?.Cancel();
        _compatibilityBackdrop = bitmap; _compatibilityStamp = stamp; _compatibilityBounds = new Rect(size);
        _compatibilityScene = scene; _compatibilityIsDesktopSnapshot = true; _handoffStarted = 0;
        ++_preparedDesktopSnapshots; RevealMaterialInvalidationReason = null;
        UpdateTransitionLayer(size); InvalidateVisual();
    }

    private byte[]? CaptureHiddenDesktop(PixelRect region, int width, int height)
    {
        using var sampler = new GlassDesk.Platform.DesktopRegionSampler();
        var owner = Window.GetWindow(this);
        var emptyOwner = owner is { IsVisible: true } && IsBackdropActive?.Invoke() == false
            ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : 0;
        return sampler.TryCaptureBeforeReveal(region, width, height, emptyOwner, out var pixels) ? pixels : null;
    }

    private GlassBackgroundFrame? PrimeRevealCapture(PixelRect host, double dpi, out PixelRect region)
    {
        region = default;
        if (_performanceSleeping || Window.GetWindow(this) is not GlassWindow owner
            || owner.SurfaceMode != GlassSurfaceMode.OpticalGlass) return null;
        var scaleFactor = dpi / 96;
        var bounds = _transitionCaptureBounds ?? new Rect(0, 0, host.Width / scaleFactor, host.Height / scaleFactor);
        // Use the full fixed slide/morph capture bounds, not just the resting
        // panel. Otherwise the visible frame changes resolution/generation and
        // throws away the prewarmed snapshot on wide Docks.
        var left = (int)Math.Floor(host.X + (bounds.Left - 48) * scaleFactor);
        var top = (int)Math.Floor(host.Y + (bounds.Top - 48) * scaleFactor);
        var right = (int)Math.Ceiling(host.X + (bounds.Right + 48) * scaleFactor);
        var bottom = (int)Math.Ceiling(host.Y + (bounds.Bottom + 48) * scaleFactor);
        var requested = new PixelRect(left, top, right - left, bottom - top);
        var display = owner.PrimaryDisplaySession?.Display ?? DisplayService.Primary();
        if (display is null) return null;
        region = ClipRegion(requested, display.Bounds);
        if (region.Width <= 0 || region.Height <= 0) return null;
        var budget = UseLaunchpadOptics
            ? 320_000 + 680_000 * Math.Clamp((BackgroundClarity - .5) * 2, 0, 1) : 320_000;
        var scale = Math.Min(1, Math.Min(2048d / Math.Max(requested.Width, requested.Height),
            Math.Sqrt(budget / ((double)requested.Width * requested.Height))));
        // Start the existing isolated owner before GDI and filtering block the
        // Dispatcher. The first visible render uses this same crop and sample size.
        var pending = _background.Capture(region, Math.Max(1, (int)Math.Floor(region.Width * scale)),
            Math.Max(1, (int)Math.Floor(region.Height * scale)), _panelRadius, true);
        _revealCaptureUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        _timer.Interval = TimeSpan.FromMilliseconds(100); _timer.Start();
        return pending;
    }

    private void PrepareWallpaperMaterial(MaterialStamp stamp, Rect bounds)
    {
        _wallpaperPreparationPending = true;
        var cancellation = new CancellationTokenSource();
        _wallpaperCancellation = cancellation;
        var token = cancellation.Token;
        var generation = _lifecycleGeneration;
        var scene = _backdropScene;
        var region = new Rect(stamp.Origin.X, stamp.Origin.Y,
            bounds.Width * stamp.Dpi.DpiScaleX, bounds.Height * stamp.Dpi.DpiScaleY);
        var scale = Math.Min(1, Math.Min(2048.0 / Math.Max(region.Width, region.Height),
            Math.Sqrt(320_000 / (region.Width * region.Height))));
        var width = Math.Max(1, (int)Math.Floor(region.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(region.Height * scale));
        var preparation = new Thread(() =>
        {
            BitmapSource? result = null;
            try
            {
                var wallpaper = new WallpaperBackdrop();
                if (!token.IsCancellationRequested) wallpaper.Update(region, width, height, 0);
                var pixels = wallpaper.Pixels;
                // A hidden black Windows wallpaper is common under dynamic wallpaper.
                // Never show that bitmap as a replacement for the visible background.
                if (!token.IsCancellationRequested && pixels is not null && !IsSolidBlack(pixels))
                {
                    var filtered = new byte[pixels.Length]; var scratch = new byte[pixels.Length];
                    RenderMaterialBackdropInto(stamp.Material, pixels, filtered, scratch, width, height,
                        bounds.Size, stamp.Dark, 1, stamp.Clarity, stamp.Launchpad);
                    result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, filtered, width * 4);
                    result.Freeze();
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException
                or ArgumentException or NotSupportedException)
            { Trace.WriteLine("Compatible first material: " + error.Message); }
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { cancellation.Dispose(); return; }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (ReferenceEquals(_wallpaperCancellation, cancellation)) _wallpaperCancellation = null;
                cancellation.Dispose();
                if (generation != _lifecycleGeneration) return;
                _wallpaperPreparationPending = false;
                if (token.IsCancellationRequested || _compatibilityIsDesktopSnapshot || _performanceSleeping || result is null || stamp != ReadMaterialStamp()
                    || scene is null || !scene.Equals(_backdropScene) || _handoffStarted != 0
                    || _frame is not null || _transitionBackdrop is not null) return;
                _compatibilityBackdrop = result; UpdateTransitionLayer(); InvalidateVisual();
            }));
        }) { IsBackground = true, Name = "GlassDesk first wallpaper material" };
        preparation.SetApartmentState(ApartmentState.STA); preparation.Start();
    }

    private static bool IsSolidBlack(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) return false;
        return true;
    }

    private bool CanCommitCapturedScene(GlassBackdropScene? capturedScene, MaterialStamp? capturedStamp, long sourceTicks)
    {
        if (capturedScene is null || capturedStamp != ReadMaterialStamp()) return false;
        if (capturedScene.Equals(_backdropScene)) return true;
        // The acquired snapshot has already passed native exclusion and scene
        // barriers. An external window moving during CPU composition does not
        // invalidate its coordinates. Keep its real scene/timestamp and require
        // the same 120 ms freshness ceiling used by the capture consumer.
        var age = Stopwatch.GetTimestamp() - sourceTicks;
        return _backdropScene?.CanHandoffMaterialFrom(capturedScene) == true && sourceTicks > 0
            && age >= 0 && age <= Stopwatch.Frequency * 120 / 1000;
    }

    private void EnsureBackdropScene(bool revealing = false)
    {
        var scene = ReadBackdropScene();
        var stamp = ReadMaterialStamp();
        var snapshotDiscarded = false;
        if (_compatibilityIsDesktopSnapshot && (scene is null || !scene.Equals(_compatibilityScene)))
        {
            var visibleHandoff = !UseLaunchpadOptics && !revealing && scene is not null && stamp == _compatibilityStamp
                && Window.GetWindow(this)?.IsVisible == true && IsVisible && IsBackdropActive?.Invoke() != false
                && scene.CanHandoffMaterialFrom(_compatibilityScene);
            if (visibleHandoff && _materialRetentionUntil == 0)
                _materialRetentionUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            if (!visibleHandoff || Stopwatch.GetTimestamp() >= _materialRetentionUntil)
            {
                RecordMaterialRetirement(scene is null ? "SceneUnknown" : "SnapshotHandoffExpired");
                RevealMaterialInvalidationReason = "desktop scene changed";
                _compatibilityBackdrop = null; _compatibilityStamp = null;
                _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null; _handoffStarted = 0;
                snapshotDiscarded = true;
            }
        }
        if ((_frame is not null || _transitionBackdrop is not null) && _materialStamp is not null && _materialStamp != stamp)
        {
            RecordMaterialRetirement("MaterialStampChanged");
            _frame = null; _frameScene = null; _transitionBackdrop = null; _transitionBackdropScene = null;
            _worker.Invalidate(); _lastSource = null; _handoffStarted = 0;
        }
        if (_frame is not null) _frameScene ??= _backdropScene;
        if (_transitionBackdrop is not null) _transitionBackdropScene ??= _backdropScene;
        var continuingVisibleCapture = !revealing && Window.GetWindow(this)?.IsVisible == true && IsVisible
            && IsBackdropActive?.Invoke() != false && scene?.CanHandoffMaterialFrom(_backdropScene) == true;
        if (scene is null || !scene.Equals(_backdropScene))
        {
            _backdropScene = scene;
            // The native producer already tracks external HWND scene revisions.
            // Cancelling it and the CPU stage on every move starves both stages.
            // Unknown scenes, reveal and recovery still require a cold boundary.
            if (!continuingVisibleCapture)
            {
                _worker.Invalidate();
                _revealCaptureUntil = 0;
                _background.Suspend();
            }
            _lastSource = null;
        }
        // Fresh acquisition still follows every scene boundary. A continuously
        // visible surface keeps its complete, self-excluded material while the
        // replacement is acquired and composed, including window close/minimize.
        // The existing 100 ms ceiling is not extended by repeated scene changes;
        // revealing, unknown scenes, geometry changes and recovery never reuse it.
        var discarded = false;
        bool Reusable(GlassBackdropScene? materialScene)
        {
            if (scene is null) return false;
            if (scene.Equals(materialScene)) return true;
            var visibleHandoff = Window.GetWindow(this)?.IsVisible == true && IsVisible
                && IsBackdropActive?.Invoke() != false && scene.CanHandoffMaterialFrom(materialScene);
            if (revealing || !scene.CanRetainMaterialFrom(materialScene) && !visibleHandoff) return false;
            var now = Stopwatch.GetTimestamp();
            if (_materialRetentionUntil == 0) _materialRetentionUntil = now + Stopwatch.Frequency / 10;
            return now < _materialRetentionUntil;
        }
        if (_frame is not null && !Reusable(_frameScene))
        {
            RecordMaterialRetirement(scene is null ? "SceneUnknown" : "SceneHandoffExpired");
            _frame = null; _frameScene = null; discarded = true;
        }
        if (_transitionBackdrop is not null && !Reusable(_transitionBackdropScene))
        {
            RecordMaterialRetirement(scene is null ? "SceneUnknown" : "SceneHandoffExpired");
            _transitionBackdrop = null; _transitionBackdropScene = null; discarded = true;
        }
        if (!discarded && !snapshotDiscarded) return;
        _compatibilityBackdrop = null; _compatibilityStamp = null; _handoffStarted = 0;
        // Expiring the displayed texture does not cancel a newer in-flight frame.
        // Its source age, recovery generation and capture pose are checked on commit.
        if (!continuingVisibleCapture) _worker.Invalidate();
        _lastSource = null;
        if (_frame is null && _transitionBackdrop is null) BackdropSource = GlassBackdropSource.Transparent;
        UpdateTransitionLayer(); InvalidateVisual();
    }

    private void RecordMaterialRetirement(string reason, bool expected = false)
    {
        if (_frame is null && _transitionBackdrop is null && (!_compatibilityIsDesktopSnapshot || _compatibilityBackdrop is null)) return;
        var visible = Window.GetWindow(this)?.IsVisible == true && IsVisible;
        var active = IsBackdropActive?.Invoke() != false;
        RecordSurfaceTrace(!expected && visible && active ? "MaterialLoss" : "MaterialState", reason, _diagnosticMaterial);
    }

    private void RecordSurfaceTrace(string stage, string reason, string? source)
    {
        var timing = _background.LastCaptureTiming;
        BackdropRecovery.Record(new(stage, reason, Stopwatch.GetTimestamp(), timing?.SourceTicks ?? 0,
            Generation: BackdropRecovery.Generation, SurfaceId: _diagnosticId, Surface: UseLaunchpadOptics ? "Launcher" : "Dock",
            MaterialSource: source, Visible: Window.GetWindow(this)?.IsVisible == true && IsVisible,
            Active: IsBackdropActive?.Invoke() != false, Sleeping: _performanceSleeping,
            SceneHash: _backdropScene?.GetHashCode() ?? 0, Width: (int)Math.Max(0, ActualWidth), Height: (int)Math.Max(0, ActualHeight)));
    }

    private GlassBackdropScene? CaptureBackdropScene()
    {
        if (Window.GetWindow(this) is not GlassWindow owner) return GlassBackdropScene.Capture();
        var bounds = !owner.IsVisible && _revealHost is { } prepared ? prepared
            : DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
        if (bounds is not { Width: > 0, Height: > 0 } pixels) return GlassBackdropScene.Capture();
        // Include the owner's entire animation path and capture guard band, then
        // use the same primary-monitor boundary as desktop acquisition.
        var dpi = VisualTreeHelper.GetDpi(owner);
        var marginX = (int)Math.Ceiling(64 * dpi.DpiScaleX);
        var marginY = (int)Math.Ceiling(64 * dpi.DpiScaleY);
        var region = new PixelRect(pixels.X - marginX, pixels.Y - marginY,
            pixels.Width + 2 * marginX, pixels.Height + 2 * marginY);
        if (owner.PrimaryDisplaySession is { IsAvailable: true } session) region = session.ClipToPrimaryBounds(region);
        return GlassBackdropScene.Capture(region);
    }

    private static void DrawWaitingRim(DrawingContext dc, Geometry panel, double strength)
    {
        // Keep the material's directional edge visible while a changed scene supplies
        // its first texture. This stroke contains no pixels from the previous window.
        var rim = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(ScaleAlpha(100, strength), 255, 255, 255), 0),
            new(Color.FromArgb(ScaleAlpha(20, strength), 255, 255, 255), .4),
            new(Color.FromArgb(ScaleAlpha(75, strength), 12, 17, 27), 1),
        }, new Point(0, 0), new Point(1, 1));
        dc.DrawGeometry(null, new Pen(rim, 1.5 * strength), panel);
    }

    private void SetTimerInterval(TimeSpan interval)
    {
        _refreshInterval = _handoffStarted != 0 ? GlassBackgroundLayer.LiveFrameInterval : interval;
        // A 16 ms DispatcherTimer can wake only every second display frame because
        // its Windows timer is rounded. Live updates follow WPF's render clock;
        // the timer only wakes idle modes or recovers a temporarily silent clock.
        var watchdog = interval <= GlassBackgroundLayer.LiveFrameInterval ? TimeSpan.FromMilliseconds(100) : interval;
        if (_timer.Interval != watchdog) _timer.Interval = watchdog;
        UpdateRenderClock();
        if (!_performanceSleeping && IsLoaded && IsVisible && IsBackdropActive?.Invoke() != false) _timer.Start();
    }

    private void UpdateRenderClock()
    {
        var active = !_performanceSleeping && IsLoaded && IsVisible && IsBackdropActive?.Invoke() != false
            && _refreshInterval <= GlassBackgroundLayer.LiveFrameInterval;
        if (active && !_renderClockAttached)
        {
            _lastRenderingTime = TimeSpan.MinValue; _lastRenderingTicks = 0;
            CompositionTarget.Rendering += OnRenderingFrame; _renderClockAttached = true;
        }
        else if (!active) StopRenderClock();
    }

    private void StopRenderClock()
    {
        if (_renderClockAttached) CompositionTarget.Rendering -= OnRenderingFrame;
        _renderClockAttached = false;
        _renderFrameOperation?.Abort(); _renderFrameOperation = null; _renderFrameQueued = false;
        _lastRenderingTicks = 0;
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        if (_performanceSleeping) return;
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        _lastRenderingTicks = Stopwatch.GetTimestamp();
        // Rendering only signals one update. CPU optics run off the dispatcher;
        // acquisition and its completion must not wait behind sustained input.
        if (_renderFrameQueued)
        {
            // Give the periodic background yield one display interval. Sustained
            // mouse input must not pin it there and block all subsequent frames.
            if (_renderFrameOperation is { Status: DispatcherOperationStatus.Pending, Priority: DispatcherPriority.Background })
                _renderFrameOperation.Priority = DispatcherPriority.Normal;
            return;
        }
        if (_worker.IsBusy) return;
        _renderFrameQueued = true;
        // Periodically join the background queue as well: a continuous stream at
        // Normal priority can otherwise starve timers/icon notifications.
        var priority = ++_renderFrameSignals % 8 == 0 ? DispatcherPriority.Background : DispatcherPriority.Normal;
        _renderFrameOperation = Dispatcher.BeginInvoke(priority, new Action(() =>
        {
            _renderFrameOperation = null;
            _renderFrameQueued = false;
            if (_renderClockAttached && IsLoaded && IsVisible) OnFrame(null, EventArgs.Empty);
        }));
    }

    private void OnWatchdogFrame(object? sender, EventArgs e)
    {
        if (_performanceSleeping) return;
        if (_renderClockAttached && _lastRenderingTicks != 0
            && Stopwatch.GetElapsedTime(_lastRenderingTicks) < TimeSpan.FromMilliseconds(100)) return;
        OnFrame(sender, e);
    }

    private static PixelRect ClipRegion(PixelRect rect, PixelRect bounds)
    {
        var left = Math.Max(rect.X, bounds.X);
        var top = Math.Max(rect.Y, bounds.Y);
        var right = Math.Min(rect.Right, bounds.Right);
        var bottom = Math.Min(rect.Bottom, bounds.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(Math.Clamp(left, bounds.X, bounds.Right), Math.Clamp(top, bounds.Y, bounds.Bottom), 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    private static byte ScaleAlpha(double alpha, double scale) =>
        (byte)Math.Clamp((int)Math.Round(alpha * scale), 0, 255);

    private void RefreshOpticalRefreshRate()
    {
        var cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        while (_opticalFrameTimestamps.TryPeek(out var timestamp) && timestamp < cutoff)
            _opticalFrameTimestamps.Dequeue();
        _opticalRefreshRate = _opticalFrameTimestamps.Count;
    }

    private void DrawMetricsOverlay(DrawingContext drawingContext, Rect bounds)
    {
        var text = new FormattedText(
            $"折射刷新 {_opticalRefreshRate:0} fps · CPU {_lastOpticsMilliseconds:0.00} ms",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var label = new Rect(bounds.Left + 6, bounds.Bottom - text.Height - 8, text.Width + 12, text.Height + 4);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 12, 18, 28)),
            null, label, 3, 3);
        drawingContext.DrawText(text, new Point(label.Left + 6, label.Top + 2));
    }
}
