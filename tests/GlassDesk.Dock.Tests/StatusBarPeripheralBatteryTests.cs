using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Battery;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class StatusBarPeripheralBatteryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void LevelReadingsAndProviderReasonsArePreservedWithoutExposingBindingKeys()
    {
        StaTest.Run(() =>
        {
            var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            var bar = new StatusBarWindow();
            try
            {
                bar.AttachSettings(host);
                host.Update(new() { StatusBarSecondaryItems = ["mouseBattery", "headsetBattery", "keyboardBattery"], StatusBarMouseDeviceKey = "private-device-id", StatusBarKeyboardDeviceKey = "keyboard" });
                var apply = typeof(StatusBarWindow).GetMethod("ApplyPeripheralBatteryStatus", Private)!;
                PeripheralBatterySnapshot[] snapshots = [
                    new("headset", "Headset", PeripheralBatteryCategory.Headset, true, null, false, DateTimeOffset.UtcNow, "Windows", "支持档位", "设备上报电量档位", Level: "高"),
                    new("keyboard", "Keyboard", PeripheralBatteryCategory.Keyboard, false, null, null, DateTimeOffset.UtcNow, "HID", "尚未验证", "设备休眠或读取失败")];
                apply.Invoke(bar, [snapshots]);
                Assert.Equal("高", ((TextBlock)bar.FindName("HeadsetBatteryText")).Text);
                var keyboardTip = ((FrameworkElement)bar.FindName("KeyboardBatteryPanel")).ToolTip!.ToString()!;
                Assert.Contains("休眠或读取失败", keyboardTip);
                Assert.Contains("尚未验证", keyboardTip);
                Assert.DoesNotContain("private-device-id", ((FrameworkElement)bar.FindName("MouseBatteryPanel")).ToolTip!.ToString());
            }
            finally { bar.Close(); }
        });
    }

    [Fact]
    public void ManualOfflineAndUnknownBatteryKeepTheirSlotAndExplainMissingReadings()
    {
        StaTest.Run(() =>
        {
            var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            var bar = new StatusBarWindow();
            try
            {
                bar.AttachSettings(host);
                host.Update(new() { StatusBarSecondaryItems = ["mouseBattery", "headsetBattery", "keyboardBattery"], StatusBarMouseDeviceKey = "offline-mouse" });
                var apply = typeof(StatusBarWindow).GetMethod("ApplyPeripheralBatteryStatus", Private);
                Assert.NotNull(apply);
                PeripheralBatterySnapshot[] snapshots = [
                    new("online-mouse", "Same name", PeripheralBatteryCategory.Mouse, true, 99, false, DateTimeOffset.UtcNow, "HID", "supported", ""),
                    new("offline-mouse", "Same name", PeripheralBatteryCategory.Mouse, false, 55, false, DateTimeOffset.UtcNow, "HID", "supported", "设备已断开"),
                    new("unknown-headset", "Unknown headset", PeripheralBatteryCategory.Headset, true, null, null, DateTimeOffset.UtcNow, "HID", "unknown", "型号协议暂不支持")];
                apply.Invoke(bar, [snapshots]);
                Assert.Equal("--", ((TextBlock)bar.FindName("MouseBatteryText")).Text);
                Assert.Contains("断开", ((FrameworkElement)bar.FindName("MouseBatteryPanel")).ToolTip!.ToString());
                Assert.Equal("--", ((TextBlock)bar.FindName("HeadsetBatteryText")).Text);
                Assert.Contains("协议", ((FrameworkElement)bar.FindName("HeadsetBatteryPanel")).ToolTip!.ToString());
                Assert.Equal("--", ((TextBlock)bar.FindName("KeyboardBatteryText")).Text);
                foreach (var name in new[] { "MouseBatteryPanel", "HeadsetBatteryPanel", "KeyboardBatteryPanel" })
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)bar.FindName(name)).Visibility);
            }
            finally { bar.Close(); }
        });
    }

    [Fact]
    public void PollingFollowsVisibilitySleepEnableAndSelectionWhileMediaSummaryIsIndependent()
    {
        StaTest.Run(() =>
        {
            var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            var bar = new StatusBarWindow();
            bar.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                bar.AttachSettings(host);
                host.Update(new() { StatusBarSecondaryItems = ["mouseBattery"] });
                bar.Start();
                Assert.True(PeripheralBatteryService.Shared.IsActive);
                var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", Private)!.GetValue(bar)!;
                var player = (MediaPlaybackControl)center.FindName("MediaPlayer");
                var mediaTimer = (DispatcherTimer)typeof(MediaPlaybackControl).GetField("_timer", Private)!.GetValue(player)!;
                Assert.True(mediaTimer.IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)bar.FindName("MusicSummary")).Visibility);
                typeof(StatusBarWindow).GetMethod("SetTargetVisible", Private)!.Invoke(bar, [false]);
                Assert.False(PeripheralBatteryService.Shared.IsActive);
                typeof(StatusBarWindow).GetMethod("SetTargetVisible", Private)!.Invoke(bar, [true]);
                Assert.True(PeripheralBatteryService.Shared.IsActive);
                bar.SetPerformanceSleeping(true);
                Assert.False(PeripheralBatteryService.Shared.IsActive);
                bar.SetPerformanceSleeping(false);
                Assert.True(PeripheralBatteryService.Shared.IsActive);
                host.Update(new() { StatusBarSecondaryItems = [] });
                Assert.False(PeripheralBatteryService.Shared.IsActive);
                Assert.True(mediaTimer.IsEnabled);
                host.Update(new() { StatusBarSecondaryItems = ["mouseBattery"], ShowStatusBar = false });
                Assert.False(PeripheralBatteryService.Shared.IsActive);
            }
            finally { bar.Close(); }
            Assert.False(PeripheralBatteryService.Shared.IsActive);
        });
    }

    [Fact]
    public void QueuedReadingFromPreviousSettingsGenerationCannotOverwriteNewBinding()
    {
        StaTest.Run(() =>
        {
            var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            var bar = new StatusBarWindow();
            bar.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var current = typeof(PeripheralBatteryService).GetField("_current", Private)!;
            try
            {
                bar.AttachSettings(host);
                host.Update(new() { StatusBarSecondaryItems = ["mouseBattery"], StatusBarMouseDeviceKey = "mouse-a" });
                bar.Start();
                // Cancel hardware discovery before supplying two completed query generations.
                PeripheralBatteryService.Shared.SetActive(false);
                PeripheralBatterySnapshot[] old = [new("mouse-a", "First mouse", PeripheralBatteryCategory.Mouse, true, 10, false, DateTimeOffset.UtcNow, "fixture", "supported", "")];
                current.SetValue(PeripheralBatteryService.Shared, old);
                typeof(StatusBarWindow).GetMethod("OnPeripheralBatteryUpdated", Private)!.Invoke(bar, [null, EventArgs.Empty]);
                PeripheralBatterySnapshot[] replacement = [new("mouse-b", "Second mouse", PeripheralBatteryCategory.Mouse, true, 90, false, DateTimeOffset.UtcNow, "fixture", "supported", "")];
                current.SetValue(PeripheralBatteryService.Shared, replacement);
                host.Update(new() { StatusBarSecondaryItems = ["mouseBattery"], StatusBarMouseDeviceKey = "mouse-b" });
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Assert.Equal("90%", ((TextBlock)bar.FindName("MouseBatteryText")).Text);
            }
            finally
            {
                bar.Close();
                current.SetValue(PeripheralBatteryService.Shared, Array.Empty<PeripheralBatterySnapshot>());
            }
        });
    }

    [Fact]
    public void UnselectedFanDoesNotScheduleSensorPolling()
    {
        StaTest.Run(() =>
        {
            var bar = new StatusBarWindow();
            bar.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                typeof(StatusBarWindow).GetField("_started", Private)!.SetValue(bar, true);
                typeof(StatusBarWindow).GetField("_targetVisible", Private)!.SetValue(bar, true);
                typeof(StatusBarWindow).GetMethod("RefreshClockAndStatus", Private)!.Invoke(bar, null);
                Assert.Equal(DateTimeOffset.MinValue, typeof(StatusBarWindow).GetField("_lastFanRefresh", Private)!.GetValue(bar));
            }
            finally { bar.Close(); }
        });
    }
}
