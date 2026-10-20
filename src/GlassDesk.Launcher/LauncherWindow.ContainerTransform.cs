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

    private PixelRect _transformSourceBounds;
    private PixelRect _transformDestinationBounds;
    private double _transformDipPerPixel = 1;
    private double _transformSourceRadiusPixels;
    private double _transformDestinationRadiusPixels;
    private double _transformEndTimeline;
    private long _transformStartedAt;
    private ContainerMorphMotion? _containerMotion;
    private bool _transformActive;
    private bool _hasTransformPath;
    private bool _preserveExpandedHostOnHide;
    private readonly MatrixTransform _glyphRenderTransform = new();
    private TimeSpan _lastTransformRenderingTime = TimeSpan.MinValue;

    private DisplayInfo? TransformDisplay => PrimaryDisplaySession is { } session
        ? session.IsAvailable ? session.Display : null
        : DisplayService.Primary();

    /// <summary>Show the launcher, optionally morphing from a live Dock-button rectangle.</summary>
    public void ShowWithTransition(PixelRect? sourceBounds)
    {
        sourceBounds = ContainerAnchorProvider?.Invoke() ?? sourceBounds;
        if (PrimaryDisplaySession is { IsAvailable: false }) return;
        var primary = TransformDisplay;
        if (sourceBounds is { } suppliedSource && primary is not null
            && !primary.Bounds.Contains(suppliedSource.X + suppliedSource.Width / 2,
                suppliedSource.Y + suppliedSource.Height / 2))
            sourceBounds = null;
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

        PositionOnWorkingArea();
        if (sourceBounds is { Width: > 0, Height: > 0 })
            PositionOnDisplay(TransformDisplay);
        PrepareToShow();

        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (sourceBounds is { Width: > 0, Height: > 0 })
            PositionOnDisplay(TransformDisplay);
        else PositionOnWorkingArea();
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
        PrepareTransitionBackdrop();
        LauncherContent.IsHitTestVisible = false;
        ApplyContainerTransformFrame(_containerMotion.CurrentFrame);
        PrepareRevealMaterial();
        Show();
        BeginContainerTransform(1);
    }

    /// <summary>Reverse the current shared transition and shrink the panel into its Dock button.</summary>
    public void CloseWithTransition()
    {
        if (!IsVisible) return;
        StopPageSlide();
        if (_transformActive && _transformEndTimeline <= 0) return;
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
        BeginContainerTransform(0);
    }

    private void BeginContainerTransform(double targetTimeline)
    {
        if (!_hasTransformPath || _containerMotion is null) return;
        StopPageSlide();
        PrepareTransitionBackdrop();
        AdvanceContainerMotion();
        StopContainerTransform();
        _transformEndTimeline = Math.Clamp(targetTimeline, 0, 1);
        _containerMotion.Retarget(_transformEndTimeline >= 1, _transformSourceBounds);
        ApplyContainerTransformFrame(_containerMotion.CurrentFrame);
        if (_containerMotion.IsSettled)
        {
            CompleteContainerTransform(_transformEndTimeline);
            return;
        }

        _transformStartedAt = Stopwatch.GetTimestamp();
        _transformActive = true;
        _lastTransformRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnContainerTransformRendering;
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
        RefreshContainerAnchor();

        AdvanceContainerMotion();
        if (_containerMotion?.IsSettled == true)
            CompleteContainerTransform(_transformEndTimeline);
    }

    private void AdvanceContainerMotion()
    {
        if (!_transformActive || _containerMotion is null) return;
        var now = Stopwatch.GetTimestamp();
        _containerMotion.Advance(Stopwatch.GetElapsedTime(_transformStartedAt, now).TotalSeconds);
        _transformStartedAt = now;
        ApplyContainerTransformFrame(_containerMotion.CurrentFrame);
    }

    private void ApplyContainerTransformFrame(ContainerMorphFrame frame)
    {
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
        GlassSurface.SetPanelShape(boundsInHost, radiusInHost, panelGeometry);
        SetTransitionShadowShape(boundsInHost, radiusInHost, .35 + .65 * frame.GlassStrength);
        Opacity = 1;
        GlassSurface.TransitionProgress = frame.GlassStrength;
        GlassSurface.TransitionBlurRadius = frame.BlurRadius;
        ApplyContentReveal(frame.ContentOpacity);
        var glyphSize = Math.Min(64, Math.Min(boundsInHost.Width, boundsInHost.Height));
        _glyphRenderTransform.Matrix = new Matrix(glyphSize / 48, 0, 0, glyphSize / 48,
            boundsInHost.X + (boundsInHost.Width - glyphSize) / 2,
            boundsInHost.Y + (boundsInHost.Height - glyphSize) / 2);
        CollapsedGlyph.RenderTransform = _glyphRenderTransform;
        CollapsedGlyph.Opacity = frame.GlyphOpacity;
    }

    private void CompleteContainerTransform(double timeline)
    {
        StopContainerTransform();
        if (_containerMotion is not null)
        {
            _containerMotion.Retarget(timeline >= 1, _transformSourceBounds);
            _containerMotion.SnapToTarget();
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
            NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _preserveExpandedHostOnHide = true;
        Hide();
        SetFinalVisualState(collapseHost: false);
        _hasTransformPath = false;
        _containerMotion = null;
    }

    private void SetPixelBounds(PixelRect bounds)
    {
        var display = TransformDisplay;
        if (display is null) return;
        if (PrimaryDisplaySession is { } session) bounds = session.ClampWindowToWorkArea(bounds);
        var dipPerPixel = 96.0 / Math.Max(1, display.Dpi);
        Width = bounds.Width * dipPerPixel;
        Height = bounds.Height * dipPerPixel;
        _ = DisplayService.MoveWindowToPixelRect(Handle, bounds);
        _transformHostBounds = bounds;
    }

    private void SetFinalVisualState(bool collapseHost = true)
    {
        if (collapseHost) CollapseTransformHost();
        ClearContainerTransformShape();
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

    private void StopContainerTransform()
    {
        if (_transformActive)
            CompositionTarget.Rendering -= OnContainerTransformRendering;
        _transformActive = false;
    }

    private void AbortContainerTransform()
    {
        StopContainerTransform();
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
        ClearContainerTransformShape();
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

    private void PrepareRevealMaterial() => GlassSurface.PrepareRevealMaterial(_transformHostBounds, _panelPixelBounds,
        Math.Max(96, TransformDisplay?.Dpi ?? 96));

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
