using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The media controller is disposed by the WPF window lifecycle in OnClosed.")]
public partial class MediaCapsuleWindow : GlassWindow
{
    private const double FullWidth = 378;
    private const double FullHeight = 76;
    private const double TabWidth = 34;
    private const double TabHeight = 54;
    private readonly MediaSessionController _controller = new();
    private readonly DispatcherTimer _timer;
    private PixelRect _anchor;
    private bool _refreshing;
    private bool _running;
    private bool _closed;
    private long _generation;
    private bool _hasMedia;
    private bool _collapsedToTab;
    private bool _modeTransitioning;
    private bool _tabDragging;
    private bool _tabDragged;
    private double _tabDragStartScreenY;
    private double _tabPointerOffsetY;
    private int? _tabTopOverride;

    public MediaCapsuleWindow()
    {
        InitializeComponent();
        PanelCornerRadius = new CornerRadius(38);
        GlassSurface.IsBackdropActive = () => IsVisible;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        SetCollapsedToTab(true);
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 38;

    internal void RequestGlassRefresh()
    {
        if (IsVisible) GlassSurface.RequestBackdropRefresh();
    }

    public void Start(PixelRect anchor)
    {
        if (_closed) return;
        _running = true;
        ++_generation;
        _anchor = anchor;
        GlassSurface.SetPerformanceSleeping(false);
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop()
    {
        _running = false;
        ++_generation;
        _timer.Stop();
        GlassSurface.SetPerformanceSleeping(true);
        Hide();
    }

    public void UpdateAnchor(PixelRect anchor)
    {
        if (_anchor.X == anchor.X && _anchor.Y == anchor.Y
            && _anchor.Width == anchor.Width && _anchor.Height == anchor.Height) return;
        if (IsVisible && !_modeTransitioning) GlassSurface.BeginRelocationHandoff();
        _anchor = anchor;
        if (IsVisible && !_modeTransitioning)
        {
            PositionForCurrentMode();
            Dispatcher.BeginInvoke(DispatcherPriority.Render,
                new Action(() => GlassSurface.RequestBackdropRefresh()));
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || !_running || _closed) return;
        _refreshing = true;
        var generation = _generation;
        try
        {
            var media = await _controller.ReadAsync();
            if (!_running || _closed || generation != _generation) return;
            if (!media.Available)
            {
                // Do not clear a useful capsule because a player temporarily removes
                // its Windows media session while paused. The controller retains that
                // snapshot and routes Play through the global media key until it returns.
                if (!_hasMedia)
                    ShowEmptyState();
            }
            else
            {
                _hasMedia = true;
                TitleText.Text = media.Title;
                ArtistText.Text = media.Artist;
                PlayPauseGlyph.Text = media.IsPlaying ? "\uE769" : "\uE768";
                PreviousButton.IsEnabled = media.CanGoPrevious;
                PlayPauseButton.IsEnabled = media.CanTogglePlayPause;
                NextButton.IsEnabled = media.CanGoNext;
                ApplyArtwork(media.Artwork);
            }

            if (!IsVisible) Show();
            if (!_modeTransitioning) PositionForCurrentMode();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowEmptyState()
    {
        TitleText.Text = "暂无媒体";
        ArtistText.Text = "播放音乐或视频后会自动显示";
        PlayPauseGlyph.Text = "\uE768";
        PreviousButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        ApplyArtwork(null);
    }

    private void ApplyArtwork(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            ArtworkImage.Source = bitmap;
            ArtworkImage.Visibility = Visibility.Visible;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (NotSupportedException)
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private PixelRect CalculateFullRect()
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return default;
        var display = session.Display;
        var width = display.ToPixels(FullWidth);
        var height = display.ToPixels(FullHeight);
        var gap = Math.Max(8, display.ToPixels(10));
        var minY = display.WorkArea.Y + gap;
        var maxY = Math.Max(minY, display.WorkArea.Bottom - height - gap);
        // Once the compact tab has been positioned by the user, keep the full
        // player aligned with that vertical position instead of jumping back to
        // the status-bar anchor.
        var desiredY = _tabTopOverride ?? (_anchor.Bottom + gap);
        return session.ClampWindowToWorkArea(new PixelRect(
            _anchor.Right - width, Math.Clamp(desiredY, minY, maxY), width, height));
    }

    private PixelRect CalculateTabRect()
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return default;
        var display = session.Display;
        var width = display.ToPixels(TabWidth);
        var height = display.ToPixels(TabHeight);
        var gap = Math.Max(8, display.ToPixels(10));
        var minY = display.WorkArea.Y + gap;
        var maxY = Math.Max(minY, display.WorkArea.Bottom - height - gap);
        var desiredY = _tabTopOverride ?? Math.Max(minY, _anchor.Bottom + gap);
        return session.ClampWindowToWorkArea(new PixelRect(
            display.WorkArea.Right - width,
            Math.Clamp(desiredY, minY, maxY),
            width,
            height));
    }

    private void PositionForCurrentMode()
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return;
        if (!_collapsedToTab)
        {
            _ = PlaceWindowWithinPrimaryWorkArea(CalculateFullRect());
            return;
        }

        _ = PlaceWindowWithinPrimaryWorkArea(CalculateTabRect());
    }

    private void SetCollapsedToTab(bool collapsed)
    {
        if (_collapsedToTab == collapsed || _modeTransitioning) return;
        _collapsedToTab = collapsed;
        if (!IsLoaded || !IsVisible || Handle == 0)
        {
            ApplyModeImmediately(collapsed);
            return;
        }

        AnimateModeChange(collapsed);
    }

    private void ApplyModeImmediately(bool collapsed)
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Width = collapsed ? TabWidth : FullWidth;
        Height = collapsed ? TabHeight : FullHeight;
        ApplyGlassMaterial(collapsed);
        PanelCornerRadius = new CornerRadius(collapsed ? 17 : 38);
        FullPlayerPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SideRevealButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        PositionForCurrentMode();
        RefreshBackdropAfterModeChange();
    }

    private void AnimateModeChange(bool collapsed)
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session
            || DisplayService.GetWindowPixelBounds(Handle) is not { Width: > 0, Height: > 0 } start)
        {
            ApplyModeImmediately(collapsed);
            return;
        }

