using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class PerformanceSettingsTests
{
    [Theory]
    [InlineData("{\"schemaVersion\":1,\"appearance\":{\"accentColor\":\"#123456\"}}")]
    [InlineData("{\"schemaVersion\":1,\"performance\":null,\"appearance\":{\"accentColor\":\"#123456\"}}")]
    public void LegacyAndNullPerformanceSectionsDefaultToAutomaticSleepWithoutChangingSchema(string json)
    {
        using var temp = new TempDataDirectory();
        File.WriteAllText(temp.Paths.SettingsFile, json);
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.True(Read(store.Current));
        Assert.Equal(1, store.Current.SchemaVersion);
        Assert.Equal("#123456", store.Current.Appearance.AccentColor);
    }

    [Fact]
    public void DisableSurvivesSaveCloneAndUnrelatedResetAndPerformanceResetRestoresOnlyItsOwnPage()
    {
        using var temp = new TempDataDirectory();
        File.WriteAllText(temp.Paths.SettingsFile,
            "{\"schemaVersion\":1,\"performance\":{\"autoSuspendWhenFullscreen\":false},\"dock\":{\"offsetX\":25}}");
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        Assert.False(Read(store.Current));
        store.Save();
        var reloaded = new SettingsStore(temp.Paths.SettingsFile);
        reloaded.Load();
        Assert.False(Read(reloaded.Current));
        var clone = SettingsPageEditor.Clone(reloaded.Current);
        Assert.False(Read(clone));
        var appearanceReset = SettingsPageEditor.ResetPage(clone, "外观");
        Assert.False(Read(appearanceReset));
        var performanceReset = SettingsPageEditor.ResetPage(clone, "性能");
        Assert.True(Read(performanceReset));
        Assert.Equal(25, performanceReset.Dock.OffsetX);
        SettingsPageEditor.Apply(clone, performanceReset);
        Assert.True(Read(clone));
    }

    private static bool Read(GlassDeskSettings settings)
    {
        var property = typeof(GlassDeskSettings).GetProperty("Performance");
        Assert.NotNull(property);
        var section = property.GetValue(settings);
        Assert.NotNull(section);
        return (bool)section.GetType().GetProperty("AutoSuspendWhenFullscreen")!.GetValue(section)!;
    }
}
