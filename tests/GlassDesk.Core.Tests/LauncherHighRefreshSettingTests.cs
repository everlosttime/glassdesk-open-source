using System.Text.Json;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LauncherHighRefreshSettingTests
{
    [Fact]
    public void HighRefreshDefaultsOnAndExplicitOptOutSurvivesSaveCloneAndUnrelatedPageReset()
    {
        var property = typeof(LauncherSettings).GetProperty("HighRefreshAnimationsEnabled");
        Assert.NotNull(property);
        var old = JsonSerializer.Deserialize("{}", GlassDeskJsonContext.Default.GlassDeskSettings)!;
        Assert.Equal(true, property.GetValue(old.Launcher));
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        property.SetValue(store.Current.Launcher, false);
        store.Save();
        store.Load();
        Assert.Equal(false, property.GetValue(store.Current.Launcher));
        Assert.Equal(false, property.GetValue(SettingsPageEditor.Clone(store.Current).Launcher));
        Assert.Equal(false, property.GetValue(SettingsPageEditor.ResetPage(store.Current, "Dock").Launcher));
        Assert.Equal(true, property.GetValue(SettingsPageEditor.ResetPage(store.Current, "启动台").Launcher));
    }
}
