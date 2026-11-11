using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock;

public enum DevicePickerKind { Wifi, Bluetooth, AudioOutput }

public partial class DevicePickerWindow : GlassWindow
{
    private long _generation;
    private bool _closed;
    private HwndSource? _source;
    private readonly PopupRevealAnimation _reveal;
    public DevicePickerKind Kind { get; private set; }
    public event EventHandler? OutputChanged;

    public DevicePickerWindow()
    {
        InitializeComponent();
        _reveal = new(this, PopupContent);
        PanelCornerRadius = new CornerRadius(23);
        GlassSurface.IsBackdropActive = () => IsVisible && !_closed;
        IsVisibleChanged += (_, _) => { if (!IsVisible) ++_generation; };
    }

    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 23;

    public void ShowNear(DevicePickerKind kind, PixelRect anchor)
    {
        if (_closed || PrimaryDisplaySession is not { IsAvailable: true } session) return;
        Kind = kind;
        Title = SelectorTitle.Text = kind switch { DevicePickerKind.Wifi => "Wi-Fi 网络", DevicePickerKind.Bluetooth => "蓝牙设备", _ => "声音输出设备" };
        RadioPowerButton.Visibility = kind == DevicePickerKind.AudioOutput ? Visibility.Collapsed : Visibility.Visible;
        SelectorSettingsButton.Content = kind switch { DevicePickerKind.Wifi => "打开 Wi-Fi 设置", DevicePickerKind.Bluetooth => "打开蓝牙设置", _ => "打开声音设置" };
        var display = session.Display;
        var opening = !IsVisible;
        if (opening) _ = new WindowInteropHelper(this).EnsureHandle();
        var target = PlaceWindowWithinPrimaryWorkArea(new(anchor.Right - display.ToPixels(Width), anchor.Bottom + display.ToPixels(8),
            display.ToPixels(Width), display.ToPixels(Height)));
        if (opening)
        {
            GlassSurface.PrepareRevealMaterial(target, target, display.Dpi);
            _reveal.Prepare();
            Show();
        }
        GlassSurface.RequestBackdropRefresh(revealing: true);
        _ = RefreshAsync();
    }

    public void RequestGlassRefresh() { if (IsVisible) GlassSurface.RequestBackdropRefresh(); }

    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        var kind = Kind;
        SelectorList.ItemsSource = new[] { new PickerItem("正在读取…", "", "\uE895", "", null) };
        IReadOnlyList<PickerItem> items;
        try
        {
            if (kind == DevicePickerKind.Wifi)
            {
                var networks = await WifiNetworkController.ScanAndGetAvailableNetworksAsync().WaitAsync(TimeSpan.FromSeconds(10));
                items = networks.Select(network => new PickerItem(network.Name,
                    network.IsConnected ? $"已连接 · 信号 {network.SignalQuality}%" : network.HasSavedProfile ? $"已保存 · 信号 {network.SignalQuality}%" : $"需要密码 · 信号 {network.SignalQuality}%",
                    "\uE701", network.IsConnected ? "断开" : network.HasSavedProfile ? "连接" : "设置", network)).ToArray();
            }
            else if (kind == DevicePickerKind.Bluetooth)
            {
                var devices = await BluetoothDeviceReader.GetPairedDevicesAsync().WaitAsync(TimeSpan.FromSeconds(10));
                items = devices.Select(device => new PickerItem(device.Name, device.IsConnected ? "已连接" : "已配对", "\uE702", "管理", device)).ToArray();
            }
            else
            {
                var devices = await Task.Run(AudioOutputController.GetDevices);
                items = devices.Select(device => new PickerItem(device.Name, device.IsDefault ? "当前输出设备" : "切换到此设备", "\uE767", device.IsDefault ? "✓" : "选择", device)).ToArray();
            }
        }
        catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            items = [new("暂时无法读取设备", "可刷新列表或打开系统设置", "\uE711", "设置", null)];
        }
        if (_closed || Dispatcher.HasShutdownStarted) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (generation != _generation || !IsVisible || _closed) return;
            SelectorList.ItemsSource = items.Count > 0 ? items : new[] { new PickerItem("没有可用设备", "可在 Windows 设置中检查设备和权限", "\uE711", "设置", null) };
        });
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A no-activate window cannot focus a ListBoxItem. WPF's default mouse
        // selection requires Focus() to succeed, so select the clicked row directly.
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(SelectorList, source) is not ListBoxItem row) return;
        e.Handled = true;
        SelectorList.SelectedItem = row.Content;
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectorList.SelectedItem is not PickerItem item) return;
        SelectorList.SelectedItem = null;
        var generation = _generation;
        if (item.Value is AudioOutputDevice output)
        {
            if (!await Task.Run(() => AudioOutputController.SetDefault(output.Id)))
            {
                if (IsVisible && generation == _generation)
                    SelectorList.ItemsSource = new[] { new PickerItem(output.Name, "切换未成功，请重试或打开声音设置", "\uE711", "设置", null) };
                return;
            }
            OutputChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (item.Value is WifiNetworkOption network)
        {
            var succeeded = await Task.Run(() => network.IsConnected ? WifiNetworkController.Disconnect(network)
                : network.HasSavedProfile && WifiNetworkController.Connect(network));
            if (!succeeded) { OnSettingsClick(sender, new RoutedEventArgs()); return; }
            await Task.Delay(1000);
        }
        else if (item.Value is BluetoothDeviceOption || item.Action == "设置") { OnSettingsClick(sender, new RoutedEventArgs()); return; }
        else return;
        if (!_closed && IsVisible && generation == _generation) await RefreshAsync();
    }

    private async void OnRadioPowerClick(object sender, RoutedEventArgs e)
    {
        var wifi = Kind == DevicePickerKind.Wifi;
        var state = wifi ? await SystemRadioController.ReadWifiAsync() : await SystemRadioController.ReadBluetoothAsync();
        var changed = state.Available && state.CanControl && (wifi ? await SystemRadioController.SetWifiAsync(!state.IsOn) : await SystemRadioController.SetBluetoothAsync(!state.IsOn));
        if (!changed) OnSettingsClick(sender, new RoutedEventArgs());
        if (!_closed && IsVisible) await RefreshAsync();
    }
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShellExecutor.Open(Kind switch
        { DevicePickerKind.Wifi => "ms-settings:network-wifi", DevicePickerKind.Bluetooth => "ms-settings:bluetooth", _ => "ms-settings:sound" });

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _ = WindowStyles.MakeNoActivate(handle);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(KeepOwnerActive);
    }
    private static nint KeepOwnerActive(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // WPF otherwise returns MA_ACTIVATE even with WS_EX_NOACTIVATE, causing
        // the control center to deactivate and hide before the row receives input.
        if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
        handled = true;
        return 3; // MA_NOACTIVATE: deliver the click while keeping the owner active.
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; ++_generation;
        _source?.RemoveHook(KeepOwnerActive);
        _source = null;
        GlassSurface.Dispose();
        base.OnClosed(e);
    }
    private sealed record PickerItem(string Title, string Subtitle, string Glyph, string Action, object? Value);
}
