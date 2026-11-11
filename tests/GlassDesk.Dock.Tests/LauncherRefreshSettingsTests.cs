using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherRefreshSettingsTests
{
    [Fact]
    public void SettingsTogglePersistsAcrossOtherEditsAndChangesTheNextAnimation()
    {
        StaTest.Run(() =>
        {
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ");
            Environment.SetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ", null);
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-refresh-" + Guid.NewGuid().ToString("N"))));
            data.LoadAll();
            var settings = new SettingsWindow();
            var launcher = new LauncherWindow();
            using var feed = new SharedBackdropFeed((_, cancellation) => cancellation.WaitHandle.WaitOne());
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var surface = (LiquidGlassSurface)launcher.FindName("GlassSurface");
            var background = typeof(LiquidGlassSurface).GetField("_background", flags)!.GetValue(surface)!;
            var field = background.GetType().GetField("_compatible", flags)!;
            ((IDisposable)field.GetValue(background)!).Dispose(); field.SetValue(background, feed);
            try
            {
                settings.AttachDataStore(data, null);
                launcher.ApplySettings(data.Settings.Current.Launcher);
                settings.LauncherSettingsChanged += (_, value) => launcher.ApplySettings(value);
                var toggle = Assert.IsType<CheckBox>(settings.FindName("LauncherHighRefreshCheck"));
                Assert.True(toggle.IsChecked);
                toggle.IsChecked = false;
                toggle.IsChecked = true;
                Assert.IsType<Slider>(settings.FindName("LauncherRowsSlider")).Value = 6;
                var reload = new SettingsStore(data.Settings.FilePath); reload.Load();
                Assert.Equal(true, reload.Current.Launcher.GetType().GetProperty("HighRefreshAnimationsEnabled")!.GetValue(reload.Current.Launcher));
                var display = DisplayService.Primary()!;
                var source = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
                launcher.ShowWithTransition(source);
                Assert.InRange(Interval(), 8, 8.4);
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", flags)!.Invoke(launcher, [1d]);
                Assert.Equal(16, Interval());
                launcher.Hide();
                toggle.IsChecked = false;
                launcher.ShowWithTransition(source);
                Assert.Equal(16, Interval());
                reload.Load();
                Assert.Equal(false, reload.Current.Launcher.GetType().GetProperty("HighRefreshAnimationsEnabled")!.GetValue(reload.Current.Launcher));
            }
            finally
            {
                launcher.Close(); settings.Close();
                Environment.SetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ", previous);
            }
            double Interval() => JsonSerializer.SerializeToElement(surface.InspectPerformance()).GetProperty("capture").GetProperty("live").GetProperty("captureIntervalMilliseconds").GetDouble();
        });
    }
}
