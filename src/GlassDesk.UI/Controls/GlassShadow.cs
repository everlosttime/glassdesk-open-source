using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Rendering;

namespace GlassDesk.UI.Controls;

/// <summary>
/// A separate, non-activating window casts the glass shadow without extending the
/// input area of the glass itself. The complete glass interior is cut out of its drawing.
/// </summary>
public sealed partial class GlassShadow : IDisposable
{
    private const double Padding = 42;
    // The native candidate consumes the same live appearance recipe as the
    // existing shadow. Backend selection and window policy remain with the host.
    internal static int CopyDirectLayers(Span<DirectShadowLayer> layers,LiquidGlassDebugView view,
        LiquidGlassFinishingParameters parameters,double scale)
    {
        if(layers.Length<4)throw new ArgumentException("Four shadow-layer slots are required.",nameof(layers));
        if(!double.IsFinite(scale) || scale<=0)throw new ArgumentOutOfRangeException(nameof(scale));
        parameters=parameters.Normalize();var count=0;
        var baseColor=Color.FromRgb(18,24,36);
        if(LiquidGlassFinishComposer.IncludesBaselineShadow(view))
        {
            layers[count++]=new((float)(34*scale),(float)(8*scale),.15f,baseColor);
            layers[count++]=new((float)(10*scale),(float)(2*scale),.10f,baseColor);
        }
        var (ambient,contact)=LiquidGlassFinishComposer.ShadowSelection(view);
        if(ambient && parameters.AmbientShadowOpacity>0)
            layers[count++]=new((float)(parameters.AmbientShadowExtentDip*scale),(float)(parameters.AmbientShadowOffsetDip*scale),(float)parameters.AmbientShadowOpacity,baseColor);
        if(contact && parameters.ContactShadowOpacity>0)
            layers[count++]=new((float)(parameters.ContactShadowExtentDip*scale),(float)(parameters.ContactShadowOffsetDip*scale),(float)parameters.ContactShadowOpacity,Color.FromRgb(12,17,27));
        return count;
    }
    private readonly GlassWindow _owner;
    private readonly bool _useVisualOpacity;
    private readonly Window _window;
    private readonly ShadowDrawing _drawing = new();
    private readonly List<DependencyPropertyDescriptor> _descriptors = [];
    private HwndSource? _ownerSource;
    private DispatcherOperation? _pendingUpdate;
    private bool _disposed;
    private bool _updating;
    private bool _suppressed;
    internal void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        if (suppressed) Hide(); else Update();
    }
    private bool _inputTransparent;
    private CaptureExclusionSession? _captureExclusion = null;
    private DesktopWindowAttachment? _desktopAttachment;
    private DependencyPropertyDescriptor? _opacityDescriptor;
    private Rect? _transitionPanel;
    private double _transitionRadius;
    private double _transitionStrength = 1;
    private int _ownerPixelWidth, _ownerPixelHeight, _paddingLeftPixels, _paddingTopPixels;
    private double _ownerScale, _shadowScale;
    private Size _shadowSize;

    public GlassShadow(GlassWindow owner, bool useVisualOpacity = false, bool useNativeShadow = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _useVisualOpacity = useVisualOpacity;
        _nativeRequested = useNativeShadow;
        _window = new Window
        {
            Title = "GlassDesk ambient shadow",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.Manual,
            Content = _drawing,
            Width = 1,
            Height = 1,
        };
        _window.SourceInitialized += OnShadowSourceInitialized;
        _window.DpiChanged += OnShadowDpiChanged;
        _owner.SourceInitialized += OnOwnerSourceInitialized;
        _owner.LocationChanged += OnOwnerChanged;
        _owner.SizeChanged += OnOwnerSizeChanged;
        _owner.StateChanged += OnOwnerChanged;
        _owner.IsVisibleChanged += OnOwnerVisibilityChanged;
        _owner.Activated += OnOwnerChanged;
        _owner.Deactivated += OnOwnerChanged;
        _owner.Closed += OnOwnerClosed;
        LiquidGlassDebugState.CompositionChanged += OnCompositionChanged;
        _drawing.SetAppearance(LiquidGlassDebugState.View, LiquidGlassDebugState.FinishingParameters);
        Observe(Window.TopmostProperty);
        _opacityDescriptor = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(GlassWindow));
        _opacityDescriptor?.AddValueChanged(_owner, OnOwnerOpacityChanged);
        Observe(GlassWindow.PanelCornerRadiusProperty);
        Observe(GlassWindow.ChromeInsetProperty);
        InstallOwnerHook();
        Update();
    }

    /// <summary>The independent shadow handle, or zero before creation and after disposal.</summary>
    public nint Handle => _disposed ? 0 : new WindowInteropHelper(_window).Handle;

    public bool IsVisible => !_disposed && _window.IsVisible && _window.Opacity > 0
        && (_nativePresenter is not null ? _owner.Opacity * _transitionStrength > 0 : _drawing.Opacity > 0);

    private void SetOpacity(double opacity)
    {
        if (_nativePresenter is not null)
        {
            _window.Opacity = 1;
            _drawing.Opacity = 0;
        }
        else if (_useVisualOpacity || _nativeRequested && !_nativeFailed)
        {
            // Window.Opacity changes the layered HWND's settings. The shadow has
            // a single visual, so applying the identical group alpha here keeps
            // its complete recipe while leaving the HWND presentation state fixed.
            _drawing.Opacity = opacity;
        }
        else { _drawing.Opacity = 1; _window.Opacity = opacity; }
    }

    internal void SetTransitionShape(Rect? panelBounds, double radius, double strength = 1) =>
        SetTransitionShape(panelBounds, radius, strength, false);

    internal void SetTransitionShape(Rect? panelBounds, double radius, double strength, bool reducedDetail)
    {
        if (_disposed) return;
        _nativeReducedDetail = reducedDetail;
        if (_nativePresenter is null) _drawing.SetReducedDetail(reducedDetail);
        _transitionPanel = panelBounds;
        _transitionRadius = double.IsFinite(radius) ? Math.Max(0, radius) : 0;
        _transitionStrength = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 1;
        SetOpacity(_owner.Opacity * _transitionStrength);
        // Sliding content does not move the fixed owner HWND, so no owner-change
        // event will reconcile a zero-opacity shadow. Hide its HWND explicitly;
        // a visible transparent shadow still blocks safe desktop sampling.
        if (_transitionStrength <= 0) Hide();
        else if (!_window.IsVisible) Update();
        else UpdatePanelGeometry();
    }

    /// <summary>Synchronizes visibility, physical position, DPI, geometry, opacity, and Z order.</summary>
    public void Update()
    {
        if (_suppressed) { Hide(); return; }
        if (_disposed || _updating || _owner.Dispatcher.HasShutdownStarted) return;
        _owner.Dispatcher.VerifyAccess();
        _updating = true;
        try
        {
            if (_owner.IsDesktopAttached)
            {
                var initialHandle = new WindowInteropHelper(_window).EnsureHandle();
                _desktopAttachment ??= new DesktopWindowAttachment(initialHandle, owner => new WindowInteropHelper(_window).Owner = owner);
                _desktopAttachment.TryAttach();
                if (!_window.IsVisible)
                {
                    SetOpacity(0);
                    _window.Show();
                }
            }
            else
            {
                _desktopAttachment?.Dispose();
                _desktopAttachment = null;
            }
            var ownerHandle = new WindowInteropHelper(_owner).Handle;
            if ((!_owner.IsVisible && !_preparingNative) || _owner.WindowState == WindowState.Minimized || _owner.Opacity <= 0 || _transitionStrength <= 0
                || (!_preparingNative && !WindowShadowStyles.IsOwnerVisible(ownerHandle))
                || DisplayService.GetWindowPixelBounds(ownerHandle) is not { Width: > 0, Height: > 0 } bounds)
            {
                Hide();
                return;
            }

            var session = _owner.PrimaryDisplaySession;
            if (session is { IsAvailable: false })
            {
                Hide();
                return;
            }

            var display = session?.Display ?? DisplayService.Primary();
            if (display is null || !Contains(display.Bounds, bounds))
            {
                Hide();
                return;
            }

            var ownerScale = display.Dpi / 96.0;
            var paddingPixels = (int)Math.Ceiling(Padding * ownerScale);
            var expandedShadowBounds = new PixelRect(bounds.X - paddingPixels, bounds.Y - paddingPixels,
                bounds.Width + paddingPixels * 2, bounds.Height + paddingPixels * 2);
            var shadowBounds = session?.ClipToPrimaryBounds(expandedShadowBounds) ?? Clip(expandedShadowBounds, display.Bounds);
            if (shadowBounds.Width <= 0 || shadowBounds.Height <= 0)
            {
                Hide();
                return;
            }
            var handle = new WindowInteropHelper(_window).EnsureHandle();
            var preserveDesktopOrder = _desktopAttachment?.IsAttached == true
                && DesktopWindowAttachment.IsDesktopShowing(_desktopAttachment.DesktopHandle);
            if (!_inputTransparent || !WindowShadowStyles.PositionBehind(handle, ownerHandle, shadowBounds, preserveDesktopOrder))
            {
                Hide();
                return;
            }

            // Use the SHADOW's actual scale after its monitor transition has settled. This
            // window is clipped to the process Primary Monitor and uses its fixed DPI.
            _ownerPixelWidth = bounds.Width;
            _ownerPixelHeight = bounds.Height;
            _paddingLeftPixels = bounds.X - shadowBounds.X;
            _paddingTopPixels = bounds.Y - shadowBounds.Y;
            _ownerScale = ownerScale;
            _shadowScale = Math.Max(.01, display.Dpi / 96.0);
            _shadowSize = new Size(shadowBounds.Width / _shadowScale, shadowBounds.Height / _shadowScale);
            UpdatePanelGeometry();
            SetOpacity(_owner.Opacity * _transitionStrength);
            if (!_window.IsVisible && !_preparingNative)
            {
                _window.Show();
                // Show() may restore WPF's remembered rectangle or Z order. Settle both again
                // in the same dispatcher turn before painting the first shadow frame.
                if (!WindowShadowStyles.PositionBehind(handle, ownerHandle, shadowBounds)) Hide();
                if (_nativePresenter is not null) UpdatePanelGeometry();
            }
        }
        finally
        {
            _updating = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _owner.Dispatcher.VerifyAccess();
        _disposed = true;
        RetireNative();
        _pendingUpdate?.Abort();
        _pendingUpdate = null;
        _desktopAttachment?.Dispose();
        _desktopAttachment = null;
        _owner.SourceInitialized -= OnOwnerSourceInitialized;
        _owner.LocationChanged -= OnOwnerChanged;
        _owner.SizeChanged -= OnOwnerSizeChanged;
        _owner.StateChanged -= OnOwnerChanged;
        _owner.IsVisibleChanged -= OnOwnerVisibilityChanged;
        _owner.Activated -= OnOwnerChanged;
        _owner.Deactivated -= OnOwnerChanged;
        _owner.Closed -= OnOwnerClosed;
        LiquidGlassDebugState.CompositionChanged -= OnCompositionChanged;
        foreach (var descriptor in _descriptors) descriptor.RemoveValueChanged(_owner, OnOwnerChanged);
        _descriptors.Clear();
        _opacityDescriptor?.RemoveValueChanged(_owner, OnOwnerOpacityChanged);
        _opacityDescriptor = null;
        if (_ownerSource is { IsDisposed: false }) _ownerSource.RemoveHook(OnOwnerMessage);
        _ownerSource = null;
        _window.SourceInitialized -= OnShadowSourceInitialized;
        _window.DpiChanged -= OnShadowDpiChanged;
        var handle = new WindowInteropHelper(_window).Handle;
        _captureExclusion?.Dispose();
        CaptureExclusionSession.Forget(handle);
        _window.Close();
    }

    private static bool Contains(PixelRect bounds, PixelRect rect) =>
        rect.Width > 0 && rect.Height > 0 && rect.X >= bounds.X && rect.Y >= bounds.Y
        && rect.Right <= bounds.Right && rect.Bottom <= bounds.Bottom;

    private static PixelRect Clip(PixelRect rect, PixelRect bounds)
    {
        var left = Math.Max(rect.X, bounds.X);
        var top = Math.Max(rect.Y, bounds.Y);
        var right = Math.Min(rect.Right, bounds.Right);
        var bottom = Math.Min(rect.Bottom, bounds.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(Math.Clamp(left, bounds.X, bounds.Right), Math.Clamp(top, bounds.Y, bounds.Bottom), 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    private void Hide()
    {
        _nativeRetry?.Stop(); _nativePending = false;
        if (_desktopAttachment?.IsAttached == true) SetOpacity(0);
        else if (_window.IsVisible) _window.Hide();
        // Drop the large layered shadow backing surface while it is not displayed.
        _window.Width = 1; _window.Height = 1;
    }

    private void Observe(DependencyProperty property)
    {
        if (DependencyPropertyDescriptor.FromProperty(property, typeof(GlassWindow)) is not { } descriptor) return;
        descriptor.AddValueChanged(_owner, OnOwnerChanged);
        _descriptors.Add(descriptor);
    }

    private void InstallOwnerHook()
    {
        var ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (_ownerSource is not null || ownerHandle == 0 || HwndSource.FromHwnd(ownerHandle) is not { } source) return;
        _ownerSource = source;
        source.AddHook(OnOwnerMessage);
    }

    private void QueueUpdate()
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted || _pendingUpdate?.Status == DispatcherOperationStatus.Pending) return;
        _pendingUpdate = _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _pendingUpdate = null;
            Update();
        }));
    }

    private nint OnOwnerMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Native placement and topmost changes need this too: not every Z-order operation
        // changes WPF's Left/Top or raises LocationChanged. Never consume the owner's message.
        if (message is 0x0047 or 0x0018 or 0x02E0 or 0x007E) QueueUpdate();
        return 0;
    }

    private void OnShadowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        _inputTransparent = WindowShadowStyles.MakeInputTransparent(handle);
        // Independent candidate only: retain visible shadow in external captures.
    }

    private void OnOwnerSourceInitialized(object? sender, EventArgs e)
    {
        InstallOwnerHook();
        QueueUpdate();
    }

    private void OnShadowDpiChanged(object sender, DpiChangedEventArgs e) => QueueUpdate();
    private void OnOwnerChanged(object? sender, EventArgs e) => Update();
    private void OnOwnerSizeChanged(object sender, SizeChangedEventArgs e) => Update();
    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();
    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    private void OnCompositionChanged(object? sender, EventArgs e)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted) return;
        void UpdateComposition()
        {
            if (_disposed) return;
            _drawing.SetAppearance(LiquidGlassDebugState.View, LiquidGlassDebugState.FinishingParameters);
            if (_nativePresenter is not null && _window.IsVisible) UpdatePanelGeometry();
        }
        if (_owner.Dispatcher.CheckAccess()) UpdateComposition();
        else _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateComposition));
    }

    private void OnOwnerOpacityChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_owner.IsVisible || _owner.WindowState == WindowState.Minimized || _owner.Opacity <= 0)
        {
            Hide();
            return;
        }

        if (!_window.IsVisible)
        {
            Update();
            return;
        }

        SetOpacity(_owner.Opacity * _transitionStrength);
        if (_nativePresenter is not null) UpdatePanelGeometry();
    }

    private void UpdatePanelGeometry()
    {
        if (_suppressed) { Hide(); return; }
        // Launcher visibility listeners restore the expanded resting outline
        // before this shadow's visibility listener runs. Never publish that
        // hidden reset through the immediate native presenter: it can briefly
        // resurrect the full border during the final Hide composition.
        if (!_owner.IsVisible && !_preparingNative) { Hide(); return; }
        if (_ownerScale <= 0 || _shadowScale <= 0 || _shadowSize.Width <= 0 || _shadowSize.Height <= 0) return;
        var inset = _owner.ChromeInset;
        Rect panel;
        double radius;
        if (_transitionPanel is { } transition)
        {
            panel = new Rect(
                (_paddingLeftPixels + (inset.Left + transition.X) * _ownerScale) / _shadowScale,
                (_paddingTopPixels + (inset.Top + transition.Y) * _ownerScale) / _shadowScale,
                Math.Max(0, transition.Width * _ownerScale / _shadowScale),
                Math.Max(0, transition.Height * _ownerScale / _shadowScale));
            radius = _transitionRadius * _ownerScale / _shadowScale;
        }
        else
        {
            panel = new Rect(
                (_paddingLeftPixels + inset.Left * _ownerScale) / _shadowScale,
                (_paddingTopPixels + inset.Top * _ownerScale) / _shadowScale,
                Math.Max(0, _ownerPixelWidth - (inset.Left + inset.Right) * _ownerScale) / _shadowScale,
                Math.Max(0, _ownerPixelHeight - (inset.Top + inset.Bottom) * _ownerScale) / _shadowScale);
            var corners = _owner.PanelCornerRadius;
            radius = Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight))
                * _ownerScale / _shadowScale;
        }

        if (TryRenderNative(panel, radius)) return;
        _drawing.SetCompactShape(_transitionPanel is not null && _owner.UsesSquirclePanelGeometry);
        _drawing.SetGeometry(_owner, panel, radius, _shadowSize, _ownerScale / _shadowScale);
    }

    private sealed class ShadowDrawing : FrameworkElement
    {
        private GlassWindow? _owner;
        private Rect _panel;
        private double _radius;
        private Size _size;
        private double _scale = 1;
        private DrawingGroup? _drawing;
        private bool _compactShape;
        private bool _reducedDetail;
        private LiquidGlassDebugView _view;
        private LiquidGlassFinishingParameters _finishingParameters = LiquidGlassFinishingParameters.Default;

        internal void SetGeometry(GlassWindow owner, Rect panel, double radius, Size size, double scale)
        {
            if (ReferenceEquals(_owner, owner) && _panel == panel && _radius == radius && _size == size && _scale == scale) return;
            _owner = owner;
            _panel = panel;
            _radius = radius;
            _size = size;
            _scale = scale;
            _drawing = null;
            InvalidateVisual();
        }

        internal void SetCompactShape(bool compact)
        {
            if (_compactShape == compact) return;
            _compactShape = compact;
            _drawing = null;
            InvalidateVisual();
        }

        internal void SetReducedDetail(bool reduced)
        {
            if (_reducedDetail == reduced) return;
            _reducedDetail = reduced;
            _drawing = null;
            InvalidateVisual();
        }

        internal void SetAppearance(LiquidGlassDebugView view, LiquidGlassFinishingParameters parameters)
        {
            parameters = parameters.Normalize();
            if (_view == view && _finishingParameters == parameters) return;
            _view = view;
            _finishingParameters = parameters;
            _drawing = null;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_panel.Width <= 0 || _panel.Height <= 0 || _size.Width <= 0 || _size.Height <= 0) return;
            if (_drawing is null)
            {
                _drawing = new DrawingGroup();
                using (var context = _drawing.Open())
                {
                    var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(_size)), CreatePanel(_panel, _radius));
                    outside.Freeze();
                    context.PushClip(outside);
                    if (LiquidGlassFinishComposer.IncludesBaselineShadow(_view))
                    {
                        // Keep the pre-Chapter-08 shadow as the zero-addition fallback so
                        // Tint=0 and Shadow=0 reproduce the established Chapter 07 look.
                        DrawSoftShadow(context, 34 * _scale, 8 * _scale, .15,
                            Color.FromRgb(18, 24, 36));
                        DrawSoftShadow(context, 10 * _scale, 2 * _scale, .10,
                            Color.FromRgb(18, 24, 36));
                    }
                    var (ambient, contact) = LiquidGlassFinishComposer.ShadowSelection(_view);
                    if (ambient && _finishingParameters.AmbientShadowOpacity > 0)
                        DrawSoftShadow(context,
                            _finishingParameters.AmbientShadowExtentDip * _scale,
                            _finishingParameters.AmbientShadowOffsetDip * _scale,
                            _finishingParameters.AmbientShadowOpacity,
                            Color.FromRgb(18, 24, 36));
                    if (contact && _finishingParameters.ContactShadowOpacity > 0)
                        DrawSoftShadow(context,
                            _finishingParameters.ContactShadowExtentDip * _scale,
                            _finishingParameters.ContactShadowOffsetDip * _scale,
                            _finishingParameters.ContactShadowOpacity,
                            Color.FromRgb(12, 17, 27));
                    context.Pop();
                }
                _drawing.Freeze();
            }

            drawingContext.DrawDrawing(_drawing);
        }

        private void DrawSoftShadow(DrawingContext context, double extent, double offset, double opacity, Color color)
        {
            // Only the launcher opts into this while its silhouette is changing.
            // Integrate the same falloff over wider bands; resting shadows retain
            // the original 32 contours and all other windows keep their defaults.
            var steps = _reducedDetail ? 16 : 32;
            for (var step = steps; step >= 1; step--)
            {
                var distance = extent * step / steps;
                var previous = extent * (step - 1) / steps;
                // Nested translucent contours integrate the Gaussian falloff without an
                // opaque source shape or an effect that could spill back into the glass.
                var alpha = opacity * (Math.Exp(-4.5 * previous * previous / (extent * extent))
                    - Math.Exp(-4.5 * distance * distance / (extent * extent)));
                var shape = _panel;
                shape.Offset(0, offset);
                shape.Inflate(distance, distance);
                var brush = new SolidColorBrush(color) { Opacity = alpha };
                brush.Freeze();
                context.DrawGeometry(brush, null, CreatePanel(shape, _radius + distance));
            }
        }

        private Geometry CreatePanel(Rect bounds, double radius) => _compactShape
            ? SquircleGlassGeometry.CreateCompact(bounds, radius)
            : _owner?.CreateGlassPanelGeometry(bounds, radius) ?? SquircleGlassGeometry.Create(bounds, radius);
    }
}
