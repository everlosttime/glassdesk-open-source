using System.Windows;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock;

public partial class StatusBarWindow
{
    private readonly ControlCenterWindow _controlCenter = new();
    private readonly DateNotificationWindow _dateNotifications = new();

    private bool _companionsRunning;

    public void ConfigureGameSleep(Func<bool> isEnabled, Action<bool> setEnabled) =>
        _controlCenter.ConfigureGameSleep(isEnabled, setEnabled);

    public void ConfigureSettings(Action showSettings) => _controlCenter.ConfigureSettings(showSettings);

    public void ConfigureCalendar(GlassDesk.Core.Storage.SettingsStore settings) => _dateNotifications.AttachSettings(settings);

    public void ConfigureNearby(Action openNearby, Func<string?>? readIcon = null) =>
        _controlCenter.ConfigureNearby(openNearby, readIcon);

    private void InitializeCompanions()
    {
        _controlCenter.IsVisibleChanged += OnPopoverVisibilityChanged;
        _dateNotifications.IsVisibleChanged += OnPopoverVisibilityChanged;
        _controlCenter.MediaSnapshotChanged += (_, snapshot) =>
        {
            MusicTitleText.Text = snapshot.Available ? snapshot.Title : "暂无媒体";
            MusicTitleText.ToolTip = MusicTitleText.Text;
            MusicSummary.ToolTip = MusicTitleText.Text;
        };
    }

    private void UpdateCompanions(bool running)
    {
        if (_companionsRunning == running) return;
        _companionsRunning = running;
        if (running)
        {
            BindCompanionWindows();
            _controlCenter.StartMedia();
        }
        else
        {
            _controlCenter.StopMedia();
            _controlCenter.Hide();
            _dateNotifications.Hide();
        }
    }

    public void RequestGlassRefresh()
    {
        if (IsVisible && CanRun) GlassSurface.RequestBackdropRefresh();
        _controlCenter.RequestGlassRefresh();
        _dateNotifications.RequestGlassRefresh();
    }

    private void BindCompanionWindows()
    {
        if (PrimaryDisplaySession is not { } session) return;
        foreach (var window in new GlassWindow[] { _controlCenter, _dateNotifications })
        {
            if (window.PrimaryDisplaySession is null) window.BindPrimaryDisplay(session);
            window.Icon = Icon;
        }
    }

    private bool IsPopoverVisible => _controlCenter.IsVisible || _dateNotifications.IsVisible;

    private void OnPopoverVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ControlCenterArrowGlyph.Text = _controlCenter.IsVisible ? "\uE70E" : "\uE70D";
        if (_companionsRunning) EvaluateVisibility();
        NotifyCaptureRequested();
    }

    private static void OpenSettings(string uri) => _ = ShellExecutor.Open(uri);
    private void OnDateTimeClick(object sender, RoutedEventArgs e)
    {
        if (!CanRun) return;
        if (_dateNotifications.IsVisible)
        {
            _dateNotifications.Hide();
            return;
        }

        _controlCenter.Hide();
        _dateNotifications.ShowNear(_restingRect);
    }
    private void OnBatteryClick(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:batterysaver");
    private void OnControlCenterArrowClick(object sender, RoutedEventArgs e) => ToggleControlCenter();

    private void ToggleControlCenter()
    {
        if (!CanRun) return;
        if (_controlCenter.IsVisible)
        {
            _controlCenter.Hide();
            return;
        }

        _dateNotifications.Hide();
        _controlCenter.ShowNear(_restingRect);
    }
}
