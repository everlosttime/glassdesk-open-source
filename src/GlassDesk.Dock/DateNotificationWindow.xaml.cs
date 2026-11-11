using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock;

public partial class DateNotificationWindow : GlassWindow
{
    private readonly DispatcherTimer _timer;
    private readonly PopupRevealAnimation _reveal;
    private SettingsStore? _settings;
    private bool _updatingCalendarMode;
    private PixelRect _anchor;

    public DateNotificationWindow()
    {
        InitializeComponent();
        SetCalendarMode(false);
        _reveal = new(this, PopupContent);
        PanelCornerRadius = new CornerRadius(32);
        GlassSurface.IsBackdropActive = () => IsVisible;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshClock();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshClock();
                SetCalendarMode(_settings?.Current.Dock.CalendarShowLunar ?? LunarCalendarButton.IsChecked == true);
                MonthCalendar.DisplayDate = DateTime.Today;
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Hide();
        };
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 32;

    public void AttachSettings(SettingsStore settings)
    {
        _settings = settings;
        SetCalendarMode(settings.Current.Dock.CalendarShowLunar);
    }

    private void SetCalendarMode(bool lunar)
    {
        _updatingCalendarMode = true;
        try
        {
            SolarCalendarButton.IsChecked = !lunar;
            LunarCalendarButton.IsChecked = lunar;
            MonthCalendar.CalendarDayButtonStyle = lunar ? (Style)Resources["LunarCalendarDay"] : null;
            Height = lunar ? 560 : 500;
            if (IsVisible) PositionNearAnchor(_anchor);
            RefreshClock();
        }
        finally { _updatingCalendarMode = false; }
    }

    private void OnCalendarModeChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingCalendarMode || MonthCalendar is null) return;
        var lunar = ReferenceEquals(sender, LunarCalendarButton);
        if (_settings is { } settings)
        {
            var previous = settings.Current.Dock.CalendarShowLunar;
            settings.Current.Dock.CalendarShowLunar = lunar;
            try { settings.Save(); }
            catch (Exception error) when (error is StorageException or System.IO.IOException or UnauthorizedAccessException)
            {
                settings.Current.Dock.CalendarShowLunar = previous;
                SetCalendarMode(previous);
                LargeDateText.ToolTip = "日历偏好保存失败，请重试。";
                return;
            }
        }
        LargeDateText.ToolTip = null;
        SetCalendarMode(lunar);
    }

    internal void RequestGlassRefresh()
    {
        if (IsVisible) GlassSurface.RequestBackdropRefresh();
    }

    public void ShowNear(PixelRect anchor)
    {
        _anchor = anchor;
        if (!IsVisible)
        {
            // Create the hidden HWND first so the reveal material uses the exact
            // destination origin/DPI instead of a temporary (0,0) material stamp.
            _ = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            var target = PositionNearAnchor(anchor);
            if (PrimaryDisplaySession is { IsAvailable: true } session && target.Width > 0 && target.Height > 0)
                GlassSurface.PrepareRevealMaterial(target, target, session.Display.Dpi);
            _reveal.Prepare();
            Show();
        }
        PositionNearAnchor(anchor);
        Activate();
    }

    internal PixelRect CalculatePlacement(PixelRect anchor)
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return default;
        var display = session.Display;
        var width = display.ToPixels(Width);
        var height = display.ToPixels(Height);
        var gap = Math.Max(8, display.ToPixels(10));
        return session.ClampWindowToWorkArea(
            new PixelRect(anchor.Right - width, anchor.Bottom + gap, width, height));
    }

    private PixelRect PositionNearAnchor(PixelRect anchor)
        => PlaceWindowWithinPrimaryWorkArea(CalculatePlacement(anchor));

    private void RefreshClock()
    {
        var now = DateTime.Now;
        LargeTimeText.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        var lunar = LunarCalendarButton.IsChecked == true ? LunarDateTextConverter.Format(now, fullDate: true) : "";
        LargeDateText.Text = string.IsNullOrEmpty(lunar)
            ? now.ToString("yyyy年M月d日 dddd", CultureInfo.CurrentCulture)
            : lunar + now.ToString(" dddd", CultureInfo.CurrentCulture);
    }

    private void OnNotificationSettingsClick(object sender, RoutedEventArgs e)
        => _ = ShellExecutor.Open("ms-settings:notifications");

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
