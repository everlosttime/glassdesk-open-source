using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock;

public partial class ControlCenterWindow : GlassWindow
{
    private readonly DevicePickerWindow _devicePicker = new();
    private readonly PopupRevealAnimation _reveal;
    private bool _refreshing, _closed;
    private long _generation;
    private PixelRect _anchor;
    private Func<bool>? _isGameSleepEnabled;
    private Action<bool>? _setGameSleepEnabled;
    private Action? _showGlassDeskSettings;
    private Action? _openNearby;
    private Func<string?>? _readNearbyIcon;
    public event EventHandler<MediaSessionSnapshot>? MediaSnapshotChanged;

    public ControlCenterWindow()
    {
        InitializeComponent();
        _powerActions = new SystemPowerController(ConfirmPowerAction);
        InitializeApplicationMixer();
        _reveal = new(this, PopupContent);
        PanelCornerRadius = new CornerRadius(30);
        GlassSurface.IsBackdropActive = () => IsVisible && !_closed;
        MediaPlayer.SnapshotChanged += (_, snapshot) => MediaSnapshotChanged?.Invoke(this, snapshot);
        _devicePicker.IsVisibleChanged += (_, _) => UpdateArrows();
        _devicePicker.OutputChanged += (_, _) => { if (!_closed && IsVisible) _ = RefreshAsync(); };
        Deactivated += (_, _) => { if (!_powerActionPending) Hide(); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { ++_generation; PowerMenu.IsOpen = false; _devicePicker.Hide(); _mixerRefreshTimer.Stop(); }
            else if (_mixerExpanded) { _mixerRefreshTimer.Start(); _ = RefreshApplicationMixerAsync(); }
            UpdateArrows();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (PowerMenu.IsOpen) PowerMenu.IsOpen = false;
            else if (_devicePicker.IsVisible) _devicePicker.Hide(); else Hide();
            e.Handled = true;
        };
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 30;
    internal void StartMedia() => MediaPlayer.Start();
    internal void StopMedia() => MediaPlayer.Stop();
    internal void ConfigureGameSleep(Func<bool> isEnabled, Action<bool> setEnabled)
    {
        _isGameSleepEnabled = isEnabled;
        _setGameSleepEnabled = setEnabled;
        RefreshGameSleepControl();
    }

    internal void ConfigureSettings(Action showSettings)
    {
        _showGlassDeskSettings = showSettings;
        SettingsButton.IsEnabled = true;
    }

    internal void ConfigureNearby(Action openNearby, Func<string?>? readIcon)
    {
        _openNearby = openNearby;
        _readNearbyIcon = readIcon;
        NearbyButton.IsEnabled = true;
        RefreshNearbyIcon();
    }

    private void OnNearbyClick(object sender, RoutedEventArgs e)
    {
        if (_openNearby is null) return;
        Hide();
        _openNearby();
    }

    private void RefreshNearbyIcon()
    {
        BitmapImage? image = null;
        var path = _readNearbyIcon?.Invoke();
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
            }
            catch (Exception error) when (error is System.IO.IOException or System.IO.FileFormatException or UnauthorizedAccessException
                or UriFormatException or NotSupportedException or ArgumentException)
            { image = null; }
        }
        NearbyCustomIcon.Source = image;
        NearbyCustomIcon.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        NearbyDefaultGlyph.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RequestGlassRefresh()
    {
        if (IsVisible) GlassSurface.RequestBackdropRefresh();
        _devicePicker.RequestGlassRefresh();
    }

    public void ShowNear(PixelRect anchor)
    {
        if (_closed) return;
        RefreshNearbyIcon();
        _anchor = anchor;
        var opening = !IsVisible;
        if (opening) _ = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        var target = PositionNearAnchor();
        if (opening)
        {
            if (PrimaryDisplaySession is { IsAvailable: true } session)
                GlassSurface.PrepareRevealMaterial(target, target, session.Display.Dpi);
            _reveal.Prepare();
            Show();
        }
        Activate();
        GlassSurface.RequestBackdropRefresh(revealing: true);
        _ = RefreshAsync();
    }

