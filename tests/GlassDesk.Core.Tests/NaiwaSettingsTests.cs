using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class NaiwaSettingsTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"appearance":{"naiwaEnabled":true}}""", true)]
    public void EnablementDefaultsOffAndSurvivesSave(string json, bool enabled)
    {
        using var temp = new TempDataDirectory();
        File.WriteAllText(temp.Paths.SettingsFile, json);
        var settings = new SettingsStore(temp.Paths.SettingsFile);
        settings.Load();
        Assert.Equal(enabled, settings.Current.Appearance.NaiwaEnabled);
        settings.Save();
        var reloaded = new SettingsStore(temp.Paths.SettingsFile);
        reloaded.Load();
        Assert.Equal(enabled, reloaded.Current.Appearance.NaiwaEnabled);
        Assert.False(SettingsPageEditor.ResetPage(reloaded.Current, "回收站").Appearance.NaiwaEnabled);
    }
}
