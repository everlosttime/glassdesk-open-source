using System.IO;
using System.Reflection;
using System.Windows.Controls;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.Platform.Battery;
using GlassDesk.UI.Settings;
using System.Windows.Threading;

namespace GlassDesk.Dock.Tests;

public sealed class StatusBarSecondaryItemsSettingsTests
{
    [Fact]
    public void SelectionLimitPersistsImmediatelyAndUnrelatedDockChangesKeepBindings()
    {
        WithWindow((window, data) =>
        {
            var memory = Check(window, "StatusBarMemoryCheckBox");
            var mouse = Check(window, "StatusBarMouseBatteryCheckBox");
            Assert.False(mouse.IsEnabled);
            memory.IsChecked = false;
            Assert.True(mouse.IsEnabled);
            mouse.IsChecked = true;
            Assert.False(Check(window, "StatusBarFanCheckBox").IsEnabled);
            Assert.Equal("3/3", Assert.IsType<TextBlock>(window.FindName("StatusBarSecondaryCount")).Text);
            data.Settings.Current.Dock.StatusBarHeadsetDeviceKey = "offline:headset";
            Check(window, "ShowThisPcEntryCheckBox").IsChecked = false;
            var reloaded = new SettingsStore(data.Settings.FilePath);
            reloaded.Load();
            Assert.Equal(new[] { "cpu", "mouseBattery", "music" },
                StatusBarSecondaryItemCatalog.GetRenderItems(reloaded.Current.Dock.StatusBarSecondaryItems));
            Assert.Equal("offline:headset", reloaded.Current.Dock.StatusBarHeadsetDeviceKey);
            memory.IsChecked = true; // A programmatic attempt cannot bypass the disabled checkbox.
            Assert.False(memory.IsChecked);
            Assert.Equal(3, data.Settings.Current.Dock.StatusBarSecondaryItems.Length);
            Assert.DoesNotContain("memory", DockSettingsHost.Current!.Settings.StatusBarSecondaryItems);
            foreach (var name in new[] { "StatusBarCpuCheckBox", "StatusBarMouseBatteryCheckBox", "StatusBarMusicCheckBox" })
                Check(window, name).IsChecked = false;
            reloaded.Load();
            Assert.Empty(reloaded.Current.Dock.StatusBarSecondaryItems);
            Assert.Equal("0/3", Assert.IsType<TextBlock>(window.FindName("StatusBarSecondaryCount")).Text);
        });
    }

    [Fact]
    public void SaveFailureRestoresSelectionCheckboxesAndCount()
    {
        WithWindow((window, data) =>
        {
            using var held = new FileStream(data.Settings.FilePath + AtomicFile.TempSuffix,
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Check(window, "StatusBarMemoryCheckBox").IsChecked = false;
            Assert.True(Check(window, "StatusBarMemoryCheckBox").IsChecked);
            Assert.Equal(new[] { "cpu", "memory", "music" }, data.Settings.Current.Dock.StatusBarSecondaryItems);
            Assert.Equal(new[] { "cpu", "memory", "music" }, DockSettingsHost.Current!.Settings.StatusBarSecondaryItems);
            Assert.Equal("3/3", Assert.IsType<TextBlock>(window.FindName("StatusBarSecondaryCount")).Text);
            Assert.Contains("保存失败", Assert.IsType<TextBlock>(window.FindName("InformationBarSettingsStatus")).Text);
        });
    }

    [Fact]
    public void OfflineManualBindingRemainsVisibleAndCanReturnToAutomatic()
    {
        WithWindow((window, data) =>
        {
            data.Settings.Current.Dock.StatusBarMouseDeviceKey = "offline:receiver:2";
            window.AttachDataStore(data, null);
            var combo = Assert.IsType<ComboBox>(window.FindName("StatusBarMouseDeviceCombo"));
            Assert.Equal(2, combo.Items.Count);
            Assert.Equal("offline:receiver:2", combo.SelectedValue);
            Assert.Contains("离线", combo.Text);
            combo.SelectedIndex = 0;
            var reloaded = new SettingsStore(data.Settings.FilePath);
            reloaded.Load();
            Assert.Null(reloaded.Current.Dock.StatusBarMouseDeviceKey);
        });
    }

    [Fact]
    public void DiscoveryDistinguishesReceiverSlotsAndKeepsSelectionWhenDeviceDisappears()
    {
        WithWindow((window, data) =>
        {
            var now = DateTimeOffset.UtcNow;
            var devices = new[]
            {
                new PeripheralBatterySnapshot("receiver:slot:1", "Same mouse", PeripheralBatteryCategory.Mouse,
                    true, 90, false, now, "HID++", "supported", "", 1),
                new PeripheralBatterySnapshot("receiver:slot:2", "Same mouse", PeripheralBatteryCategory.Mouse,
                    true, 20, false, now, "HID++", "supported", "", 2),
                new PeripheralBatterySnapshot("keyboard:1", "Same mouse", PeripheralBatteryCategory.Keyboard,
                    true, 50, false, now, "Windows", "supported", ""),
            };
            Populate(window, devices);
            var mouse = Assert.IsType<ComboBox>(window.FindName("StatusBarMouseDeviceCombo"));
            Assert.Equal(3, mouse.Items.Count); // Automatic plus both physical receiver slots.
            mouse.SelectedValue = "receiver:slot:2";
            Assert.Equal("receiver:slot:2", data.Settings.Current.Dock.StatusBarMouseDeviceKey);
            Assert.DoesNotContain("receiver:slot", mouse.Text);
            Assert.Contains("槽位 2", mouse.Text);
            Populate(window, devices.Take(1).ToArray());
            Assert.Equal("receiver:slot:2", mouse.SelectedValue);
            Assert.Contains("离线", mouse.Text);
            using var held = new FileStream(data.Settings.FilePath + AtomicFile.TempSuffix,
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            mouse.SelectedIndex = 0;
            Assert.Equal("receiver:slot:2", mouse.SelectedValue);
            Assert.Equal("receiver:slot:2", data.Settings.Current.Dock.StatusBarMouseDeviceKey);
        });
    }

    private static void Populate(SettingsWindow window, IReadOnlyList<PeripheralBatterySnapshot> devices)
        => typeof(SettingsWindow).GetMethod("PopulateStatusBarBatteryDevices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [devices]);

    private static CheckBox Check(SettingsWindow window, string name) => Assert.IsType<CheckBox>(window.FindName(name));

    private static void WithWindow(Action<SettingsWindow, GlassDeskDataStore> body) => StaTest.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk.SecondarySettings", Guid.NewGuid().ToString("N"));
        var data = new GlassDeskDataStore(new AppPaths(root));
        data.LoadAll();
        var window = new SettingsWindow();
        var previousHost = DockSettingsHost.Current;
        var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
        host.Update(data.Settings.Current.Dock);
        try { window.AttachDataStore(data, null); body(window, data); }
        finally
        {
            window.Close();
            typeof(DockSettingsHost).GetProperty(nameof(DockSettingsHost.Current))!.GetSetMethod(true)!.Invoke(null, [previousHost]);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });
}
