using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DockBuiltinEntrySettingsTests
{
    [Fact]
    public void AllEntryCombinationsApplyImmediatelyWithoutGapsOrChangingPinnedOrder()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-builtin-" + Guid.NewGuid().ToString("N"));
            var window = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window.AttachServices(services, host, () => { });
                window.Show();
                var names = new[] { "LauncherButton", "ThisPcButton", "DesktopIconsButton" };
                var fields = new[] { "ShowLauncherEntry", "ShowThisPcEntry", "ShowDesktopIconsEntry" };
                var buttons = names.Select(n => Assert.IsType<Button>(window.FindName(n))).ToArray();
                var row = Assert.IsType<StackPanel>(buttons[0].Parent);
                Assert.Equal(buttons, row.Children.OfType<Button>().ToArray());
                for (var mask = 0; mask < 8; mask++)
                {
                    var settings = new DockSettings();
                    for (var i = 0; i < 3; i++) SetEntry(settings, fields[i], (mask & (1 << i)) != 0);
                    host.Update(settings);
                    window.UpdateLayout();
                    double x = 0;
                    for (var i = 0; i < 3; i++)
                    {
                        var visible = (mask & (1 << i)) != 0;
                        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, buttons[i].Visibility);
                        if (!visible) continue;
                        Assert.Equal(x, buttons[i].TranslatePoint(new Point(), row).X, 5);
                        x += 56;
                    }
                    Assert.Empty(data.Layouts.Dock.PinnedItemIds);
                    Assert.Equal(0, window.TileCount);
                    if ((mask & 1) == 0) Assert.Null(window.GetLauncherButtonPixelBounds());
                }
            }
            finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void SettingsSwitchesPersistAndSurviveOtherDockChangesAndReload()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-builtin-settings-" + Guid.NewGuid().ToString("N"));
            var window = new SettingsWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window.AttachDataStore(data, null);
                var fields = new[] { "ShowLauncherEntry", "ShowThisPcEntry", "ShowDesktopIconsEntry" };
                foreach (var field in fields)
                {
                    var box = Assert.IsType<CheckBox>(window.FindName(field + "CheckBox"));
                    Assert.True(box.IsChecked);
                    box.IsChecked = false;
                }
                Assert.IsType<Slider>(window.FindName("DockOffsetXSlider")).Value = 25;
                var reloaded = new SettingsStore(data.Paths.SettingsFile);
                reloaded.Load();
                foreach (var field in fields)
                {
                    Assert.False(GetEntry(reloaded.Current.Dock, field));
                    Assert.False(GetEntry(host.Settings, field));
                }
                Assert.Equal(25, reloaded.Current.Dock.OffsetX);
                var loaded = new GlassDeskDataStore(data.Paths);
                loaded.LoadAll();
                window.AttachDataStore(loaded, null);
                foreach (var field in fields)
                    Assert.False(Assert.IsType<CheckBox>(window.FindName(field + "CheckBox")).IsChecked);
            }
            finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void ShortFrameSettingMatchesTheSpaceRequiredByEnabledEntries()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-builtin-frame-" + Guid.NewGuid().ToString("N"));
            var window = new SettingsWindow();
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                window.AttachDataStore(data, null);
                dock.Show();
                var frame = Assert.IsType<Slider>(window.FindName("DockFrameLengthSlider"));
                frame.Value = frame.Minimum + 8;
                frame.Value = frame.Minimum;
                dock.UpdateLayout();
                Assert.Equal(frame.Value, dock.ActualWidth, 5);
                Assert.IsType<CheckBox>(window.FindName("ShowDesktopIconsEntryCheckBox")).IsChecked = false;
                frame.Value = frame.Minimum;
                dock.UpdateLayout();
                Assert.Equal(frame.Value, dock.ActualWidth, 5);
            }
            finally { dock.Close(); window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void DesktopIconsEntryInvokesHostActionOncePerClick()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                var callback = typeof(DockWindow).GetProperty("ToggleDesktopIcons");
                Assert.NotNull(callback);
                var count = 0;
                callback.SetValue(window, (Action)(() => count++));
                var button = Assert.IsType<Button>(window.FindName("DesktopIconsButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, count);
            }
            finally { window.Close(); }
        });
    }

    private static void SetEntry(DockSettings settings, string name, bool value)
    {
        var property = typeof(DockSettings).GetProperty(name);
        Assert.NotNull(property);
        property.SetValue(settings, value);
    }

    private static bool GetEntry(DockSettings settings, string name) =>
        (bool)typeof(DockSettings).GetProperty(name)!.GetValue(settings)!;
}
