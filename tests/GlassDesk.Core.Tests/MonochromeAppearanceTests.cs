using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class MonochromeAppearanceTests
{
    [Fact]
    public void IndependentBrightnessValuesSurviveSaveCloneAndReloadAndResetToWhite()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile); store.Load();
        Set(store.Current.Appearance, "DockBuiltinIconBrightness", .25);
        Set(store.Current.Appearance, "LauncherTextBrightness", .75);
        store.Current.Appearance.DockGlassTransparency = .63;
        store.Save(); store.Load();
        var copy = SettingsPageEditor.Clone(store.Current);
        Assert.Equal(.25, Get(copy.Appearance, "DockBuiltinIconBrightness"));
        Assert.Equal(.75, Get(copy.Appearance, "LauncherTextBrightness"));
        Assert.Equal(.63, copy.Appearance.DockGlassTransparency);
        var reset = SettingsPageEditor.ResetPage(copy, "外观");
        Assert.Equal(1, Get(reset.Appearance, "DockBuiltinIconBrightness"));
        Assert.Equal(1, Get(reset.Appearance, "LauncherTextBrightness"));
    }

    [Fact]
    public void OldSettingsDefaultToWhiteWithoutChangingOtherValues()
    {
        using var temp = new TempDataDirectory();
        File.WriteAllText(temp.Paths.SettingsFile, """{"appearance":{"dockGlassTransparency":0.42,"launcherGlassTransparency":0.3}}""");
        var store = new SettingsStore(temp.Paths.SettingsFile); store.Load();
        Assert.Equal(1, Get(store.Current.Appearance, "DockBuiltinIconBrightness"));
        Assert.Equal(1, Get(store.Current.Appearance, "LauncherTextBrightness"));
        Assert.Equal(.42, store.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.3, store.Current.Appearance.LauncherGlassTransparency);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void InvalidBrightnessIsRejectedAndNormalizedToASafeValue(double input, double expected)
    {
        foreach (var name in new[] { "DockBuiltinIconBrightness", "LauncherTextBrightness" })
        {
            var settings = new GlassDeskSettings();
            Set(settings.Appearance, name, input);
            Assert.NotNull(SettingsPageEditor.Validate(settings));
            settings.Normalize();
            Assert.Equal(expected, Get(settings.Appearance, name));
            Assert.Null(SettingsPageEditor.Validate(settings));
        }
    }

    private static void Set(AppearanceSettings appearance, string name, double value)
    {
        var property = typeof(AppearanceSettings).GetProperty(name);
        Assert.NotNull(property); property.SetValue(appearance, value);
    }

    private static double Get(AppearanceSettings appearance, string name)
    {
        var property = typeof(AppearanceSettings).GetProperty(name);
        Assert.NotNull(property); return (double)property.GetValue(appearance)!;
    }
}
