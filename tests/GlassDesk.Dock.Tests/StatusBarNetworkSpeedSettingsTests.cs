using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class StatusBarNetworkSpeedSettingsTests
{
    [Fact]
    public void SettingsCheckboxPersistsTheChoiceAndOtherDockEditsPreserveIt()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk.NetworkSpeedSettings", Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                var check = Assert.IsType<CheckBox>(window.FindName("ShowStatusBarNetworkSpeedCheckBox"));
                Assert.False(check.IsChecked);
                check.IsChecked = true;
                Assert.IsType<CheckBox>(window.FindName("ShowThisPcEntryCheckBox")).IsChecked = false;
                var reload = new SettingsStore(data.Settings.FilePath);
                reload.Load();
                Assert.True(ReadChoice(reload.Current.Dock));
                Assert.True(ReadChoice(SettingsPageEditor.Clone(reload.Current).Dock));
                check.IsChecked = false;
                reload.Load();
                Assert.False(ReadChoice(reload.Current.Dock));
            }
            finally
            {
                window.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void HidingSpeedReclaimsItsSpaceWithoutResizingTheBar(double scale)
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            var settings = new DockSettings();
            SetChoice(settings, true);
            host.Update(settings);
            window.ConfigureSizeScale(() => scale);
            window.AttachSettings(host);
            try
            {
                window.Show();
                window.UpdateLayout();
                var speed = Assert.IsType<StackPanel>(window.FindName("NetworkSpeedPanel"));
                var volume = Assert.IsType<TextBlock>(window.FindName("VolumeText"));
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var before = volume.TransformToAncestor(root).Transform(new Point()).X;
                var originalWidth = window.Width;
                var originalHeight = window.Height;
                SetChoice(settings, false);
                host.Update(settings);
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, speed.Visibility);
                Assert.Equal(0, speed.DesiredSize.Width);
                var after = volume.TransformToAncestor(root).Transform(new Point()).X;
                Assert.True(after < before - 10 * scale, "Volume must move into the former speed area.");
                Assert.InRange(window.Width, 380 * scale - 1, 380 * scale + 1);
                Assert.InRange(window.Height, 54 * scale - 1, 54 * scale + 1);
                Assert.Equal(originalWidth, window.Width, 2);
                Assert.Equal(originalHeight, window.Height, 2);
                Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(window.FindName("NetworkText")).Visibility);
                SetChoice(settings, true);
                host.Update(settings);
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, speed.Visibility);
                Assert.Equal(before, volume.TransformToAncestor(root).Transform(new Point()).X, 2);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BatteryNetworkAndVolumeFitBeforeTheArrowWhenSpeedIsVisible(bool hasBattery)
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            try
            {
                typeof(StatusBarWindow).GetMethod("ApplySystemStatus", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [new GlassDesk.Platform.SystemStatusSnapshot(100, true, "Wi-Fi", 100, false) { HasBattery = hasBattery }]);
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var volume = Assert.IsType<TextBlock>(window.FindName("VolumeText"));
                var arrow = Assert.IsType<Button>(window.FindName("ControlCenterArrowButton"));
                var volumeBounds = volume.TransformToAncestor(root).TransformBounds(new Rect(volume.RenderSize));
                var arrowBounds = arrow.TransformToAncestor(root).TransformBounds(new Rect(arrow.RenderSize));
                Assert.True(volumeBounds.Right <= arrowBounds.Left, "Status text must not overlap the control-center button.");
                Assert.InRange(window.Width, 379, 381);
                Assert.InRange(window.Height, 53, 55);
            }
            finally { window.Close(); }
        });
    }

    private static bool ReadChoice(DockSettings settings)
    {
        var property = typeof(DockSettings).GetProperty("ShowStatusBarNetworkSpeed");
        Assert.NotNull(property);
        return Assert.IsType<bool>(property.GetValue(settings));
    }

    private static void SetChoice(DockSettings settings, bool value)
    {
        var property = typeof(DockSettings).GetProperty("ShowStatusBarNetworkSpeed");
        Assert.NotNull(property);
        property.SetValue(settings, value);
    }
}
