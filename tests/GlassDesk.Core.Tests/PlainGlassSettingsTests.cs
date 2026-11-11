using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class PlainGlassSettingsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.63)]
    [InlineData(1)]
    public void PlainGlassSurvivesSaveReloadWithoutChangingOtherAppearanceChoices(double transparency)
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.GlassMaterial = GlassMaterialMode.PlainGlass;
        store.Current.Appearance.DockGlassTransparency = transparency;
        store.Current.Appearance.LauncherGlassTransparency = .37;
        store.Current.Appearance.PinBarsToDesktop = false;
        store.Save();
        var reload = new SettingsStore(temp.Paths.SettingsFile);
        reload.Load();
        Assert.Equal(GlassMaterialMode.PlainGlass, reload.Current.Appearance.GlassMaterial);
        Assert.Equal(transparency, reload.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.37, reload.Current.Appearance.LauncherGlassTransparency);
        Assert.False(reload.Current.Appearance.PinBarsToDesktop);
    }
}