    private PixelRect PositionNearAnchor()
    {
        if (PrimaryDisplaySession is not { IsAvailable: true } session) return default;
        var display = session.Display;
        var width = display.ToPixels(Width);
        var height = display.ToPixels(Height);
        var gap = display.ToPixels(16);
        var below = _anchor.Bottom + gap;
        var above = _anchor.Y - gap - height;
        var y = below + height <= display.WorkArea.Bottom ? below : above;
        return PlaceWindowWithinPrimaryWorkArea(new(_anchor.Right - width, y, width, height));
    }

    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        var statusTask = Task.Run(() => (Status: SystemStatusReader.Read(), Output: AudioOutputController.GetDevices().FirstOrDefault(device => device.IsDefault)));
        var wifiTask = SystemRadioController.ReadWifiAsync();
        var bluetoothTask = SystemRadioController.ReadBluetoothAsync();
        await Task.WhenAll(statusTask, wifiTask, bluetoothTask);
        if (_closed || Dispatcher.HasShutdownStarted) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closed || !IsVisible || generation != _generation) return;
            _refreshing = true;
            try
            {
                RefreshGameSleepControl();
                var (status, output) = statusTask.Result;
                VolumeSlider.Value = status.VolumePercent ?? 0;
                VolumeValueText.Text = status.VolumePercent is { } volume ? $"{volume}%" : "--";
                VolumeGlyph.Text = status.IsMuted ? "\uE74F" : "\uE767";
                OutputDeviceNameText.Text = output?.Name ?? "没有可用的输出设备";
                OutputDeviceNameText.ToolTip = OutputDeviceNameText.Text;
                ApplyRadioState(wifiTask.Result, WifiStateText, WifiIconBubble);
                ApplyRadioState(bluetoothTask.Result, BluetoothStateText, BluetoothIconBubble);
            }
            finally { _refreshing = false; }
        });
    }

    private static void ApplyRadioState(SystemRadioState state, TextBlock label, Border bubble)
    {
        label.Text = !state.Available ? "打开设置" : state.IsOn ? "已开启" : "已关闭";
        bubble.Background = new SolidColorBrush(state.Available && state.IsOn ? Color.FromRgb(65, 145, 255) : Color.FromArgb(48, 255, 255, 255));
    }

    private void OnGameSleepClick(object sender, RoutedEventArgs e)
    {
        if (_isGameSleepEnabled is null || _setGameSleepEnabled is null) return;
        _setGameSleepEnabled(!_isGameSleepEnabled());
        RefreshGameSleepControl();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _showGlassDeskSettings?.Invoke();

    private void RefreshGameSleepControl()
    {
        var available = _isGameSleepEnabled is not null && _setGameSleepEnabled is not null;
        var sleeping = available && _isGameSleepEnabled!();
        GameSleepButton.IsEnabled = available;
        GameSleepStateText.Text = !available
            ? "当前不可用"
            : sleeping
                ? "已开启 · 快捷键或托盘菜单恢复"
                : "开启后隐藏桌面组件";
        GameSleepIconBubble.Background = new SolidColorBrush(sleeping
            ? Color.FromRgb(81, 156, 255)
            : Color.FromArgb(48, 255, 255, 255));
    }

    private void OnWifiClick(object sender, RoutedEventArgs e) => TogglePicker(DevicePickerKind.Wifi, WifiButton);
    private void OnBluetoothClick(object sender, RoutedEventArgs e) => TogglePicker(DevicePickerKind.Bluetooth, BluetoothButton);
    private void OnOutputDeviceClick(object sender, RoutedEventArgs e) => TogglePicker(DevicePickerKind.AudioOutput, OutputDeviceArrowButton);

    private void TogglePicker(DevicePickerKind kind, FrameworkElement button)
    {
        if (_closed || !IsVisible || PrimaryDisplaySession is not { IsAvailable: true } session) return;
        if (_devicePicker.IsVisible && _devicePicker.Kind == kind) { _devicePicker.Hide(); return; }
        if (_devicePicker.PrimaryDisplaySession is null) _devicePicker.BindPrimaryDisplay(session);
        _devicePicker.Owner ??= this;
        _devicePicker.Icon = Icon;
        var origin = button.PointToScreen(new Point(0, button.ActualHeight));
        _devicePicker.ShowNear(kind, new((int)origin.X, (int)origin.Y, (int)Math.Max(1, button.ActualWidth * session.Display.Dpi / 96d), 0));
        UpdateArrows();
    }

    private void UpdateArrows()
    {
        UpdateArrow(WifiButton, WifiArrowGlyph, DevicePickerKind.Wifi, "Wi-Fi 网络");
        UpdateArrow(BluetoothButton, BluetoothArrowGlyph, DevicePickerKind.Bluetooth, "蓝牙设备");
        UpdateArrow(OutputDeviceArrowButton, OutputDeviceArrowGlyph, DevicePickerKind.AudioOutput, "输出设备");
    }
    private void UpdateArrow(Button button, TextBlock glyph, DevicePickerKind kind, string label)
    {
        var expanded = _devicePicker.IsVisible && _devicePicker.Kind == kind;
        glyph.Text = expanded ? "\uE70E" : "\uE70D";
        button.ToolTip = (expanded ? "收起" : "展开") + label;
        AutomationProperties.SetName(button, (string)button.ToolTip);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing) return;
        var value = (int)Math.Round(e.NewValue);
        _ = SystemStatusReader.SetVolumePercent(value);
        if (value > 0) _ = SystemStatusReader.SetMuted(false);
        VolumeValueText.Text = $"{value}%";
        VolumeGlyph.Text = value == 0 ? "\uE74F" : "\uE767";
    }
    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _ = SystemStatusReader.SetMuted(!SystemStatusReader.Read().IsMuted);
        _ = RefreshAsync();
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; ++_generation;
        PowerMenu.IsOpen = false;
        _mixerClosed = true;
        _mixerRefreshTimer.Stop();
        _mixerWriteTimer.Stop();
        _devicePicker.Close();
        MediaPlayer.Dispose();
        GlassSurface.Dispose();
        base.OnClosed(e);
    }
}
