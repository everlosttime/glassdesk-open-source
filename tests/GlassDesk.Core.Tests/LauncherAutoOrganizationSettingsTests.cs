using System.Text.Json;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LauncherAutoOrganizationSettingsTests
{
    [Fact]
    public void OldSettingsKeepDesktopOptInAndSmartGroupingDefaultWhileSavedChoicesSurviveCloning()
    {
        var auto = typeof(LauncherSettings).GetProperty("AutoAddDesktopAppsEnabled");
        var smart = typeof(LauncherSettings).GetProperty("SmartGroupingEnabled");
        Assert.NotNull(auto); Assert.NotNull(smart);
        var old = JsonSerializer.Deserialize("{}", GlassDeskJsonContext.Default.GlassDeskSettings)!;
        Assert.Equal(false, auto.GetValue(old.Launcher));
        Assert.Equal(true, smart.GetValue(old.Launcher));
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile); store.Load();
        auto.SetValue(store.Current.Launcher, true); smart.SetValue(store.Current.Launcher, false);
        store.Save(); store.Load();
        var clone = SettingsPageEditor.Clone(store.Current);
        Assert.Equal(true, auto.GetValue(clone.Launcher));
        Assert.Equal(false, smart.GetValue(clone.Launcher));
        var resetOther = SettingsPageEditor.ResetPage(store.Current, "Dock");
        Assert.Equal(true, auto.GetValue(resetOther.Launcher));
        Assert.Equal(false, smart.GetValue(resetOther.Launcher));
        var resetLauncher = SettingsPageEditor.ResetPage(store.Current, "启动台");
        Assert.Equal(false, auto.GetValue(resetLauncher.Launcher));
        Assert.Equal(true, smart.GetValue(resetLauncher.Launcher));
    }
}
