using System.Text.Json;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LegacyLauncherSettingsTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void LegacyCaptureToggleCanBeDiscardedWithoutLosingLauncherSettings(string legacyValue)
    {
        using var temp = new TempDataDirectory();
        System.IO.File.WriteAllText(temp.Paths.SettingsFile,
            "{\"launcher\":{\"rows\":6,\"highRefreshAnimationsEnabled\":" + legacyValue + "}}");
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.Equal(6, store.Current.Launcher.Rows);
        store.Save();
        var saved = JsonSerializer.SerializeToElement(store.Current, GlassDeskJsonContext.Default.GlassDeskSettings);
        Assert.False(saved.GetProperty("launcher").TryGetProperty("highRefreshAnimationsEnabled", out _));
    }
}
