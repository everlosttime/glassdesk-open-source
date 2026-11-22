using System.IO;
using System.Windows.Controls;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherSettingsViewTests
{
    [Fact]
    public void LauncherSettingsHasNoBackgroundCadenceToggleAndStillSavesRows()
    {
        StaTest.Run(() =>
        {
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-launcher-settings-" + Guid.NewGuid().ToString("N"))));
            data.LoadAll();
            var settings = new SettingsWindow();
            try
            {
                settings.AttachDataStore(data, null);
                Assert.Null(settings.FindName("LauncherHighRefreshCheck"));
                Assert.IsType<Slider>(settings.FindName("LauncherRowsSlider")).Value = 6;
                var reload = new SettingsStore(data.Settings.FilePath);
                reload.Load();
                Assert.Equal(6, reload.Current.Launcher.Rows);
            }
            finally { settings.Close(); }
        });
    }
}
