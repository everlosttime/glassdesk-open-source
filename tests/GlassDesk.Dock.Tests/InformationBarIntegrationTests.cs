using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class InformationBarIntegrationTests
{
    [Fact]
    public void HorizontalOffsetSliderPersistsAndAppliesWithoutChangingDockOffset()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-information-offset-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Settings.Current.Dock.StatusBarOffsetX = -350;
            data.Settings.Current.Dock.OffsetX = 25;
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(data.Settings.Current.Dock);
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                var slider = Assert.IsType<Slider>(window.FindName("StatusBarOffsetXSlider"));
                Assert.Equal(-350d, slider.Value);
                slider.Value = -200;
                var saved = new SettingsStore(data.Settings.FilePath);
                saved.Load();
                Assert.Equal(-200d, saved.Current.Dock.StatusBarOffsetX);
                Assert.Equal(25d, saved.Current.Dock.OffsetX);
                Assert.Equal(-200d, host.Settings.StatusBarOffsetX);
                Assert.Contains("已保存", Assert.IsType<TextBlock>(window.FindName("InformationBarSettingsStatus")).Text);
            }
            finally { window.Close(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void InformationBarSaveFailureRestoresSelectionAndReportsInItsOwnCategory()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-information-save-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(data.Settings.Current.Dock);
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                using var blocker = new FileStream(data.Paths.SettingsFile + AtomicFile.TempSuffix,
                    FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                var combo = Assert.IsType<ComboBox>(window.FindName("StatusBarEdgeCombo"));
                combo.SelectedIndex = 1;
                Assert.Equal(0, combo.SelectedIndex);
                Assert.Equal(StatusBarEdge.Top, host.Settings.StatusBarEdge);
                Assert.Contains("保存失败", Assert.IsType<TextBlock>(window.FindName("InformationBarSettingsStatus")).Text);
            }
            finally { window.Close(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void PlacementControlsApplyImmediatelyAndPersistWithoutChangingDockSize()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-information-settings-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Settings.Current.Dock.StatusBarOffsetX = -120;
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(data.Settings.Current.Dock);
            var bar = new StatusBarWindow();
            var settings = new SettingsWindow();
            try
            {
                bar.BindPrimaryDisplay(new PrimaryDisplaySession(new("test", true,
                    new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96)));
                bar.ConfigurePlacement(() => (data.Settings.Current.Dock.StatusBarOffsetX,
                    data.Settings.Current.Dock.StatusBarEdge));
                bar.ConfigureSizeScale(() => data.Settings.Current.Dock.StatusBarSizeScale);
                bar.AttachSettings(host);
                bar.RefreshDisplayPlacement();
                settings.AttachDataStore(data, null);

                Assert.IsType<ComboBox>(settings.FindName("StatusBarEdgeCombo")).SelectedIndex = 1;
                Assert.IsType<Slider>(settings.FindName("StatusBarScaleSlider")).Value = 1.25;

                Assert.Equal(475d, bar.Width);
                Assert.Equal(68d, bar.Height);
                var rect = (PixelRect)typeof(StatusBarWindow).GetField("_restingRect",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
                Assert.Equal(new PixelRect(1307, 954, 475, 68), rect);
                var reload = new SettingsStore(data.Settings.FilePath);
                reload.Load();
                Assert.Equal(StatusBarEdge.Bottom, reload.Current.Dock.StatusBarEdge);
                Assert.Equal(1.25, reload.Current.Dock.StatusBarSizeScale);
                Assert.Equal(-120, reload.Current.Dock.StatusBarOffsetX);
                Assert.Equal(1d, reload.Current.Dock.SizeScale);
            }
            finally { bar.Close(); settings.Close(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void InformationCategoryContainsTheSwitchPlacementAndSizeControls()
    {
        StaTest.Run(() =>
        {
            var settings = new SettingsWindow();
            try
            {
                settings.Show();
                settings.NavigateToPage("信息栏");
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                settings.UpdateLayout();
                var heading = Assert.IsType<TextBlock>(settings.FindName("InformationBarSectionHeading"));
                var next = Assert.IsType<TextBlock>(settings.FindName("LauncherSectionHeading"));
                foreach (var name in new[] { "ShowStatusBarCheckBox", "StatusBarEdgeCombo", "StatusBarScaleSlider" })
                {
                    var control = Assert.IsAssignableFrom<FrameworkElement>(settings.FindName(name));
                    Assert.True(control.TranslatePoint(new Point(), heading).Y > 0, name + " precedes its category");
                    Assert.True(control.TranslatePoint(new Point(), next).Y < 0, name + " follows the next category");
                }
            }
            finally { settings.Close(); }
        });
    }

    [Fact]
    public void ControlCenterRoutesSettingsAndTogglesManualSleepInBothDirections()
    {
        StaTest.Run(() =>
        {
            var bar = new StatusBarWindow();
            try
            {
                var settingsCalls = 0;
                var sleeping = false;
                bar.ConfigureSettings(() => settingsCalls++);
                bar.ConfigureGameSleep(() => sleeping, value => sleeping = value);
                var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
                Assert.IsType<Button>(center.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, settingsCalls);
                var sleep = Assert.IsType<Button>(center.FindName("GameSleepButton"));
                sleep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(sleeping);
                sleep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(sleeping);
            }
            finally { bar.Close(); }
        });
    }
}
