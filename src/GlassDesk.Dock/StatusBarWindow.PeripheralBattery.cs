using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Battery;

namespace GlassDesk.Dock;

public partial class StatusBarWindow
{
    private readonly Dictionary<PeripheralBatteryCategory, string> _automaticBatteryKeys = new();
    private PeripheralBatteryCategory[] _batteryCategories = [];
    private bool _batterySamplingActive;

    private void UpdatePeripheralBatterySampling(bool refresh = false)
    {
        var service = PeripheralBatteryService.Shared;
        if (refresh)
        {
            var categories = new List<PeripheralBatteryCategory>();
            if (IsSecondarySelected(StatusBarSecondaryItemCatalog.MouseBattery)) categories.Add(PeripheralBatteryCategory.Mouse);
            if (IsSecondarySelected(StatusBarSecondaryItemCatalog.HeadsetBattery)) categories.Add(PeripheralBatteryCategory.Headset);
            if (IsSecondarySelected(StatusBarSecondaryItemCatalog.KeyboardBattery)) categories.Add(PeripheralBatteryCategory.Keyboard);
            if (!_batteryCategories.SequenceEqual(categories))
            {
                service.SetActive(false);
                _batterySamplingActive = false;
                _batteryCategories = categories.ToArray();
                service.SetCategories(_batteryCategories);
            }
        }
        var active = CanRun && _targetVisible && _batteryCategories.Length > 0;
        var changed = active != _batterySamplingActive;
        if (changed)
        {
            _batterySamplingActive = active;
            service.SetActive(active);
        }
        if (changed || refresh) ApplyPeripheralBatteryStatus(service.Current);
    }

    private void OnPeripheralBatteryUpdated(object? sender, EventArgs e)
    {
        var generation = Interlocked.Read(ref _generation);
        var snapshots = PeripheralBatteryService.Shared.Current;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CanApply(generation) && _batteryCategories.Length > 0)
                ApplyPeripheralBatteryStatus(snapshots);
        }));
    }

    private void ApplyPeripheralBatteryStatus(IReadOnlyList<PeripheralBatterySnapshot> snapshots)
    {
        ApplyPeripheralBatteryItem(snapshots, PeripheralBatteryCategory.Mouse, Settings.StatusBarMouseDeviceKey, MouseBatteryPanel, MouseBatteryText, "鼠标");
        ApplyPeripheralBatteryItem(snapshots, PeripheralBatteryCategory.Headset, Settings.StatusBarHeadsetDeviceKey, HeadsetBatteryPanel, HeadsetBatteryText, "耳机");
        ApplyPeripheralBatteryItem(snapshots, PeripheralBatteryCategory.Keyboard, Settings.StatusBarKeyboardDeviceKey, KeyboardBatteryPanel, KeyboardBatteryText, "键盘");
    }

    private void ApplyPeripheralBatteryItem(IReadOnlyList<PeripheralBatterySnapshot> snapshots,
        PeripheralBatteryCategory category, string? preferredKey, FrameworkElement panel, TextBlock text, string label)
    {
        _automaticBatteryKeys.TryGetValue(category, out var retainedKey);
        var snapshot = PeripheralBatteryService.Select(snapshots, category, preferredKey, retainedKey);
        if (snapshot is null)
        {
            text.Text = "--";
            panel.ToolTip = string.IsNullOrWhiteSpace(preferredKey)
                ? $"{label}电量：未发现已连接的设备"
                : $"{label}电量：绑定设备已断开或暂不可用";
            return;
        }
        if (string.IsNullOrWhiteSpace(preferredKey) && snapshot.Connected)
            _automaticBatteryKeys[category] = snapshot.DeviceKey;
        var available = snapshot.Connected && snapshot.Percent is >= 0 and <= 100;
        var availableLevel = snapshot.Connected && !string.IsNullOrWhiteSpace(snapshot.Level);
        text.Text = available ? $"{snapshot.Percent}%" : availableLevel ? snapshot.Level! : "--";
        var reason = !string.IsNullOrWhiteSpace(snapshot.Reason) ? snapshot.Reason
            : !snapshot.Connected ? "设备已断开"
            : available ? $"电量：{snapshot.Percent}%"
            : availableLevel ? $"电量级别：{snapshot.Level}" : "设备未提供受支持的电量读数";
        var charging = snapshot.Charging == true ? "\n正在充电" : "";
        var level = !available && !string.IsNullOrWhiteSpace(snapshot.Level) ? $"\n电量级别：{snapshot.Level}" : "";
        panel.ToolTip = $"{snapshot.Name}（{label}）\n{reason}{level}{charging}\n支持情况：{snapshot.Support}\n来源：{snapshot.Source}\n采样：{snapshot.SampleTime.ToLocalTime():HH:mm:ss}";
    }
}
