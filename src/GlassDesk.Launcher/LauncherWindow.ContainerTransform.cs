using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    public void StopForPrimaryDisplayChange()
    {
        FinishIconDrag();
        AbortContainerTransform();
        Hide();
    }

    public Func<PixelRect?>? ContainerAnchorProvider { get; set; }
    public bool IsClosingContainer => _transformActive && _transformEndTimeline == 0;

    /// <summary>Prepares the next Dock-to-launchpad reveal while the launcher is hidden.</summary>
    public void PrewarmNextRevealMaterial()
    {
        if (IsVisible || _transformActive || _panelPixelBounds is not { Width: > 0, Height: > 0 }) return;
        var display = TransformDisplay;
        var source = ContainerAnchorProvider?.Invoke();
        if (display is null || source is not { Width: > 0, Height: > 0 }
            || !display.Bounds.Contains(source.Value.X + source.Value.Width / 2, source.Value.Y + source.Value.Height / 2)) return;

        _ = new WindowInteropHelper(this).EnsureHandle();
        PositionOnDisplay(display);
        _transformSourceBounds = source.Value;
        _transformDestinationBounds = _panelPixelBounds;
        _transformSourceRadiusPixels = Math.Min(_transformSourceBounds.Width, _transformSourceBounds.Height) / 3.0;
        _transformDestinationRadiusPixels = PanelCornerRadius.TopLeft * display.Dpi / 96.0;
        _transformDipPerPixel = 96.0 / Math.Max(1, display.Dpi);
        _panelDipPerPixel = _transformDipPerPixel;
        ExpandTransformHost();
        PrepareTransitionBackdrop();
        PrepareRevealMaterial();
        PrewarmIndependentShadow();
    }

    private PixelRect _transformSourceBounds;
    private PixelRect _transformDestinationBounds;
    private double _transformDipPerPixel = 1;
    private double _transformSourceRadiusPixels;
    private double _transformDestinationRadiusPixels;
    private double _transformEndTimeline;
    private long _transformStartedAt;
    private bool _transformAwaitingFirstFrame;
    private long _transformMaterialWaitAt;
    private ContainerMorphMotion? _containerMotion;
    private bool _transformActive;
    private bool _hasTransformPath;
    private bool _preserveExpandedHostOnHide;
    private readonly bool _retainRevealGrid = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_RETAIN_REVEAL_GRID") == "1";
    private readonly MatrixTransform _glyphRenderTransform = new();
    private TimeSpan _lastTransformRenderingTime = TimeSpan.MinValue;
    private readonly bool _useDisplayAnimationCadence = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_DISPLAY_CADENCE") == "1";

    private DisplayInfo? TransformDisplay => PrimaryDisplaySession is { } session
        ? session.IsAvailable ? session.Display : null
        : DisplayService.Primary();

    /// <summary>Show the launcher, optionally morphing from a live Dock-button rectangle.</summary>
    public void ShowWithTransition(PixelRect? sourceBounds)
    {
        StartAnimationRequest(opening: true);
        var preparationStarted = _showRequestedAt;
        sourceBounds = ContainerAnchorProvider?.Invoke() ?? sourceBounds;
        if (PrimaryDisplaySession is { IsAvailable: false }) return;
        var primary = TransformDisplay;
        if (sourceBounds is { } suppliedSource && primary is not null
            && !primary.Bounds.Contains(suppliedSource.X + suppliedSource.Width / 2,
                suppliedSource.Y + suppliedSource.Height / 2))
            sourceBounds = null;
        MarkPreparationTiming("resolve-anchor-and-display", ref preparationStarted);
        if (IsVisible)
        {
            if (_transformActive)
            {
                if (_transformEndTimeline < 1) BeginContainerTransform(1);
                return;
            }

            PositionOnWorkingArea();
            PrepareToShow();
            if (_hasTransformPath)
            {
                if (sourceBounds is { Width: > 0, Height: > 0 } existingSource)
                {
                    _transformSourceBounds = existingSource;
                    _transformSourceRadiusPixels = Math.Min(existingSource.Width, existingSource.Height) / 3.0;
                }

                var existingDestination = _panelPixelBounds.Width > 0 && _panelPixelBounds.Height > 0
                    ? _panelPixelBounds
                    : DisplayService.GetWindowPixelBounds(Handle);
                if (existingDestination is { Width: > 0, Height: > 0 })
                {
                    _transformDestinationBounds = existingDestination.Value;
                    var display = TransformDisplay;
                    _transformDestinationRadiusPixels = PanelCornerRadius.TopLeft * (display?.Dpi ?? 96) / 96.0;
                    _transformDipPerPixel = 96.0 / Math.Max(1, display?.Dpi ?? 96);
                }
            }
            return;
        }

        PositionOnDisplayCore(primary, deferHostPlacement: true);
        MarkPreparationTiming("initial-placement", ref preparationStarted);

        var handle = new WindowInteropHelper(this).EnsureHandle();
        PositionOnDisplayCore(primary, deferHostPlacement: true);
        MarkPreparationTiming("native-host-placement", ref preparationStarted);
        var destination = _panelPixelBounds.Width > 0 && _panelPixelBounds.Height > 0
            ? _panelPixelBounds
            : DisplayService.GetWindowPixelBounds(handle);
        var targetDisplay = destination is { } target && primary is not null
            && primary.Bounds.Contains(target.X + target.Width / 2, target.Y + target.Height / 2)
            ? primary : null;
        var sourceDisplay = sourceBounds is { } source && primary is not null
            && primary.Bounds.Contains(source.X + source.Width / 2, source.Y + source.Height / 2)
            ? primary : null;
        var canAnimate = ThemeService.Current?.AnimationsEnabled != false
            && sourceBounds is { Width: > 0, Height: > 0 }
            && destination is { Width: > 0, Height: > 0 }
            && targetDisplay is not null
            && sourceDisplay is not null
            && string.Equals(targetDisplay.DeviceName, sourceDisplay.DeviceName, StringComparison.OrdinalIgnoreCase);

        StopContainerTransform();
        if (!canAnimate)
        {
            _hasTransformPath = false;
            _containerMotion = null;
            if (destination is { } finalBounds) SetPixelBounds(finalBounds);
            PrepareToShow();
            SetFinalVisualState();
            PrepareRevealMaterial();
            Show();
            return;
        }

        _transformSourceBounds = sourceBounds!.Value;
        _transformDestinationBounds = destination!.Value;
        _transformSourceRadiusPixels = Math.Min(_transformSourceBounds.Width, _transformSourceBounds.Height) / 3.0;
        _transformDestinationRadiusPixels = PanelCornerRadius.TopLeft * targetDisplay!.Dpi / 96.0;
        _transformDipPerPixel = 96.0 / Math.Max(1, targetDisplay.Dpi);
        _panelDipPerPixel = _transformDipPerPixel;
        _hasTransformPath = true;
        _containerMotion = new ContainerMorphMotion(_transformSourceBounds, _transformDestinationBounds,
            _transformSourceRadiusPixels, _transformDestinationRadiusPixels);
        ExpandTransformHost();
        MarkPreparationTiming("expand-host", ref preparationStarted);
        // Restore the retired HWND while its tile tree is still empty. WPF
        // synchronizes its render channel on resize; rebuilding tiles first
        // puts that cold rendering work inside the synchronous resize.
        PrepareToShow();
        MarkPreparationTiming("library-and-tiles", ref preparationStarted);
        PrepareTransitionBackdrop();
        MarkPreparationTiming("transition-capture-bounds", ref preparationStarted);
        LauncherContent.IsHitTestVisible = false;
        // Establish WPF geometry for material/layout without constructing an
        // independent shadow that unified presentation will immediately replace.
        ApplyContainerTransformFrameCore(_containerMotion.CurrentFrame, updateOutput: false);
        MarkPreparationTiming("prepare-morph-geometry", ref preparationStarted);
        PrepareRevealMaterial();
        MarkPreparationTiming("prepare-reveal-material", ref preparationStarted);
        PrepareUnifiedTransition();
        MarkPreparationTiming("prepare-unified-transition", ref preparationStarted);
        Show();
        MarkPreparationTiming("show-window", ref preparationStarted);
        BeginContainerTransform(1);
    }

    /// <summary>Reverse the current shared transition and shrink the panel into its Dock button.</summary>
    public void CloseWithTransition()
    {
        if (!IsVisible) return;
        if (_transformActive && _transformEndTimeline <= 0) return;
        StartAnimationRequest(opening: false);
        var preparationStarted = _animationRequestStartedAt;
        StopFilterTransition();
        StopPageSlide();
        if (!_hasTransformPath || ThemeService.Current?.AnimationsEnabled == false)
        {
            StopContainerTransform();
            Hide();
            SetFinalVisualState();
            return;
        }

        RefreshContainerAnchor();
        ExpandTransformHost();
        LauncherContent.IsHitTestVisible = false;
        MarkPreparationTiming("close-anchor-and-host", ref preparationStarted);
        BeginContainerTransform(0);
    }

    private void BeginContainerTransform(double targetTimeline)
    {
        if (!_hasTransformPath || _containerMotion is null) return;
        var preparationStarted = Stopwatch.GetTimestamp();
        StopPageSlide();
        PrepareTransitionBackdrop();
        AdvanceContainerMotion();
        StopContainerTransform("reversed");
        _transformEndTimeline = Math.Clamp(targetTimeline, 0, 1);
        if (_transformEndTimeline >= 1) _unifiedClosingMaterial = null;
        _containerMotion.Retarget(_transformEndTimeline >= 1, _transformSourceBounds);
        CancelUnifiedTransfer();
        MarkPreparationTiming("begin-retarget", ref preparationStarted);
        if (!_unifiedActive) PrepareUnifiedTransition();
        MarkPreparationTiming("begin-unified", ref preparationStarted);
        // Preparation (or the reversal advance) already submitted this frame.
        // A duplicate synchronous Present consumes another swapchain admission
        // before the clock can begin the first moving frame.
        ApplyContainerTransformFrameCore(_containerMotion.CurrentFrame, updateOutput: !_unifiedActive || !_unifiedFrameSubmitted);
        MarkPreparationTiming("begin-first-frame", ref preparationStarted);
        if (_containerMotion.IsSettled)
        {
            CompleteContainerTransform(_transformEndTimeline);
            return;
        }

        _transformStartedAt = Stopwatch.GetTimestamp();
        _transformMaterialWaitAt = _transformEndTimeline >= 1 && !GlassSurface.IsCurrentRevealMaterialReady
            ? _transformStartedAt : 0;
        _transformAwaitingFirstFrame = true;
        _transformActive = true;
        if (_useDisplayAnimationCadence)
            GlassSurface.SetContainerAnimationCadence(UseHighRefreshCapture, DisplayService.RefreshForWindow(Handle)?.Hertz);
        else GlassSurface.SetContainerAnimationActive(UseHighRefreshCapture);
        BeginAnimationTrace(_transformEndTimeline >= 1);
        _lastTransformRenderingTime = TimeSpan.MinValue;
        if (!StartUnifiedClock()) CompositionTarget.Rendering += OnContainerTransformRendering;
        NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnContainerTransformRendering(object? sender, EventArgs e)
    {
        if (!_transformActive) return;
        // Layout can cause Rendering to fire twice for the same composition timestamp.
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastTransformRenderingTime) return;
            _lastTransformRenderingTime = rendering.RenderingTime;
        }
        var frameStarted = Stopwatch.GetTimestamp();
        RefreshContainerAnchor();
        AdvanceContainerMotion();
        _animationTrace?.Record(frameStarted, _frameWorkTiming);
        if (_containerMotion?.IsSettled == true && (!_unifiedActive || _unifiedFrameSubmitted))
            CompleteContainerTransform(_transformEndTimeline);
    }

    private void AdvanceContainerMotion()
    {
        if (!_transformActive || _containerMotion is null) return;
        var now = Stopwatch.GetTimestamp();
        if (_transformEndTimeline >= 1 && _unifiedSourceWaitAt != 0 && _transformMaterialWaitAt == 0)
            _transformMaterialWaitAt = now;
        if (_transformMaterialWaitAt != 0)
        {
            var ready = GlassSurface.IsCurrentRevealMaterialReady;
            if (!ready && Stopwatch.GetElapsedTime(_transformMaterialWaitAt, now).TotalSeconds < 3)
            {
                // TransparentWhileLoading intentionally paints no glass until a
                // current source arrives. Do not spend the opening off screen.
                _transformAwaitingFirstFrame = true;
                _transformStartedAt = now;
                if (!_unifiedActive) ApplyContainerTransformFrame(_containerMotion.CurrentFrame);
                return;
            }
            _transformMaterialWaitAt = 0;
            if (ready && !_unifiedActive)
            {
                PrepareUnifiedTransition();
                if (!_unifiedClockDriving && StartUnifiedClock())
                    CompositionTarget.Rendering -= OnContainerTransformRendering;
            }
            _transformAwaitingFirstFrame = true;
            now = Stopwatch.GetTimestamp();
        }
        // Start the timeline at the first render callback. Cold WPF/driver wake
        // can delay dispatch long enough to consume the whole opening unseen.
        if (!_transformAwaitingFirstFrame)
            _containerMotion.Advance(Stopwatch.GetElapsedTime(_transformStartedAt, now).TotalSeconds);
        _transformAwaitingFirstFrame = false;
        _transformStartedAt = now;
        ApplyContainerTransformFrame(_containerMotion.CurrentFrame);
    }

    private void ApplyContainerTransformFrame(ContainerMorphFrame frame) => ApplyContainerTransformFrameCore(frame, true);

    private void ApplyContainerTransformFrameCore(ContainerMorphFrame frame, bool updateOutput)
    {
        _panelLayoutCurrent = false;
        var timed = _animationTrace is not null;
        var started = timed ? Stopwatch.GetTimestamp() : 0;
        var boundsInHost = new Rect(
            (frame.Bounds.X - _transformHostBounds.X) * _transformDipPerPixel,
            (frame.Bounds.Y - _transformHostBounds.Y) * _transformDipPerPixel,
            frame.Bounds.Width * _transformDipPerPixel,
            frame.Bounds.Height * _transformDipPerPixel);
        var radiusInHost = frame.CornerRadius * _transformDipPerPixel;
        _transitionPanelBoundsDip = boundsInHost;
        _transitionPanelRadiusDip = radiusInHost;
        var panelGeometry = SquircleGlassGeometry.CreateCompact(boundsInHost, radiusInHost);
        ContainerTransformClipRoot.Clip = panelGeometry;
        SetSharedPanelGeometry(panelGeometry);
        var geometryEnd = timed ? Stopwatch.GetTimestamp() : 0;
        GlassSurface.SetTransitionFrame(boundsInHost, radiusInHost, panelGeometry, frame.GlassStrength, frame.BlurRadius);
        var glassEnd = timed ? Stopwatch.GetTimestamp() : 0;
        if (updateOutput && (!_unifiedActive || !TryDrawUnifiedFrame(frame)))
        {
            if (_unifiedShadowOnly && _unifiedPresenter is not null) DrawUnifiedShadow(.35 + .65 * frame.GlassStrength);
            else SetTransitionShadowShape(boundsInHost, radiusInHost, .35 + .65 * frame.GlassStrength, reducedDetail: true);
        }
        var shadowEnd = timed ? Stopwatch.GetTimestamp() : 0;
        Opacity = 1;
        ApplyContentReveal(frame.ContentOpacity);
        var glyphSize = Math.Min(64, Math.Min(boundsInHost.Width, boundsInHost.Height));
        _glyphRenderTransform.Matrix = new Matrix(glyphSize / 48, 0, 0, glyphSize / 48,
            boundsInHost.X + (boundsInHost.Width - glyphSize) / 2,
            boundsInHost.Y + (boundsInHost.Height - glyphSize) / 2);
        CollapsedGlyph.RenderTransform = _glyphRenderTransform;
        CollapsedGlyph.Opacity = frame.GlyphOpacity;
        if (timed) _frameWorkTiming = new(
            Stopwatch.GetElapsedTime(started, geometryEnd).TotalMilliseconds,
            Stopwatch.GetElapsedTime(geometryEnd, glassEnd).TotalMilliseconds,
            Stopwatch.GetElapsedTime(glassEnd, shadowEnd).TotalMilliseconds,
            Stopwatch.GetElapsedTime(shadowEnd).TotalMilliseconds, !_unifiedActive || _unifiedFrameSubmitted);
    }

    private void CompleteContainerTransform(double timeline)
    {
        var trace = _animationTrace;
        _animationTrace = null;
        var finalizationStarted = trace is null ? 0 : Stopwatch.GetTimestamp();
        StopContainerTransform();
        if (_containerMotion is not null)
        {
            var presented = _containerMotion.CurrentFrame;
            _containerMotion.Retarget(timeline >= 1, _transformSourceBounds);
            _containerMotion.SnapToTarget();
            if (!_unifiedActive || !_unifiedFrameSubmitted || presented != _containerMotion.CurrentFrame)
                ApplyContainerTransformFrame(_containerMotion.CurrentFrame);
        }

        if (timeline >= 1)
        {
            // Keep the transparent host stable for the reverse path. Only the shared
            // panel geometry owns input/paint; resizing a layered HWND here stalls close.
            // Keep the same desktop sampling grid and GPU lens after settling.
            // Switching to a panel-sized CPU frame moves wallpaper detail at the
            // handoff, and closing would switch back a second time.
            ClearContainerTransformShape(keepBackdrop: RenderCapability.IsPixelShaderVersionSupported(3, 0));
            LauncherContent.IsHitTestVisible = true;
            CompleteUnifiedTransition(opening: true);
            NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            trace?.Finish(GlassSurface.InspectPerformance(), "completed", Stopwatch.GetElapsedTime(finalizationStarted).TotalMilliseconds);
            return;
        }

        _preserveExpandedHostOnHide = true;
        CompleteUnifiedTransition(opening: false);
        Hide();
        SetFinalVisualState(collapseHost: false);
        _hasTransformPath = false;
        _containerMotion = null;
        trace?.Finish(GlassSurface.InspectPerformance(), "completed", Stopwatch.GetElapsedTime(finalizationStarted).TotalMilliseconds);
    }

    private void SetPixelBounds(PixelRect bounds)
    {
        var display = TransformDisplay;
        if (display is null) return;
        if (PrimaryDisplaySession is { } session) bounds = session.ClampWindowToWorkArea(bounds);
        var dipPerPixel = 96.0 / Math.Max(1, display.Dpi);
        // Move the HWND in one physical operation. Setting Width then Height
        // first forces two synchronous WPF/DWM surface resizes after retirement.
        if (Handle != 0) _ = DisplayService.MoveWindowToPixelRect(Handle, bounds);
        Width = bounds.Width * dipPerPixel;
        Height = bounds.Height * dipPerPixel;
        _transformHostBounds = bounds;
    }

    private void SetFinalVisualState(bool collapseHost = true)
    {
        if (collapseHost) CollapseTransformHost();
        ClearContainerTransformShape(keepBackdrop: _retainRevealGrid && !collapseHost
            && RenderCapability.IsPixelShaderVersionSupported(3, 0));
        Opacity = 1;
        PanelCornerRadius = new CornerRadius(Math.Max(80, ThemeService.Current?.Appearance.LauncherCornerRadius ?? 80));
        GlassSurface.TransitionProgress = 1;
        GlassSurface.TransitionBlurRadius = 0;
        CollapsedGlyph.Opacity = 0;
        LauncherContent.Opacity = 1;
        LauncherContent.IsHitTestVisible = true;
        ContentScaleTransform.ScaleX = ContentScaleTransform.ScaleY = 1;
        ApplyContentReveal(1);
    }

    private void StopContainerTransform(string outcome = "cancelled")
    {
        _transformMaterialWaitAt = 0;
        StopUnifiedClock();
        GlassSurface.SetContainerAnimationActive(false);
        FinishAnimationTrace(outcome);
        if (_transformActive)
            CompositionTarget.Rendering -= OnContainerTransformRendering;
        _transformActive = false;
    }

    private void AbortContainerTransform()
    {
        ClearUnifiedOutput();
        StopContainerTransform();
        var keepBackdrop = _retainRevealGrid && _preserveExpandedHostOnHide
            && RenderCapability.IsPixelShaderVersionSupported(3, 0);
        if (_preserveExpandedHostOnHide)
        {
            _preserveExpandedHostOnHide = false;
            SetPanelRootLayout(_panelPixelBounds, _transformHostBounds, _panelDipPerPixel);
        }
        else
        {
            CollapseTransformHost();
        }
        _hasTransformPath = false;
        _containerMotion = null;
        ClearContainerTransformShape(keepBackdrop);
        Opacity = 1;
        GlassSurface.TransitionProgress = 1;
        GlassSurface.TransitionBlurRadius = 0;
        ApplyContentReveal(1);
        LauncherContent.IsHitTestVisible = true;
    }

    private void ClearContainerTransformShape(bool keepBackdrop = false)
    {
        if (!keepBackdrop) GlassSurface.SetContainerTransitionBounds(null);
        if (_panelPixelBounds.Width > 0 && _transformHostBounds.Width > 0)
            SetPanelRootLayout(_panelPixelBounds, _transformHostBounds, _panelDipPerPixel);
        CollapsedGlyph.Opacity = 0;
    }

    private void PrepareTransitionBackdrop() => GlassSurface.SetContainerTransitionBounds(new Rect(0, 0,
        _transformHostBounds.Width * _transformDipPerPixel, _transformHostBounds.Height * _transformDipPerPixel));

    private void PrepareRevealMaterial()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            GlassSurface.PrepareRevealMaterial(_transformHostBounds, _panelPixelBounds,
                Math.Max(96, TransformDisplay?.Dpi ?? 96));
        }
        finally { _lastRevealPreparationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    private void RefreshContainerAnchor()
    {
        if (ContainerAnchorProvider?.Invoke() is not { Width: > 0, Height: > 0 } source || source == _transformSourceBounds) return;
        var display = TransformDisplay;
        if (display is null
            || !display.Bounds.Contains(source.X + source.Width / 2, source.Y + source.Height / 2)
            || !display.Bounds.Contains(_transformDestinationBounds.X + _transformDestinationBounds.Width / 2,
                _transformDestinationBounds.Y + _transformDestinationBounds.Height / 2)) return;
        _transformSourceBounds = source;
        _transformSourceRadiusPixels = Math.Min(source.Width, source.Height) / 3.0;
        _containerMotion?.Retarget(_transformEndTimeline >= 1, source);
        if (_transformActive) ExpandTransformHost();
    }

    private void ExpandTransformHost()
    {
        var path = Union(_transformSourceBounds, _transformDestinationBounds);
        // Reserve the spring's complete envelope once; the layered HWND stays fixed
        // while the live silhouette overshoots and settles inside it.
        var paddingX = (int)Math.Ceiling(_transformDestinationBounds.Width * .04);
        var paddingY = (int)Math.Ceiling(_transformDestinationBounds.Height * .04);
        if (_unifiedRequested || Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_RESERVE_SHADOW") == "1")
        {
            var shadowPadding = (int)Math.Ceiling(42 / _transformDipPerPixel);
            paddingX += shadowPadding; paddingY += shadowPadding;
        }
        var requiredHost = new PixelRect(path.X - paddingX, path.Y - paddingY,
            path.Width + paddingX * 2, path.Height + paddingY * 2);
        if (PrimaryDisplaySession is { } session)
            requiredHost = session.ClampWindowToWorkArea(requiredHost);
        var host = _transformHostBounds.Width > 0 && ContainsPixelRect(_transformHostBounds, requiredHost)
            ? _transformHostBounds
            : requiredHost;
        if (_transformHostBounds == host) return;
        SetPixelBounds(host);
        _transformHostBounds = host;
        SetPanelRootLayout(_transformDestinationBounds, host, _transformDipPerPixel);
    }

    private void CollapseTransformHost()
    {
        var panel = _panelPixelBounds.Width > 0 ? _panelPixelBounds : _transformDestinationBounds;
        if (panel.Width <= 0 || panel.Height <= 0) return;
        if (_transformHostBounds != panel) SetPixelBounds(panel);
        _transformHostBounds = panel;
        var dipPerPixel = _transformDestinationBounds == panel && _transformDestinationBounds.Width > 0
            ? _transformDipPerPixel
            : _panelDipPerPixel;
        SetPanelRootLayout(panel, panel, dipPerPixel);
    }

    private static PixelRect Union(PixelRect left, PixelRect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        var endX = Math.Max(left.Right, right.Right);
        var endY = Math.Max(left.Bottom, right.Bottom);
        return new PixelRect(x, y, endX - x, endY - y);
    }

}