        _modeTransitioning = true;
        var display = session.Display;
        var target = collapsed ? CalculateTabRect() : CalculateFullRect();
        var duration = TimeSpan.FromMilliseconds(180);

        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Width = display.ToDips(start.Width);
        Height = display.ToDips(start.Height);
        Left = display.ToDips(start.X);
        Top = display.ToDips(start.Y);

        if (!collapsed)
        {
            PanelCornerRadius = new CornerRadius(38);
            FullPlayerPanel.Visibility = Visibility.Visible;
            SideRevealButton.Visibility = Visibility.Collapsed;
        }

        var widthAnimation = CreateModeAnimation(Width, display.ToDips(target.Width), duration);
        widthAnimation.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Width = display.ToDips(target.Width);
            Height = display.ToDips(target.Height);
            Left = display.ToDips(target.X);
            Top = display.ToDips(target.Y);
            ApplyGlassMaterial(collapsed);
            PanelCornerRadius = new CornerRadius(collapsed ? 17 : 38);
            FullPlayerPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            SideRevealButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            _modeTransitioning = false;
            PositionForCurrentMode();
            RefreshBackdropAfterModeChange();
        };

        BeginAnimation(WidthProperty, widthAnimation, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(HeightProperty,
            CreateModeAnimation(Height, display.ToDips(target.Height), duration),
            HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(LeftProperty,
            CreateModeAnimation(Left, display.ToDips(target.X), duration),
            HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty,
            CreateModeAnimation(Top, display.ToDips(target.Y), duration),
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateModeAnimation(double from, double to, TimeSpan duration)
        => new(from, to, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

    private void ApplyGlassMaterial(bool collapsed)
    {
        GlassSurface.UseFrostedMaterial = !collapsed;
        GlassSurface.BackgroundClarityOverride = collapsed ? double.NaN : .35;
    }

    private void RefreshBackdropAfterModeChange()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (!IsVisible) return;
            GlassSurface.RequestBackdropRefresh(revealing: true);
        }));
    }

    private void OnManualCollapseClick(object sender, RoutedEventArgs e)
    {
        SetCollapsedToTab(true);
        if (!IsVisible) Show();
        PositionForCurrentMode();
    }

    private void OnSideRevealPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_modeTransitioning || DisplayService.GetWindowPixelBounds(Handle) is not { } bounds)
            return;

        var screenPosition = PointToScreen(e.GetPosition(this));
        _tabDragging = true;
        _tabDragged = false;
        _tabDragStartScreenY = screenPosition.Y;
        _tabPointerOffsetY = screenPosition.Y - bounds.Y;
        SideRevealButton.CaptureMouse();
    }

    private void OnSideRevealPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_tabDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var screenPosition = PointToScreen(e.GetPosition(this));
        if (!_tabDragged && Math.Abs(screenPosition.Y - _tabDragStartScreenY) < 4) return;

        _tabDragged = true;
        MoveCollapsedTabTo(screenPosition.Y - _tabPointerOffsetY);
    }

    private void OnSideRevealPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tabDragging) return;
        _tabDragging = false;
        if (SideRevealButton.IsMouseCaptured) SideRevealButton.ReleaseMouseCapture();
        if (_tabDragged)
        {
            _tabDragged = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Render,
                new Action(() => GlassSurface.RequestBackdropRefresh()));
        }
        else
        {
            SetCollapsedToTab(false);
            if (!IsVisible) Show();
            PositionForCurrentMode();
        }

        // The Button class would otherwise raise Click after this drag/click gesture.
        // Expanding is handled above so both paths are mutually exclusive.
        e.Handled = true;
    }

    private void MoveCollapsedTabTo(double desiredScreenY)
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return;
        var display = session.Display;
        var width = display.ToPixels(TabWidth);
        var height = display.ToPixels(TabHeight);
        var gap = Math.Max(8, display.ToPixels(10));
        var minY = display.WorkArea.Y + gap;
        var maxY = Math.Max(minY, display.WorkArea.Bottom - height - gap);
        _tabTopOverride = (int)Math.Round(Math.Clamp(desiredScreenY, minY, maxY));
        _ = PlaceWindowWithinPrimaryWorkArea(new PixelRect(
            display.WorkArea.Right - width,
            _tabTopOverride.Value,
            width,
            height));
    }

    private async void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        await _controller.PreviousAsync();
        await RefreshAsync();
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        await _controller.TogglePlayPauseAsync();
        await RefreshAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        await _controller.NextAsync();
        await RefreshAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ = WindowStyles.MakeNoActivate(new WindowInteropHelper(this).Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _running = false;
        ++_generation;
        _timer.Stop();
        _controller.Dispose();
        base.OnClosed(e);
    }
}
