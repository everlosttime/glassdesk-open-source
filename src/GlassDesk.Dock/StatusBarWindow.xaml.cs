using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Battery;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

/// <summary>Non-activating primary-display information bar, adapted from the community 1.3.1 patch.</summary>
public partial class StatusBarWindow : GlassWindow
{
    protected override bool SupportsDesktopLayer => true;
    protected override bool UseStatusBarTextBrightness => true;
    private const double BaseWidthDip = 380;
    private const double BaseHeightDip = 54;
    private const double BaseCornerRadiusDip = 25;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _visibilityTimer;
    private readonly StatusBarVisibilityController _visibility = new();
    private DockSettingsHost? _settings;
    private PixelRect _restingRect;
    private bool _started, _closed, _performanceSleeping, _displayPaused, _revealLocked;
    private bool _targetVisible, _captureRequested, _sampling, _fanSampling;
    private bool _preparingReveal;
    private bool? _animationTarget;
    private long _generation;
    private long _preparationStarted;
    private DateTimeOffset _lastSystemRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFanRefresh = DateTimeOffset.MinValue;
    private readonly NetworkSpeedReader _networkSpeedReader = new();
    private Func<(double X, StatusBarEdge Edge)>? _readPlacement;
    private Func<double>? _readSizeScale;
    private double? _appliedSizeScale;

    private static readonly DependencyProperty RevealProgressProperty = DependencyProperty.Register(
        "RevealProgress", typeof(double), typeof(StatusBarWindow),
        new PropertyMetadata(0d, (owner, _) => ((StatusBarWindow)owner).ApplySlide()));

    public Func<bool>? IsLauncherActive { get; set; }
    public bool IsPerformanceCaptureRequested => CanRun
        && (_targetVisible || IsPopoverVisible);
    public event EventHandler? PerformanceSurfaceVisibilityChanged;
    private DockSettings Settings => _settings?.Settings ?? new DockSettings();
    private bool CanRun => _started && !_closed && !_performanceSleeping && !_displayPaused
        && Settings.ShowStatusBar && PrimaryDisplaySession is { IsAvailable: true };

    public StatusBarWindow()
    {
        InitializeComponent();
        InitializeCompanions();
        ApplySecondaryItems();
        PeripheralBatteryService.Shared.Updated += OnPeripheralBatteryUpdated;
        PanelCornerRadius = new CornerRadius(25);
        GlassSurface.IsBackdropActive = () => CanRun && !_preparingReveal && IsVisible
            && (_targetVisible || (double)GetValue(RevealProgressProperty) > 0);
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => RefreshClockAndStatus();
        // Match Dock's reveal admission priority; ordinary background work must not starve pointer polling.
        _visibilityTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(100) };
        _visibilityTimer.Tick += (_, _) => EvaluateVisibility();
        Loaded += (_, _) => ApplySlide();
        SizeChanged += (_, _) => ApplySlide();
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 25;

    public void AttachSettings(DockSettingsHost settings)
    {
        if (_settings is not null) _settings.Changed -= OnSettingsChanged;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        OnSettingsChanged(this, EventArgs.Empty);
    }

    public void Start()
    {
        if (_closed) return;
        _started = true;
        RefreshDisplayPlacement();
    }

    public void ConfigurePlacement(Func<(double X, StatusBarEdge Edge)> readPlacement) => _readPlacement = readPlacement;

