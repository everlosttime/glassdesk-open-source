using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class GlassMaterialSettingsTests
{
    [Fact]
    public void DockAndLauncherTransparencyHaveIndependentDefaultsPersistenceAndReset()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.Equal(.9, store.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.2, store.Current.Appearance.LauncherGlassTransparency);
        store.Current.Appearance.DockGlassTransparency = .65;
        store.Save();
        store.Load();
        Assert.Equal(.65, store.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.2, store.Current.Appearance.LauncherGlassTransparency);
        store.Current.Appearance.LauncherGlassTransparency = .35;
        store.Save();
        store.Load();
        Assert.Equal(.65, store.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.35, store.Current.Appearance.LauncherGlassTransparency);
        var reset = SettingsPageEditor.ResetPage(store.Current, "外观");
        Assert.Equal(.9, reset.Appearance.DockGlassTransparency);
        Assert.Equal(.2, reset.Appearance.LauncherGlassTransparency);
    }
    [Fact]
    public void BackgroundClarityDefaultsToOriginalAppearanceAndPersistsIndependentlyOfTint()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.Equal(.5, store.Current.Appearance.GlassBackgroundClarity);
        store.Current.Appearance.GlassBackgroundClarity = 1;
        store.Save();
        store.Load();
        Assert.Equal(1, store.Current.Appearance.GlassBackgroundClarity);
        Assert.Equal(.16, store.Current.Appearance.GlassTintOpacity);
        Assert.Equal(.5, SettingsPageEditor.ResetPage(store.Current, "外观").Appearance.GlassBackgroundClarity);
        store.Current.Appearance.GlassBackgroundClarity = double.NaN;
        Assert.NotNull(SettingsPageEditor.Validate(store.Current));
        store.Current.Normalize();
        Assert.Equal(.5, store.Current.Appearance.GlassBackgroundClarity);
    }
    [Fact]
    public void BodyTintIsBackwardCompatiblePersistedAndResetWithAppearance()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.Equal(.16, store.Current.Appearance.GlassTintOpacity);
        store.Current.Appearance.GlassTintOpacity = 0;
        store.Save();
        var reload = new SettingsStore(temp.Paths.SettingsFile);
        reload.Load();
        Assert.Equal(0, reload.Current.Appearance.GlassTintOpacity);
        Assert.Equal(.16, SettingsPageEditor.ResetPage(reload.Current, "外观").Appearance.GlassTintOpacity);
        reload.Current.Appearance.GlassTintOpacity = double.NaN;
        Assert.NotNull(SettingsPageEditor.Validate(reload.Current));
        reload.Current.Normalize();
        Assert.Equal(.16, reload.Current.Appearance.GlassTintOpacity);
        reload.Current.Appearance.GlassTintOpacity = 2;
        reload.Current.Normalize();
        Assert.Equal(1, reload.Current.Appearance.GlassTintOpacity);
    }
    [Fact]
    public void MissingMaterialInOldSettingsUsesNewEffect()
    {
        using var temp = new TempDataDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Paths.SettingsFile)!);
        File.WriteAllText(temp.Paths.SettingsFile, "{\"schemaVersion\":1,\"appearance\":{\"theme\":\"Dark\"}}");
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, store.Current.Appearance.GlassMaterial);
    }

    [Fact]
    public void LegacySelectionUsesLiquidGlassAfterSaveReloadAndInvalidModeNormalizes()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.GlassMaterial = GlassMaterialMode.PhysicalGlass;
        store.Save();
        var reloaded = new SettingsStore(temp.Paths.SettingsFile);
        reloaded.Load();
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, reloaded.Current.Appearance.GlassMaterial);
        reloaded.Current.Appearance.GlassMaterial = (GlassMaterialMode)999;
        Assert.NotNull(SettingsPageEditor.Validate(reloaded.Current));
        reloaded.Current.Normalize();
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, reloaded.Current.Appearance.GlassMaterial);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("System")]
    public void LegacyAppearanceLoadsAsDarkLiquidGlassWhilePreservingOtherPreferences(string theme)
    {
        using var temp = new TempDataDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Paths.SettingsFile)!);
        File.WriteAllText(temp.Paths.SettingsFile,
            $$$"""{"schemaVersion":1,"appearance":{"theme":"{{{theme}}}","glassMaterial":"PhysicalGlass","glassBackgroundClarity":0.8,"reduceMotion":true,"reduceTransparency":true}}""");
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, store.Current.Appearance.GlassMaterial);
        Assert.Equal(.8, store.Current.Appearance.GlassBackgroundClarity);
        Assert.True(store.Current.Appearance.ReduceMotion);
        Assert.False(store.Current.Appearance.ReduceTransparency);
        store.Save();
        store.Load();
        Assert.Equal(ThemeMode.Dark, store.Current.Appearance.Theme);
    }
}
