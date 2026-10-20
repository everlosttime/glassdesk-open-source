using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class AppearanceCustomizationTests
{
    [Fact]
    public void FrostedChoiceSurvivesSavingAndReloadingWithoutChangingTransparency()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.GlassMaterial = (GlassMaterialMode)2;
        store.Current.Appearance.DockGlassTransparency = .75;
        store.Save();
        store.Load();
        Assert.Equal((GlassMaterialMode)2, store.Current.Appearance.GlassMaterial);
        Assert.Equal(.75, store.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.2, store.Current.Appearance.LauncherGlassTransparency);
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass,
            SettingsPageEditor.ResetPage(store.Current, "外观").Appearance.GlassMaterial);
    }
}
