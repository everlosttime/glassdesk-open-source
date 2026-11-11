using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Battery;

namespace GlassDesk.Settings;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Window lifetime owns discovery cancellation; hiding or closing cancels and disposes the source.")]
public partial class SettingsWindow
{
    private bool _loadingStatusBarSecondaryControls;
    private CancellationTokenSource? _statusBarBatteryDiscovery;
    private CancellationTokenSource? _statusBarBatteryRefreshOwner;
    private IReadOnlyList<PeripheralBatterySnapshot> _statusBarBatteryDevices = PeripheralBatteryService.Shared.Current;

    private sealed record BatteryDeviceOption(string? DeviceKey, string Label, string Details);

    private CheckBox[] StatusBarSecondaryChecks => [StatusBarCpuCheckBox, StatusBarMemoryCheckBox,
        StatusBarFanCheckBox, StatusBarMouseBatteryCheckBox, StatusBarHeadsetBatteryCheckBox,
        StatusBarKeyboardBatteryCheckBox, StatusBarMusicCheckBox];

    private void InitializeStatusBarSecondaryControls()
    {
        IsVisibleChanged += OnStatusBarBatteryVisibilityChanged;
        Closed += (_, _) => StopStatusBarBatteryDiscovery();
    }

    private void LoadStatusBarSecondaryControls()
    {
        if (_dataStore is null) return;
        _loadingStatusBarSecondaryControls = true;
        try
        {
            var dock = _dataStore.Settings.Current.Dock;
            var items = StatusBarSecondaryItemCatalog.Normalize(dock.StatusBarSecondaryItems);
            foreach (var check in StatusBarSecondaryChecks)
            {
                var selected = items.Contains((string)check.Tag, StringComparer.Ordinal);
                check.IsChecked = selected;
                check.IsEnabled = selected || items.Length < StatusBarSecondaryItemCatalog.MaximumItems;
            }
            StatusBarSecondaryCount.Text = $"{items.Length}/3";
            PopulateStatusBarBatteryDevices(_statusBarBatteryDevices);
        }
        finally { _loadingStatusBarSecondaryControls = false; }
    }

    private void OnStatusBarSecondaryItemChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingStatusBarSecondaryControls || _loadingDockControls || _dataStore is null) return;
        var selected = StatusBarSecondaryChecks.Where(check => check.IsChecked == true)
            .Select(check => (string)check.Tag).ToArray();
        if (selected.Length > StatusBarSecondaryItemCatalog.MaximumItems)
        {
            LoadStatusBarSecondaryControls();
            InformationBarSettingsStatus.Text = "最多显示 3 项，请先取消一项。";
            return;
        }
        var snapshot = SettingsPageEditor.Clone(_dataStore.Settings.Current);
        var candidate = SettingsPageEditor.Clone(_dataStore.Settings.Current).Dock;
        candidate.StatusBarSecondaryItems = selected;
        _dataStore.Settings.Current.Dock = candidate;
        SavePageSnapshot(snapshot, "信息栏", InformationBarSettingsStatus);
        LoadStatusBarSecondaryControls();
    }

    private void PopulateStatusBarBatteryDevices(IReadOnlyList<PeripheralBatterySnapshot> devices)
    {
        if (_dataStore is null) return;
        _statusBarBatteryDevices = devices;
        var wasLoading = _loadingStatusBarSecondaryControls;
        _loadingStatusBarSecondaryControls = true;
        try
        {
            var dock = _dataStore.Settings.Current.Dock;
            Populate(StatusBarMouseDeviceCombo, PeripheralBatteryCategory.Mouse, dock.StatusBarMouseDeviceKey);
            Populate(StatusBarHeadsetDeviceCombo, PeripheralBatteryCategory.Headset, dock.StatusBarHeadsetDeviceKey);
            Populate(StatusBarKeyboardDeviceCombo, PeripheralBatteryCategory.Keyboard, dock.StatusBarKeyboardDeviceKey);
        }
        finally { _loadingStatusBarSecondaryControls = wasLoading; }

        void Populate(ComboBox combo, PeripheralBatteryCategory category, string? key)
        {
            var options = new List<BatteryDeviceOption> { new(null, "自动选择", "自动选择已连接设备；耳机优先系统默认音频设备。") };
            var candidates = devices.Where(d => d.Category == category)
                         .DistinctBy(d => d.DeviceKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(d => d.DeviceKey, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var device in candidates)
            {
                var sameName = candidates.Where(d => string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var disambiguation = sameName.Length > 1 && device.ReceiverSlot is null
                    ? $" · 设备 {Array.IndexOf(sameName, device) + 1}" : string.Empty;
                var state = !device.Connected ? "离线" : device.Percent is { } percent ? $"{percent}%"
                    : !string.IsNullOrWhiteSpace(device.Level) ? device.Level : "--";
                var slot = device.ReceiverSlot is { } receiverSlot ? $" · 槽位 {receiverSlot}" : string.Empty;
                var details = $"支持状态：{device.Support}\n电量来源：{device.Source}\n{device.Reason}";
                options.Add(new(device.DeviceKey, $"{device.Name}{slot}{disambiguation} · {state}", details));
            }
            if (key is not null && !options.Any(option => string.Equals(option.DeviceKey, key, StringComparison.OrdinalIgnoreCase)))
                options.Add(new(key, "已选择的设备（离线）", "手动选择已保留；设备重新连接后继续读取。"));
            combo.ItemsSource = options;
            combo.SelectedItem = options.First(option => string.Equals(option.DeviceKey, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnStatusBarBatteryDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStatusBarSecondaryControls || _loadingDockControls || _dataStore is null
            || sender is not ComboBox { SelectedItem: BatteryDeviceOption option }) return;
        var snapshot = SettingsPageEditor.Clone(_dataStore.Settings.Current);
        var dock = SettingsPageEditor.Clone(_dataStore.Settings.Current).Dock;
        if (ReferenceEquals(sender, StatusBarMouseDeviceCombo)) dock.StatusBarMouseDeviceKey = option.DeviceKey;
        else if (ReferenceEquals(sender, StatusBarHeadsetDeviceCombo)) dock.StatusBarHeadsetDeviceKey = option.DeviceKey;
        else if (ReferenceEquals(sender, StatusBarKeyboardDeviceCombo)) dock.StatusBarKeyboardDeviceKey = option.DeviceKey;
        _dataStore.Settings.Current.Dock = dock;
        SavePageSnapshot(snapshot, "信息栏", InformationBarSettingsStatus);
        LoadStatusBarSecondaryControls();
    }

    private void OnStatusBarBatteryVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) { StopStatusBarBatteryDiscovery(); return; }
        if (_statusBarBatteryDiscovery is not null) return;
        _statusBarBatteryDiscovery = new();
        PeripheralBatteryService.Shared.Updated += OnStatusBarBatteryDevicesUpdated;
        PopulateStatusBarBatteryDevices(PeripheralBatteryService.Shared.Current);
    }

    private void StopStatusBarBatteryDiscovery()
    {
        PeripheralBatteryService.Shared.Updated -= OnStatusBarBatteryDevicesUpdated;
        _statusBarBatteryDiscovery?.Cancel();
        _statusBarBatteryDiscovery?.Dispose();
        _statusBarBatteryDiscovery = null;
    }

    private async void OnStatusBarBatteryDropDownOpened(object? sender, EventArgs e)
        => await RefreshStatusBarBatteryDevicesAsync();

    private async Task RefreshStatusBarBatteryDevicesAsync()
    {
        if (_statusBarBatteryDiscovery is not { } discovery || _dataStore is null
            || ReferenceEquals(_statusBarBatteryRefreshOwner, discovery)) return;
        _statusBarBatteryRefreshOwner = discovery;
        StatusBarBatteryDiscoveryStatus.Text = "正在查找电量设备…";
        try
        {
            var devices = await PeripheralBatteryService.Shared.RefreshAsync(discovery.Token);
            if (!ReferenceEquals(discovery, _statusBarBatteryDiscovery)) return;
            PopulateStatusBarBatteryDevices(devices);
            StatusBarBatteryDiscoveryStatus.Text = devices.Count == 0 ? "尚未发现可用设备；已手动选择的设备会继续保留。" : "设备列表已更新；无法读取电量时显示 --。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(discovery, _statusBarBatteryDiscovery))
                StatusBarBatteryDiscoveryStatus.Text = "设备查找未完成：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_statusBarBatteryRefreshOwner, discovery)) _statusBarBatteryRefreshOwner = null;
        }
    }

    private void OnStatusBarBatteryDevicesUpdated(object? sender, EventArgs e)
    {
        var discovery = _statusBarBatteryDiscovery;
        if (discovery is null || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(discovery, _statusBarBatteryDiscovery))
                PopulateStatusBarBatteryDevices(PeripheralBatteryService.Shared.Current);
        });
    }
}