    public void ConfigureSizeScale(Func<double> readSizeScale) => _readSizeScale = readSizeScale;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ++_generation;
        ApplySecondaryItems();
        NetworkSpeedPanel.Visibility = Settings.ShowStatusBarNetworkSpeed ? Visibility.Visible : Visibility.Collapsed;
        if (!Settings.ShowStatusBarNetworkSpeed) _networkSpeedReader.Reset();
        ApplySizeScale();
        PositionTopRight();
        EvaluateVisibility();
        UpdatePeripheralBatterySampling(refresh: true);
    }

    public void ToggleRevealLock()
    {
        _revealLocked = !_revealLocked;
        EvaluateVisibility();
    }

    public void SetPerformanceSleeping(bool sleeping)
    {
        if (_performanceSleeping == sleeping) return;
        _performanceSleeping = sleeping;
        EvaluateVisibility();
    }

    public void StopForPrimaryDisplayChange()
    {
        _displayPaused = true;
        StopImmediately();
    }

    public void RefreshDisplayPlacement()
    {
        _displayPaused = false;
        BindCompanionWindows();
        ApplySizeScale(force: true);
        PositionTopRight();
        EvaluateVisibility();
    }

    private void PositionTopRight()
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return;
        var display = session.Display;
        var width = display.ToPixels(Width);
        var height = display.ToPixels(Height);
        var gap = display.ToPixels(18);
        var work = display.WorkArea;
        var (offsetX, edge) = _readPlacement?.Invoke() ?? (0d, StatusBarEdge.Top);
        var y = edge == StatusBarEdge.Bottom ? work.Bottom - height - gap : work.Y + gap;
        _restingRect = PlaceWindowWithinPrimaryWorkArea(new PixelRect(work.Right - width - gap + display.ToPixels(offsetX),
            y, width, height));
        ApplySlide();
    }

    private void ApplySizeScale(bool force = false)
    {
        var scale = Math.Clamp(_readSizeScale?.Invoke() ?? 1d,
            DockSettings.MinimumStatusBarSizeScale, DockSettings.MaximumStatusBarSizeScale);
        if (!force && _appliedSizeScale == scale) return;
        _appliedSizeScale = scale;
        Width = Math.Round(BaseWidthDip * scale, MidpointRounding.AwayFromZero);
        Height = Math.Round(BaseHeightDip * scale, MidpointRounding.AwayFromZero);
        PanelCornerRadius = new CornerRadius(BaseCornerRadiusDip * scale);
    }

    private void EvaluateVisibility()
    {
        if (!CanRun) { StopImmediately(); return; }
        if (_restingRect.Width <= 0) PositionTopRight();
        if (_restingRect.Width <= 0) { StopImmediately(); return; }
        _visibilityTimer.Start();
        var launcherActive = IsLauncherActive?.Invoke() == true;
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        UpdateCompanions(!fullscreen || launcherActive);
        var cursor = DisplayService.GetCursorPixelPoint();
        var over = cursor is { } point && IsOverStatusOrRevealZone(point.X, point.Y);
        var state = _visibility.Update(Settings, DateTime.UtcNow, over,
            ForegroundCoverage.IsCovered(_restingRect), fullscreen,
            launcherActive || IsPopoverVisible, _revealLocked, true, false, IsFixedDesktopLayer);
        SetTargetVisible(state != AutoHideState.Hidden);
    }

    private bool IsOverStatusOrRevealZone(int x, int y)
    {
        if (new PixelRect(_restingRect.X - 2, _restingRect.Y - 2,
                _restingRect.Width + 4, _restingRect.Height + 4).Contains(x, y)) return true;
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return false;
        var bounds = session.Display.Bounds;
        var edge = _readPlacement?.Invoke().Edge ?? StatusBarEdge.Top;
        var recovery = session.Display.ToPixels(16);
        if (x >= bounds.Right - recovery && x < bounds.Right
            && (edge == StatusBarEdge.Bottom ? y >= bounds.Bottom - recovery : y < bounds.Y + recovery)) return true;
        var height = session.Display.ToPixels(30);
        var padding = session.Display.ToPixels(18);
        var zoneY = edge == StatusBarEdge.Bottom ? _restingRect.Bottom : _restingRect.Y - height;
        return x >= _restingRect.X - padding && x < _restingRect.Right + padding
            && y >= Math.Max(bounds.Y, zoneY) && y < Math.Min(bounds.Bottom, zoneY + height);
    }

    private void SetTargetVisible(bool visible)
    {
        var continueVisibleSlide = visible && !_targetVisible && IsVisible
            && (double)GetValue(RevealProgressProperty) > 0;
        if (_targetVisible != visible)
        {
            var current = (double)GetValue(RevealProgressProperty);
            SetValue(RevealProgressProperty, current);
            BeginAnimation(RevealProgressProperty, null);
            _targetVisible = visible;
            _preparationStarted = 0;
            _animationTarget = null;
            if (!visible) StopSampling();
        }
        if (visible)
        {
            if (IsVisible) ShowSurface();
            GlassSurface.SetPerformanceSleeping(false);
            if (continueVisibleSlide)
            {
                // This panel has not finished concealing: keep its current pixels and
                // animation position. Reacquiring a desktop snapshot here stalls the reversal.
                _preparationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                AnimateVisibility(true);
            }
            if (_preparationStarted == 0 && _animationTarget is null)
            {
                _preparationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                // This API deliberately refuses visible, active owners. Prepare while hidden
                // (or explicitly inactive during a reversal), before exposing the first pixel.
                _preparingReveal = true;
                try
                {
                    _ = new WindowInteropHelper(this).EnsureHandle();
                    PositionTopRight();
                    GlassSurface.SetContainerTransitionBounds(new Rect(0, -Height, Width, Height * 2));
                    if (PrimaryDisplaySession is { } session)
                        GlassSurface.PrepareRevealMaterial(_restingRect, _restingRect, session.Display.Dpi);
                }
                finally { _preparingReveal = false; }
            }
            if (!IsVisible)
            {
                SetValue(RevealProgressProperty, 0d);
                ShowSurface();
                UpdateLayout();
                PositionTopRight();
            }
            if (_animationTarget is null)
            {
                GlassSurface.RequestBackdropRefresh(revealing: true);
                // Keep first pixels concealed until material is ready, with a bounded readable fallback.
                if (GlassSurface.IsCurrentRevealMaterialReady
                    || System.Diagnostics.Stopwatch.GetElapsedTime(_preparationStarted).TotalSeconds >= 3)
                    AnimateVisibility(true);
            }
            if (!_clockTimer.IsEnabled)
            {
                _lastSystemRefresh = _lastFanRefresh = DateTimeOffset.MinValue;
                _clockTimer.Start();
                RefreshClockAndStatus();
            }
        }
        else if (IsVisible && _animationTarget != false) AnimateVisibility(false);
        NotifyCaptureRequested();
        UpdatePeripheralBatterySampling();
    }

    private void AnimateVisibility(bool visible)
    {
        var current = (double)GetValue(RevealProgressProperty);
        SetValue(RevealProgressProperty, current);
        BeginAnimation(RevealProgressProperty, null);
        _animationTarget = visible;
        if (ThemeService.Current?.AnimationsEnabled == false) { FinishVisibility(visible); return; }
        var animation = new DoubleAnimation(current, visible ? 1 : 0,
            TimeSpan.FromMilliseconds(visible ? 150 : 130))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) =>
        {
            if (!_closed && _animationTarget == visible && _targetVisible == visible) FinishVisibility(visible);
        };
        BeginAnimation(RevealProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void FinishVisibility(bool visible)
    {
        // Commit the endpoint underneath the clock before removing it. Clearing first
        // briefly restores the previous base value, which moves the clip/text/shadow
        // back to the other endpoint and can present a full-panel flash through Show().
        SetValue(RevealProgressProperty, visible ? 1d : 0d);
        BeginAnimation(RevealProgressProperty, null);
        if (!visible)
        {
            // Pointer conceal is a warm suspension: stop reads without killing the native
            // owner. Game/session sleep and the explicit switch use StopImmediately instead.
            GlassSurface.RetireConcealedMaterial();
            HideSurface();
        }
    }

    private void ApplySlide()
    {
        if (GlassSurface is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var progress = Math.Clamp((double)GetValue(RevealProgressProperty), 0, 1);
        var offset = -ActualHeight * (1 - progress);
        StatusSlideTransform.Y = offset;
        var bounds = new Rect(0, offset, ActualWidth, ActualHeight);
        GlassSurface.SetPanelShape(bounds, 25);
        GlassSurface.SetContainerTransitionBounds(new Rect(0, -ActualHeight, ActualWidth, ActualHeight * 2));
        SetSharedPanelClip(bounds, 25);
        SetTransitionShadowShape(progress >= 1 ? null : bounds, 25, progress);
    }

    private void StopSampling()
    {
        PeripheralBatteryService.Shared.SetActive(false);
        _batterySamplingActive = false;
        _clockTimer.Stop();
        _networkSpeedReader.Reset();
        ++_generation;
    }

    private void StopImmediately()
    {
        UpdateCompanions(false);
        _visibilityTimer.Stop();
        StopSampling();
        _targetVisible = false;
        _animationTarget = null;
        _preparationStarted = 0;
        _visibility.Update(Settings, DateTime.UtcNow, false, false, false, false, false, false, true);
        GlassSurface.SetPerformanceSleeping(true);
        FinishVisibility(false);
        NotifyCaptureRequested();
    }

    private void NotifyCaptureRequested()
    {
        var requested = IsPerformanceCaptureRequested;
        if (_captureRequested == requested) return;
        _captureRequested = requested;
        PerformanceSurfaceVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshClockAndStatus()
    {
        if (!CanRun || !_targetVisible) return;
        var localNow = DateTimeOffset.Now;
        TimeText.Text = localNow.ToString("HH:mm", CultureInfo.CurrentCulture);
        DateText.Text = localNow.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        var now = DateTimeOffset.UtcNow;
        if (!_sampling)
        {
            var readSystem = now - _lastSystemRefresh >= TimeSpan.FromSeconds(5);
            if (readSystem) _lastSystemRefresh = now;
            _ = RefreshSystemAsync(_generation, readSystem);
        }
        if (IsSecondarySelected(StatusBarSecondaryItemCatalog.Fan)
            && !_fanSampling && now - _lastFanRefresh >= TimeSpan.FromSeconds(8))
        {
            _lastFanRefresh = now;
            _ = RefreshFanAsync(_generation);
        }
    }

    private bool CanApply(long generation) => generation == _generation && CanRun && _targetVisible;

    private async Task RefreshSystemAsync(long generation, bool readSystem)
    {
        _sampling = true;
        var readNetworkSpeed = Settings.ShowStatusBarNetworkSpeed;
        try
        {
            var result = await Task.Run(() => (Performance: SystemStatusReader.ReadPerformance(),
                NetworkSpeed: readNetworkSpeed ? _networkSpeedReader.Read() : null,
                Status: readSystem ? SystemStatusReader.Read() : (SystemStatusSnapshot?)null));
            if (!CanApply(generation)) return;
            CpuText.Text = result.Performance.CpuPercent is { } cpu ? $"{cpu}%" : "--";
            MemoryText.Text = result.Performance.MemoryPercent is { } memory ? $"{memory}%" : "--";
            if (Settings.ShowStatusBarNetworkSpeed && result.NetworkSpeed is { } speed)
            {
                DownloadSpeedText.Text = "↓ " + (speed.Connected ? NetworkSpeedReader.FormatRate(speed.DownloadBytesPerSecond) : "0 B/s");
                UploadSpeedText.Text = "↑ " + (speed.Connected ? NetworkSpeedReader.FormatRate(speed.UploadBytesPerSecond) : "0 B/s");
                NetworkSpeedPanel.ToolTip = speed.Connected
                    ? $"当前联网网卡：{speed.AdapterName}{Environment.NewLine}每秒采样实际上下行流量"
                    : "未连接网络";
            }
            if (result.Status is { } status) ApplySystemStatus(status);
        }
        catch (Exception)
        {
            // Native devices may disappear mid-read. Preserve the clock and report unavailable data.
            if (CanApply(generation))
            {
                CpuText.Text = MemoryText.Text = "--";
                if (readSystem) ApplySystemStatus(new(null, false, "未知", null, false));
            }
        }
        finally { _sampling = false; }
    }

    private void ApplySystemStatus(SystemStatusSnapshot status)
    {
        BatteryButton.Visibility = status.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        BatteryText.Text = status.BatteryPercent is { } battery ? $"{battery}%{(status.IsCharging ? " ⚡" : "")}"
            : status.IsCharging ? "电源" : "--";
        BatteryFill.Width = status.BatteryPercent is { } level ? Math.Clamp(14 * level / 100d, 1, 14) : 0;
        NetworkText.Text = status.NetworkLabel;
        var wired = status.NetworkLabel == "联网";
        NetworkWiredGlyph.Visibility = wired ? Visibility.Visible : Visibility.Collapsed;
        NetworkWifiGlyph.Visibility = wired ? Visibility.Collapsed : Visibility.Visible;
        VolumeText.Text = status.VolumePercent is { } volume ? $"{volume}%" : "--";
        VolumeGlyph.Text = status.IsMuted ? "\uE74F" : "\uE767";
    }

    private async Task RefreshFanAsync(long generation)
    {
        _fanSampling = true;
        try
        {
            var rpm = await Task.Run(FanSpeedReader.ReadRpm);
            if (CanApply(generation) && IsSecondarySelected(StatusBarSecondaryItemCatalog.Fan)) ApplyFanStatus(rpm);
        }
        catch (Exception) { if (CanApply(generation)) ApplyFanStatus(null); }
        finally { _fanSampling = false; }
    }

    private void ApplyFanStatus(int? rpm)
    {
        var visible = rpm is { } value && FanSpeedReader.IsPlausibleRpm(value);
        FanText.Text = visible ? $"{rpm:N0}" : "--";
        FanPanel.ToolTip = visible ? $"风扇转速：{rpm:N0} RPM" : "风扇转速：未获得受支持的实时转速读数";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ = WindowStyles.MakeNoActivate(new WindowInteropHelper(this).Handle);
        PositionTopRight();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        PeripheralBatteryService.Shared.Updated -= OnPeripheralBatteryUpdated;
        _visibilityTimer.Stop();
        StopSampling();
        if (_settings is not null) _settings.Changed -= OnSettingsChanged;
        _controlCenter.Close();
        _dateNotifications.Close();
        GlassSurface.Dispose();
        base.OnClosed(e);
    }

}
