using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Tests;

public sealed class NaiwaOptionsTests
{
    [Fact]
    public void MigratesOriginalPreferencesAndConstrainsOutOfRangeValues()
    {
        var options = NaiwaOptions.FromLegacyIni(["scale=150", "volume=-2", "sound=False", "recycle=False", "fullscreen=False", "hunger=7"]);

        Assert.Equal(150, options.Scale);
        Assert.Equal(0, options.Volume);
        Assert.False(options.Sound);
        Assert.False(options.Recycle);
        Assert.False(options.AutoHide);
        Assert.Equal("options 150 0 0 0 0", options.ToPipeCommand());
    }

    [Fact]
    public void RecyclePageOwnsEnablementAndPreferences()
    {
        var settings = new GlassDeskSettings { NaiwaOptions = new() { Volume = 25 } };
        settings.Appearance.NaiwaEnabled = true;

        var appearance = SettingsPageEditor.ResetPage(settings, "外观");
        Assert.True(appearance.Appearance.NaiwaEnabled);
        Assert.Equal(25, appearance.NaiwaOptions!.Volume);

        var recycle = SettingsPageEditor.ResetPage(settings, "回收站");
        Assert.False(recycle.Appearance.NaiwaEnabled);
        Assert.Equal(100, recycle.NaiwaOptions!.Volume);
    }
}
