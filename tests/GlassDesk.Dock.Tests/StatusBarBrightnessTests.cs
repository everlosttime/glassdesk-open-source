using System.IO;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class StatusBarBrightnessTests
{
    [Fact]
    public void SliderPersistsAndUpdatesAllInformationTextWithoutChangingOtherWindows()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-bar-brightness-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var settings = new SettingsWindow(); var bar = new StatusBarWindow();
            try
            {
                settings.AttachDataStore(data, null);
                _ = new WindowInteropHelper(settings).EnsureHandle();
                _ = new WindowInteropHelper(bar).EnsureHandle();
                var originalSettingsColor = Assert.IsType<SolidColorBrush>(settings.Foreground).Color;
                var slider = (Slider)settings.FindName("StatusBarTextBrightnessSlider");
                foreach (var (value, color) in new[] { (0d, Colors.Black), (.5, Color.FromRgb(128,128,128)), (1d, Colors.White) })
                {
                    slider.Value = value;
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                    foreach (var name in new[] { "TimeText", "DateText", "BatteryText", "NetworkText", "VolumeGlyph", "CpuText", "MemoryText", "MusicTitleText", "ControlCenterArrowGlyph" })
                        Assert.Equal(color, Assert.IsType<SolidColorBrush>(((TextBlock)bar.FindName(name)).Foreground).Color);
                    Assert.Equal(color, Assert.IsType<SolidColorBrush>(((Border)bar.FindName("BatteryFill")).Background).Color);
                    var reload = new SettingsStore(data.Settings.FilePath); reload.Load();
                    Assert.Equal(value, reload.Current.Appearance.StatusBarTextBrightness);
                    Assert.Equal(1, reload.Current.Appearance.DockBuiltinIconBrightness);
                    Assert.Equal(1, reload.Current.Appearance.LauncherTextBrightness);
                }
                Assert.Equal(originalSettingsColor, Assert.IsType<SolidColorBrush>(settings.Foreground).Color);
            }
            finally { settings.Close(); bar.Close(); Directory.Delete(root, true); }
        });
    }
}
