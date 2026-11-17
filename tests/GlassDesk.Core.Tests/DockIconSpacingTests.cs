using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class DockIconSpacingTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(30, 24)]
    [InlineData(double.NaN, 8)]
    [InlineData(double.PositiveInfinity, 8)]
    [InlineData(0, 0)]
    [InlineData(16, 16)]
    [InlineData(24, 24)]
    public void NormalizationKeepsSpacingUsableAndSavedChoicesSurviveReload(double input, double expected)
    {
        using var temp = new TempDataDirectory();
        var settings = new GlassDeskSettings { Dock = new DockSettings { IconSpacing = input } };
        settings.Normalize();
        Assert.Equal(expected, settings.Dock.IconSpacing);

        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Current.Dock = settings.Dock;
        store.Save();
        var reloaded = new SettingsStore(temp.Paths.SettingsFile);
        reloaded.Load();
        Assert.Equal(expected, reloaded.Current.Dock.IconSpacing);
    }
}
