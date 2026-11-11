using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Rendering;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private readonly bool _unifiedRequested = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_UNIFIED_TRANSITION") != "0";
    private NativeDirectPresenter? _unifiedPresenter;
    private BitmapSource? _unifiedBackdrop;
    private BitmapSource? _unifiedOverlay;
    private long _unifiedBackdropRevision;
    private readonly DirectShadowLayer[] _unifiedLayers = new DirectShadowLayer[4];
    private Size _unifiedHostSize, _unifiedForegroundSize;
    private Point _unifiedContentCenter;
    private int _unifiedWidth, _unifiedHeight;
    private bool _unifiedActive, _unifiedShadowOnly, _unifiedFailed, _unifiedFrameSubmitted;
    private ulong _unifiedSubmitted, _unifiedDeferred;
    private string? _unifiedFailure;
    private DispatcherOperation? _unifiedTransfer;
    private bool _unifiedTransferPending;
    private int _unifiedTransferFrames;
    private long _unifiedContentRevision, _unifiedSnapshotRevision;
    private TilesRenderKey? _unifiedTilesKey;
    private ThemeService? _unifiedTheme;
    private TimeSpan _unifiedTransferRenderingTime = TimeSpan.MinValue;
    private Brush? _unifiedOriginalBackground;
    private bool _unifiedOwnsInputMask;
    private bool _unifiedGlyphVector;
    private double _unifiedSnapshotDpi;
    private long _unifiedSourceWaitAt;
    private ulong _unifiedSourceDeferrals, _unifiedMaterialFallbacks;
    private Action? _unifiedWpfFence;
    private long _unifiedTransferRequestedAt;
    private bool _unifiedTransferFenced;
    private ulong _unifiedHandoffFences;
    private double _unifiedMaximumFenceMilliseconds;
    private bool _unifiedSurfacesRetired;
    private PreparedTransitionFrame? _unifiedLastMaterial;
    private long _unifiedLastMaterialAt;
    private PixelRect _unifiedLastMaterialHost;
    private double _unifiedLastMaterialScale;
    private PreparedTransitionFrame? _unifiedClosingMaterial;
    private long _unifiedClosingRequestId;
    private int _unifiedForegroundCaptures;
    private double _unifiedLastForegroundMilliseconds, _unifiedMaximumForegroundMilliseconds;

    private void SetUnifiedPresentation(bool native)
    {
        if (native)
        {
            if (!_unifiedOwnsInputMask) _unifiedOriginalBackground = ContainerTransformClipRoot.Background;
            // Layered-window input still uses WPF's backing alpha. Keep an exact
            // one-alpha panel mask while the unchanged input tree is concealed;
            // native pixels alone cannot retain cross-process hit testing.
            var mask = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); mask.Freeze();
            ContainerTransformClipRoot.Background = mask;
            ContainerTransformClipRoot.Child.Opacity = 0;
            _unifiedOwnsInputMask = true;
        }
        else
        {
            ContainerTransformClipRoot.Child.Opacity = 1;
            if (_unifiedOwnsInputMask) ContainerTransformClipRoot.Background = _unifiedOriginalBackground;
            _unifiedOwnsInputMask = false;
        }
    }

    internal object InspectUnifiedTransition() => new
    {
        requested = _unifiedRequested, active = _unifiedActive, shadowOnly = _unifiedShadowOnly,
        submitted = _unifiedSubmitted, deferred = _unifiedDeferred, failure = _unifiedFailure,
        sourceDeferrals = _unifiedSourceDeferrals, materialFallbacks = _unifiedMaterialFallbacks,
        handoffFences = _unifiedHandoffFences, maximumFenceMilliseconds = _unifiedMaximumFenceMilliseconds,
        targetWidth = _unifiedWidth, targetHeight = _unifiedHeight,
        inputHost = _transformHostBounds, windowWidth = Width, windowHeight = Height,
        prepared = GlassSurface.InspectPreparedTransition(),
        foregroundCaptures = _unifiedForegroundCaptures, lastForegroundMilliseconds = _unifiedLastForegroundMilliseconds,
        maximumForegroundMilliseconds = _unifiedMaximumForegroundMilliseconds,
        clock = _unifiedFrameClock?.Statistics,
        host = _unifiedHost?.Handle.ToInt64(),
    };

    private void PrepareUnifiedTransition()
    {
        if (!_unifiedRequested || _unifiedFailed || _containerMotion is null) return;
        try
        {
            _unifiedWpfFence ??= WpfPresentationFence.Create(Dispatcher);
            if (!TryGetUnifiedMaterial(out var material))
            {
                if (UseUnifiedClosingShadowFallback()) return;
                // The retired output is hidden and only 1x1. A missing first
                // source is temporary; retain its pipelines for late admission.
                if (!_unifiedSurfacesRetired && (_unifiedPresenter is not null || _unifiedShadowOnly || _unifiedTransferPending))
                    DisposeUnifiedTransition();
                return;
            }
            if (material.Frosted)
            {
                if (_unifiedPresenter is not null || _unifiedShadowOnly || _unifiedTransferPending)
                    DisposeUnifiedTransition();
                return;
            }
            _unifiedTransfer?.Abort(); _unifiedTransfer = null;
            CancelUnifiedTransfer();
            if (!IsLoaded && _workspace is not null) _workspace.Reconcile(_view.Capacity);
            RenderTiles(reuseUnchanged: true);
            ContainerTransformClipRoot.Measure(new Size(_transformHostBounds.Width * _transformDipPerPixel, _transformHostBounds.Height * _transformDipPerPixel));
            ContainerTransformClipRoot.Arrange(new Rect(0, 0, _transformHostBounds.Width * _transformDipPerPixel, _transformHostBounds.Height * _transformDipPerPixel));
            UpdateLayout();
            _unifiedHostSize = new(_transformHostBounds.Width * _transformDipPerPixel, _transformHostBounds.Height * _transformDipPerPixel);
            var width = _transformHostBounds.Width; var height = _transformHostBounds.Height;
            _unifiedPresenter ??= new NativeDirectPresenter(EnsureUnifiedHost(), width, height);
            if (_unifiedWidth != width || _unifiedHeight != height)
            {
                if (_unifiedWidth != 0) _unifiedPresenter.Resize(width, height);
                _unifiedWidth = width; _unifiedHeight = height; _unifiedBackdrop = null;
            }
            if (UnifiedForegroundNeedsCapture()) CaptureUnifiedForeground();
            if (_unifiedTheme is null && ThemeService.Current is { } theme)
            {
                _unifiedTheme = theme;
                theme.Changed += OnUnifiedThemeChanged;
                LiquidGlassDebugState.CompositionChanged += OnUnifiedCompositionChanged;
            }
            if (TryDrawUnifiedFrame(_containerMotion.CurrentFrame))
            {
                _unifiedSurfacesRetired = false;
                SetUnifiedPresentation(true);
                SetIndependentShadowSuppressed(true);
                _unifiedActive = true; _unifiedShadowOnly = false;
                UpdateUnifiedHostVisibility();
            }
        }
        catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); }
    }

    private static RenderTargetBitmap Snapshot(FrameworkElement element, Size size, double dpi)
    {
        var width = Math.Max(1, (int)Math.Ceiling(size.Width * dpi / 96 - 1e-8));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * dpi / 96 - 1e-8));
        if ((long)width * height * 4 > 16 * 1024 * 1024) throw new NotSupportedException("Foreground snapshot exceeds the full-resolution budget.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var offset = VisualTreeHelper.GetOffset(element);
            var brush = new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(new Point(offset.X, offset.Y), size), Stretch = Stretch.Fill };
            dc.DrawRectangle(brush, null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private void CaptureUnifiedForeground()
    {
        var captureStarted = Stopwatch.GetTimestamp();
        var opacity = LauncherContent.Opacity;
        var sx = ContentScaleTransform.ScaleX; var sy = ContentScaleTransform.ScaleY;
        try
        {
            LauncherContent.Opacity = 1; ContentScaleTransform.ScaleX = ContentScaleTransform.ScaleY = 1;
            UpdateLayout();
            _unifiedForegroundSize = new(LauncherPanelRoot.Width, LauncherPanelRoot.Height);
            _unifiedContentCenter = LauncherContent.TranslatePoint(new Point(LauncherContent.ActualWidth / 2, LauncherContent.ActualHeight / 2), LauncherPanelRoot);
            var dpi = 96 / _transformDipPerPixel;
            _unifiedPresenter!.SetImage(1, Snapshot(LauncherPanelRoot, _unifiedForegroundSize, dpi));
            var glyph = new LaunchpadGlyph { Width = 64, Height = 64, OutlineBrush = CollapsedGlyph.OutlineBrush, IsFilled = CollapsedGlyph.IsFilled };
            glyph.Measure(new(64, 64)); glyph.Arrange(new(0, 0, 64, 64));
            _unifiedGlyphVector = CollapsedGlyph.OutlineBrush is SolidColorBrush;
            if (CollapsedGlyph.OutlineBrush is SolidColorBrush brush)
            {
                var geometry = new GeometryGroup();
                var inset = CollapsedGlyph.IsFilled ? 4 : 5.75; var size = CollapsedGlyph.IsFilled ? 12 : 8.5;
                var radius = CollapsedGlyph.IsFilled ? 3.5 : 1.8;
                for (var i = 0; i < 9; i++) geometry.Children.Add(new RectangleGeometry(new Rect(inset + i % 3 * 14, inset + i / 3 * 14, size, size), radius, radius));
                geometry.Freeze(); var color = brush.Color; color.A = (byte)Math.Round(color.A * brush.Opacity);
                if (CollapsedGlyph.IsFilled) _unifiedPresenter.SetVector(2, geometry, new(48, 48), color);
                else _unifiedPresenter.SetStrokeVector(2, geometry, new(48, 48), color, 3.5f);
            }
            else _unifiedPresenter.SetImage(2, Snapshot(glyph, new(64, 64), dpi));
            _unifiedSnapshotRevision = _unifiedContentRevision;
            _unifiedSnapshotDpi = dpi;
            _unifiedTilesKey = _services is null ? null : CurrentTilesRenderKey();
        }
        finally
        {
            LauncherContent.Opacity = opacity; ContentScaleTransform.ScaleX = sx; ContentScaleTransform.ScaleY = sy;
            _unifiedForegroundCaptures++;
            _unifiedLastForegroundMilliseconds = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
            _unifiedMaximumForegroundMilliseconds = Math.Max(_unifiedMaximumForegroundMilliseconds, _unifiedLastForegroundMilliseconds);
        }
    }

    private bool UnifiedForegroundNeedsCapture() => _unifiedSnapshotDpi != 96 / _transformDipPerPixel
        || _unifiedForegroundSize != new Size(LauncherPanelRoot.Width, LauncherPanelRoot.Height)
        || _unifiedSnapshotRevision != _unifiedContentRevision
        || _services is not null && _unifiedTilesKey != CurrentTilesRenderKey();

    private bool TryGetUnifiedMaterial(out PreparedTransitionFrame material)
    {
        if (GlassSurface.TryGetPreparedTransitionMaterial(out material)) return true;
        return TryGetClosingMaterial(out material);
    }

    private bool TryGetClosingMaterial(out PreparedTransitionFrame material)
    {
        material = default;
        // Only finish shrinking a recently displayed native frame. Opening must
        // still acquire current material, and old resting snapshots never qualify.
        if (_showRequestedAt == 0 && _transformEndTimeline == 0 && !_unifiedSurfacesRetired
            && _unifiedLastMaterialHost == _transformHostBounds && _unifiedLastMaterialScale == _transformDipPerPixel
            && Stopwatch.GetElapsedTime(_animationRequestStartedAt).TotalSeconds < 1)
        {
            if (_unifiedClosingRequestId == _animationRequestId && _unifiedClosingMaterial is { } closing)
            { material = closing; return true; }
            if (_unifiedLastMaterial is { } previous && _unifiedLastMaterialAt != 0
                && Stopwatch.GetElapsedTime(_unifiedLastMaterialAt).TotalSeconds < 1)
            {
                _unifiedClosingRequestId = _animationRequestId; _unifiedClosingMaterial = previous;
                material = previous; return true;
            }
        }
        return false;
    }

    private bool TryDrawUnifiedFrame(ContainerMorphFrame frame)
    {
        if (_unifiedPresenter is null || _unifiedFailed) return false;
        _unifiedFrameSubmitted = false;
        try
        {
            var currentMaterial = GlassSurface.TryGetPreparedTransitionMaterial(out var material);
            if (!currentMaterial && !TryGetClosingMaterial(out material))
            {
                // Freshness validation can withdraw a prepared frame while a new
                // capture is in flight, especially across an immediate reversal.
                // Keep the last complete displayed frame; never animate stale
                // material or permanently disable the backend for this condition.
                if (_unifiedActive)
                {
                    if (UseUnifiedClosingShadowFallback()) return false;
                    if (_unifiedSourceWaitAt == 0) _unifiedSourceWaitAt = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(_unifiedSourceWaitAt).TotalMilliseconds < 250)
                    { _unifiedSourceDeferrals++; _unifiedDeferred++; return true; }
                    _unifiedMaterialFallbacks++;
                }
                DisposeUnifiedTransition();
                return false;
            }
            _unifiedSourceWaitAt = 0;
            if (_unifiedWidth != _transformHostBounds.Width || _unifiedHeight != _transformHostBounds.Height)
            {
                _unifiedPresenter.Resize(_transformHostBounds.Width, _transformHostBounds.Height);
                _unifiedWidth = _transformHostBounds.Width; _unifiedHeight = _transformHostBounds.Height;
                _unifiedHostSize = new(_unifiedWidth * _transformDipPerPixel, _unifiedHeight * _transformDipPerPixel);
                _unifiedBackdrop = _unifiedOverlay = null;
            }
            if (UnifiedForegroundNeedsCapture()) CaptureUnifiedForeground();
            if (!ReferenceEquals(material.Primary.Bitmap, _unifiedBackdrop) || material.Primary.Revision != _unifiedBackdropRevision)
            {
                _unifiedPresenter.UploadPreparedBackdrop(material.Primary.Bitmap); _unifiedBackdrop = material.Primary.Bitmap;
                _unifiedBackdropRevision = material.Primary.Revision;
            }
            if (material.Overlay is { } overlay && !ReferenceEquals(overlay.Bitmap, _unifiedOverlay))
            { _unifiedPresenter.UploadPreparedBackdrop(overlay.Bitmap, 1); _unifiedOverlay = overlay.Bitmap; }
            var panel = new Rect((frame.Bounds.X - _transformHostBounds.X) * _transformDipPerPixel,
                (frame.Bounds.Y - _transformHostBounds.Y) * _transformDipPerPixel,
                frame.Bounds.Width * _transformDipPerPixel, frame.Bounds.Height * _transformDipPerPixel);
            var radius = frame.CornerRadius * _transformDipPerPixel;
            Span<DirectShadowLayer> layers = _unifiedLayers;
            var count = GlassShadow.CopyDirectLayers(layers, LiquidGlassDebugState.View, LiquidGlassDebugState.FinishingParameters, 1);
            _unifiedPresenter.RenderShadow(new(_unifiedHostSize, panel, (float)radius, (float)(.35 + .65 * frame.GlassStrength), true, true), layers[..count]);
            DrawMaterial(material.Primary, 0);
            if (material.Overlay is { } over) DrawMaterial(over, 1);
            void DrawMaterial(PreparedTransitionLayer layer, uint slot)
            {
            var composite = layer.Opacity == 1;
            if (!composite) _unifiedPresenter.PushGroup((float)layer.Opacity);
            _unifiedPresenter.RenderPreparedGlass(new(_unifiedHostSize, panel, layer.Bounds, (float)radius, (float)(.6 + .4 * frame.GlassStrength),
                (float)(LiquidGlassDebugState.DispersionParameters.DispersionStrength / LiquidGlassDispersionParameters.DefaultDispersionStrength),
                (float)(LiquidGlassDebugState.DispersionParameters.InnerDispersionGain / LiquidGlassDispersionParameters.DefaultInnerDispersionGain),
                (float)(LiquidGlassDebugState.DispersionParameters.OuterDispersionGain / LiquidGlassDispersionParameters.DefaultOuterDispersionGain), GlassSurface.UseLaunchpadOptics, true), material.Frosted, slot, composite);
            if (!composite) _unifiedPresenter.PopGroup();
            }
            var geometry = SquircleGlassGeometry.CreateCompact(panel, radius);
            _unifiedPresenter.SetVector(1, geometry, _unifiedHostSize, Colors.White);
            if (frame.ContentOpacity > 0)
            {
                var destination = new Rect((_transformDestinationBounds.X - _transformHostBounds.X) * _transformDipPerPixel,
                    (_transformDestinationBounds.Y - _transformHostBounds.Y) * _transformDipPerPixel, _unifiedForegroundSize.Width, _unifiedForegroundSize.Height);
                var scale = .96 + .04 * frame.ContentOpacity;
                var target = new Rect(destination.X + _unifiedContentCenter.X * (1 - scale), destination.Y + _unifiedContentCenter.Y * (1 - scale), destination.Width * scale, destination.Height * scale);
                _unifiedPresenter.PushGroup((float)frame.ContentOpacity);
                _unifiedPresenter.DrawImages(_unifiedHostSize, [new(1, target)]);
                _unifiedPresenter.BeginGroupMask(); _unifiedPresenter.DrawVectors(_unifiedHostSize, [new(1, new Rect(_unifiedHostSize))]); _unifiedPresenter.PopGroup();
            }
            if (frame.GlyphOpacity > 0)
            {
                var size = Math.Min(64, Math.Min(panel.Width, panel.Height));
                var glyphBounds = new Rect(panel.X + (panel.Width - size) / 2, panel.Y + (panel.Height - size) / 2, size, size);
                if (_unifiedGlyphVector) _unifiedPresenter.DrawVectors(_unifiedHostSize, [new(2, glyphBounds, (float)frame.GlyphOpacity)]);
                else _unifiedPresenter.DrawImages(_unifiedHostSize, [new(2, glyphBounds, (float)frame.GlyphOpacity)]);
            }
            _unifiedFrameSubmitted = _unifiedPresenter.Present();
            if (_unifiedFrameSubmitted && currentMaterial)
            {
                _unifiedLastMaterial = material; _unifiedLastMaterialAt = Stopwatch.GetTimestamp();
                _unifiedLastMaterialHost = _transformHostBounds; _unifiedLastMaterialScale = _transformDipPerPixel;
                if (_showRequestedAt == 0 && _transformEndTimeline == 0)
                { _unifiedClosingMaterial = material; _unifiedClosingRequestId = _animationRequestId; }
            }
            if (_unifiedFrameSubmitted) ++_unifiedSubmitted; else ++_unifiedDeferred;
            if (_unifiedActive) UpdateUnifiedHostVisibility();
            return _unifiedActive || _unifiedFrameSubmitted;
        }
        catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); return false; }
    }

    private void CompleteUnifiedTransition(bool opening)
    {
        if (!opening && _unifiedShadowOnly)
        { ClearUnifiedOutput(restoreShadow: false); _unifiedLastMaterial = _unifiedClosingMaterial = null; return; }
        if (!_unifiedActive) return;
        SetUnifiedPresentation(false);
        _unifiedActive = false;
        if (!opening) { ClearUnifiedOutput(restoreShadow: false); _unifiedLastMaterial = _unifiedClosingMaterial = null; return; }
        // Retain the complete native image until WPF has painted ready material
        // and its render thread has presented that scene. Rendering callbacks
        // alone are not a presentation fence.
        _unifiedTransferPending = true; _unifiedTransferFrames = 0; _unifiedTransferRenderingTime = TimeSpan.MinValue;
        _unifiedTransferRequestedAt = Stopwatch.GetTimestamp(); _unifiedTransferFenced = false;
        GlassSurface.InvalidateVisual();
        UpdateUnifiedHostVisibility();
        CompositionTarget.Rendering += OnUnifiedTransferRendering;
        QueueUnifiedTransfer();
    }

    private void OnUnifiedTransferRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (_unifiedTransferRenderingTime == rendering.RenderingTime) return;
            _unifiedTransferRenderingTime = rendering.RenderingTime;
        }
        if (!_unifiedTransferPending || !IsVisible || _transformActive) { CancelUnifiedTransfer(); return; }
        ++_unifiedTransferFrames;
        QueueUnifiedTransfer();
    }

    private void QueueUnifiedTransfer()
    {
        if (_unifiedTransfer is { Status: DispatcherOperationStatus.Pending }) return;
        // Runs after the Render-priority visual update, outside WPF's render
        // callback; CompleteRender may then flush/wait for its actual commit.
        _unifiedTransfer = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FinishUnifiedTransfer));
    }

    private void FinishUnifiedTransfer()
    {
        _unifiedTransfer = null;
        if (!_unifiedTransferPending || !IsVisible || _transformActive) return;
        try
        {
            if (Stopwatch.GetElapsedTime(_unifiedTransferRequestedAt).TotalMilliseconds > 1000)
                throw new InvalidOperationException("WPF presentation handoff did not complete.");
            if (!GlassSurface.HasReadyPaintAfter(_unifiedTransferRequestedAt)) return;
            if (!_unifiedTransferFenced)
            {
                var started = Stopwatch.GetTimestamp();
                _unifiedWpfFence!();
                _unifiedMaximumFenceMilliseconds = Math.Max(_unifiedMaximumFenceMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                _unifiedHandoffFences++; _unifiedTransferFenced = true;
            }
            DrawUnifiedRestingShadow();
            if (_unifiedShadowOnly) CancelUnifiedTransfer();
        }
        catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); }
    }

    private void CancelUnifiedTransfer()
    {
        if (_unifiedTransferPending) CompositionTarget.Rendering -= OnUnifiedTransferRendering;
        _unifiedTransferPending = false;
        _unifiedTransfer?.Abort(); _unifiedTransfer = null;
    }

    private void OnUnifiedThemeChanged(object? sender, EventArgs e) => DisposeUnifiedTransition();
    private void OnUnifiedCompositionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => OnUnifiedCompositionChanged(sender, e))); return; }
        if (_unifiedTransferPending) { QueueUnifiedTransfer(); return; }
        if (!_unifiedActive && !_transformActive && IsVisible) DrawUnifiedRestingShadow();
    }

    private void DrawUnifiedRestingShadow()
        => DrawUnifiedShadow(1);

    private void DrawUnifiedShadow(double strength)
    {
        if (_unifiedPresenter is null || _transitionPanelBoundsDip is not { } panel) return;
        try
        {
            Span<DirectShadowLayer> layers = _unifiedLayers;
            var count = GlassShadow.CopyDirectLayers(layers, LiquidGlassDebugState.View, LiquidGlassDebugState.FinishingParameters, 1);
            _unifiedPresenter.RenderShadow(new(_unifiedHostSize, panel, (float)_transitionPanelRadiusDip, (float)strength, false, false), layers[..count]);
            if (_unifiedPresenter.Present()) _unifiedShadowOnly = true;
        }
        catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); }
    }

    private bool UseUnifiedClosingShadowFallback()
    {
        if (_showRequestedAt != 0 || _transformEndTimeline != 0 || _unifiedPresenter is null
            || _unifiedSurfacesRetired || _unifiedFailed) return false;
        if (_unifiedWidth != _transformHostBounds.Width || _unifiedHeight != _transformHostBounds.Height)
        {
            _unifiedPresenter.Resize(_transformHostBounds.Width, _transformHostBounds.Height);
            _unifiedWidth = _transformHostBounds.Width; _unifiedHeight = _transformHostBounds.Height;
            _unifiedBackdrop = _unifiedOverlay = null;
        }
        _unifiedHostSize = new(_unifiedWidth * _transformDipPerPixel, _unifiedHeight * _transformDipPerPixel);
        // No current glass is available, so retain the ordinary WPF material
        // fallback. Its matching shadow can use this already-created device;
        // destroying it here would cold-start another renderer on the UI thread.
        SetUnifiedPresentation(false);
        _unifiedActive = false; _unifiedShadowOnly = true; _unifiedSourceWaitAt = 0;
        SetIndependentShadowSuppressed(true);
        return true;
    }

    private static bool IsUnifiedFailure(Exception error) => error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        or COMException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException or Win32Exception
        or MemberAccessException or System.Reflection.TargetInvocationException;

    private void FailUnifiedTransition(Exception error)
    {
        // A device retained through game/idle sleep may have been reset by the
        // driver. Discard it once and reacquire on this/next reveal; a failure of
        // that fresh device still takes the ordinary, bounded legacy fallback.
        if (_unifiedSurfacesRetired && error is COMException
            && error.HResult is unchecked((int)0x887A0005) or unchecked((int)0x887A0006) or unchecked((int)0x887A0007))
        {
            DisposeUnifiedTransition();
            PrepareUnifiedTransition();
            return;
        }
        _unifiedFailure = error.GetType().Name + ": " + error.Message; _unifiedFailed = true;
        if (!string.IsNullOrWhiteSpace(_animationTraceDirectory))
        {
            var directory = System.IO.Path.Combine(_animationTraceDirectory, "unified-lifecycle");
            var diagnostic = new { type = error.GetType().Name, error.HResult, materialUnavailable = error is NotSupportedException && error.Message.StartsWith("Prepared material is no longer accepted", StringComparison.Ordinal),
                active = _unifiedActive, width = _unifiedWidth, height = _unifiedHeight, qpc = Stopwatch.GetTimestamp() };
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { System.IO.Directory.CreateDirectory(directory); System.IO.File.WriteAllText(System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), System.Text.Json.JsonSerializer.Serialize(diagnostic)); }
                catch (Exception io) when (io is System.IO.IOException or UnauthorizedAccessException) { }
            });
        }
        DisposeUnifiedTransition();
    }

    private void ClearUnifiedOutput(bool restoreShadow = true)
    {
        CancelUnifiedTransfer();
        _unifiedTransfer?.Abort(); _unifiedTransfer = null;
        // Hide before touching any GPU resource. The next complete frame starts
        // with RenderShadow's clear, so a device-lost cleanup needs no Present.
        _unifiedActive = _unifiedShadowOnly = false;
        _unifiedSourceWaitAt = 0;
        _unifiedHost?.Hide();
        SetUnifiedPresentation(false);
        if (restoreShadow)
        {
            if (_transitionPanelBoundsDip is { } panel)
                SetTransitionShadowShape(panel, _transitionPanelRadiusDip, .35 + .65 * GlassSurface.TransitionProgress, reducedDetail: _transformActive);
            SetIndependentShadowSuppressed(false);
        }
    }

    private void RetireUnifiedTransitionSurfaces()
    {
        _unifiedLastMaterial = _unifiedClosingMaterial = null;
        if (_unifiedPresenter is null) return;
        try
        {
            // Keep compiled pipelines/device across idle and game sleep. Recreating
            // them synchronously on reveal costs hundreds of milliseconds. Retire
            // the full-size pixels and targets while the owned output stays hidden.
            ClearUnifiedOutput(restoreShadow: false);
            DisposeUnifiedClock();
            _unifiedPresenter.RemoveImage(1); _unifiedPresenter.RemoveImage(2);
            _unifiedPresenter.RemoveVector(1); _unifiedPresenter.RemoveVector(2);
            var empty = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4);
            empty.Freeze();
            _unifiedPresenter.UploadPreparedBackdrop(empty);
            _unifiedPresenter.UploadPreparedBackdrop(empty, 1);
            _unifiedPresenter.Resize(1, 1);
            _unifiedWidth = _unifiedHeight = 1;
            _unifiedHostSize = new(1, 1);
            _unifiedHost?.Update(new PixelRect(0, 0, 1, 1), false);
            _unifiedBackdrop = _unifiedOverlay = null;
            _unifiedSnapshotDpi = 0; _unifiedTilesKey = null;
            _unifiedSurfacesRetired = true;
            SetIndependentShadowSuppressed(false);
        }
        catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); }
    }

    private void DisposeUnifiedTransition()
    {
        _unifiedLastMaterial = _unifiedClosingMaterial = null;
        _unifiedSurfacesRetired = false;
        DisposeUnifiedClock();
        CancelUnifiedTransfer();
        if (_unifiedTheme is not null) { _unifiedTheme.Changed -= OnUnifiedThemeChanged; _unifiedTheme = null; LiquidGlassDebugState.CompositionChanged -= OnUnifiedCompositionChanged; }
        _unifiedTransfer?.Abort(); _unifiedTransfer = null;
        _unifiedPresenter?.Dispose(); _unifiedPresenter = null; _unifiedBackdrop = null; _unifiedOverlay = null;
        DisposeUnifiedHost();
        _unifiedWidth = _unifiedHeight = 0; _unifiedActive = _unifiedShadowOnly = false;
        _unifiedSnapshotDpi = 0;
        _unifiedSourceWaitAt = 0;
        SetUnifiedPresentation(false);
        if (_transitionPanelBoundsDip is { } panel)
            SetTransitionShadowShape(panel, _transitionPanelRadiusDip, .35 + .65 * GlassSurface.TransitionProgress, reducedDetail: _transformActive);
        SetIndependentShadowSuppressed(false);
    }
}
