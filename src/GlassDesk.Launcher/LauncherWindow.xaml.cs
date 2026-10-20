using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.UI.Controls;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

/// <summary>启动台界面；编辑与持久化交由独立服务处理。</summary>
public partial class LauncherWindow : GlassWindow
{
    protected override bool UseLauncherTextBrightness => true;

    protected override byte ResolveAcrylicTintAlpha(GlassDesk.UI.Theming.GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        _transitionPanelBoundsDip is { } transitionBounds
            ? SquircleGlassGeometry.Contains(transitionBounds, _transitionPanelRadiusDip, point)
            : SquircleGlassGeometry.Contains(bounds, radius, point);

    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(bounds, radius, point, out depth, out inwardNormal);

    protected override bool UsesSquirclePanelGeometryCore => true;

    private LauncherSettings _settings = new();
    private readonly LauncherViewState _view = new(5, 7);
    private AppServiceHost? _services;
    private LauncherWorkspace? _workspace;
    private string? _notice;
    private bool _imeComposing;
    private bool _preparedForShow;
    private int _openContextMenuCount;
    private int _keepOpenWhileModalCount;
    private PixelRect _panelPixelBounds;
    private PixelRect _transformHostBounds;
    private double _panelDipPerPixel = 1;
    private Rect? _transitionPanelBoundsDip;
    private double _transitionPanelRadiusDip;
    public event EventHandler? NavigationAvailabilityChanged;
    public bool CanNavigatePages => IsVisible && !_imeComposing && !_transformActive;

    public LauncherWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { _noticeTimer?.Stop(); CancelDrag(); ResetFolderPresentation(); StopPageSlide(); _iconLoader.Dispose(); };
        PageViewport.SizeChanged += (_, _) => StopPageSlide();
        Deactivated += OnLauncherDeactivated;
        IsVisibleChanged += OnVisibilityChanged;
        Loaded += (_, _) => RefreshLibrary();
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, _) => { _imeComposing = true; NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty); });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(SearchBox, (_, _) => { _imeComposing = true; NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty); });
        TextCompositionManager.AddPreviewTextInputHandler(SearchBox, (_, _) => { _imeComposing = false; NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty); });
    }

    public void AttachServices(AppServiceHost services, bool libraryAvailable)
    {
        _services = services;
        _workspace = new LauncherWorkspace(services, libraryAvailable);
        RefreshLibrary();
    }

    public void ApplySettings(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _view.SetGrid(Math.Clamp(settings.Rows, 2, 10), Math.Clamp(settings.Columns, 2, 12));
        PositionOnWorkingArea();
        RefreshLibrary();
    }

    public void PrepareToShow()
    {
        if (!_settings.RememberLastPage) _view.MoveToPage(0);
        RefreshLibrary(reuseUnchanged: true);
        _preparedForShow = IsLoaded && _services is not null && _workspace is not null;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _settings.FocusSearchOnOpen) SearchBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void RefreshLibrary() => RefreshLibrary(reuseUnchanged: false);

    private void RefreshLibrary(bool reuseUnchanged)
    {
        if (!IsLoaded || _services is null || _workspace is null) return;
        if (!reuseUnchanged) _iconLoader.Clear();
        try
        {
            _workspace.Reconcile(_view.Capacity);
        }
        catch (StorageException ex)
        {
            _notice = "启动台布局未能保存；已有应用库记录仍在。" + ex.Message;
        }
        RenderTiles(reuseUnchanged);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(80, appearance.LauncherCornerRadius);
    }

    public void RefreshDisplayPlacement() => PositionOnWorkingArea();
    public void PositionOnWorkingArea()
    {
        var session = PrimaryDisplaySession;
        if (session is { IsAvailable: false }) return;
        // Pointer location never selects another monitor. The launcher remains centered
        // on this process's Primary Monitor work area.
        PositionOnDisplay(session?.Display ?? DisplayService.Primary());
    }
    public void PositionOnDisplay(DisplayInfo? display)
    {
        var session = PrimaryDisplaySession;
        if (session is { IsAvailable: false }) return;
        display = session?.Display ?? display ?? DisplayService.Primary();
        if (display is null) return;
        var rect = WindowPlacementPolicy.Centered(display.WorkArea, _settings.WindowWorkAreaRatio, false);
        if (session is not null) rect = session.ClampWindowToWorkArea(rect);
        var dipPerPixel = 96.0 / Math.Max(1, display.Dpi);
        if (_transformActive) AbortContainerTransform();
        if (_panelPixelBounds.Width > 0 && (_panelPixelBounds != rect || _panelDipPerPixel != dipPerPixel))
        {
            // A previous path belongs to its physical bounds AND its DPI mapping.
            // Fall back to a settled panel rather than replay stale screen coordinates.
            _hasTransformPath = false;
            _containerMotion = null;
        }
        var existingHost = _transformHostBounds;
        var preserveExpandedHost = !IsVisible && existingHost.Width > 0 && existingHost != rect
            && ContainsPixelRect(existingHost, rect);
        _panelPixelBounds = rect;
        _panelDipPerPixel = dipPerPixel;
        var host = preserveExpandedHost ? existingHost : rect;
        _transformHostBounds = host;
        SetPanelRootLayout(rect, host, _panelDipPerPixel);
        if (preserveExpandedHost) return;
        Width = display.ToDips(host.Width);
        Height = display.ToDips(host.Height);
        if (Handle == 0) return;
        _ = DisplayService.MoveWindowToPixelRect(Handle,host);
        // Reapply the physical size using the fixed Primary Monitor DPI.
        Width = display.ToDips(host.Width);
        Height = display.ToDips(host.Height);
        _ = DisplayService.MoveWindowToPixelRect(Handle,host);
        SetPanelRootLayout(rect, host, _panelDipPerPixel);
    }

    private void SetPanelRootLayout(PixelRect panelBounds, PixelRect hostBounds, double dipPerPixel)
    {
        var panel = new Rect(
            (panelBounds.X - hostBounds.X) * dipPerPixel,
            (panelBounds.Y - hostBounds.Y) * dipPerPixel,
            panelBounds.Width * dipPerPixel,
            panelBounds.Height * dipPerPixel);
        LauncherPanelRoot.Width = panel.Width;
        LauncherPanelRoot.Height = panel.Height;
        TileGrid.Width = Math.Min(1120, Math.Max(1, panel.Width - 80));
        LauncherPanelRoot.Margin = new Thickness(panel.X, panel.Y, 0, 0);
        LauncherPanelRoot.RenderTransform = Transform.Identity;
        _transitionPanelBoundsDip = panel;
        _transitionPanelRadiusDip = PanelCornerRadius.TopLeft;
        var geometry = SquircleGlassGeometry.Create(panel, _transitionPanelRadiusDip);
        GlassSurface.SetPanelShape(panel, _transitionPanelRadiusDip, geometry);
        SetSharedPanelGeometry(geometry);
        ContainerTransformClipRoot.Clip = geometry;
        SetTransitionShadowShape(panel, _transitionPanelRadiusDip);
    }

    private static bool ContainsPixelRect(PixelRect outer, PixelRect inner) =>
        outer.X <= inner.X && outer.Y <= inner.Y && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    private void OnLauncherDeactivated(object? sender, EventArgs e)
    {
        if (_keepOpenWhileModalCount > 0 || _dragEdit is not null) return;

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            if (!IsVisible || IsActive || _keepOpenWhileModalCount > 0 || _openContextMenuCount > 0 || _dragEdit is not null)
                return;

            if (Application.Current.Windows.OfType<Window>().Any(window =>
                    window != this && window.Owner == this && window.IsVisible))
                return;

            CloseWithTransition();
        }));
    }

    /// <summary>Restore Launcher above Dock after a cross-window drag without taking focus.</summary>
    public void RaiseAboveDockAfterDrag() => _ = WindowStyles.BringToTopWithoutActivation(Handle);

    private ContextMenu CreateTrackedContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _openContextMenuCount++;
        menu.Closed += (_, _) =>
        {
            _openContextMenuCount = Math.Max(0, _openContextMenuCount - 1);
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if (IsVisible && !IsActive && _openContextMenuCount == 0 && _keepOpenWhileModalCount == 0)
                    CloseWithTransition();
            }));
        };
        return menu;
    }

    private bool ShowModalDialog(Func<bool?> showDialog)
    {
        _keepOpenWhileModalCount++;
        try { return showDialog() == true; }
        finally { _keepOpenWhileModalCount = Math.Max(0, _keepOpenWhileModalCount - 1); }
    }
    public void NavigatePage(int delta)
    {
        if (!CanNavigatePages || delta == 0) return;
        if (_dragOrigin is not null)
        {
            NavigateDragPage(Math.Sign(delta));
            return;
        }
        CancelDrag();
        ResetFolderPresentation();
        SlideToPage(Math.Sign(delta));
    }
    public void RefreshDiagnostics()
    {
        if (_notice is null)
        {
            StatusText.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Text = _notice;
        StatusText.Visibility = Visibility.Visible;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PositionOnWorkingArea();
        RefreshDiagnostics();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        if (e.NewValue is false)
        {
            StopPageSlide();
            _preparedForShow = false;
            AbortContainerTransform();
            CancelDrag();
            ResetFolderPresentation();
        }
        else
        {
            // ShowWithTransition has already reconciled and rendered this page. Repeating
            // it here rebuilds every tile/template immediately before the first animation frame.
            if (!_preparedForShow) RefreshLibrary();
            _preparedForShow = false;
            if (_settings.FocusSearchOnOpen)
                Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
