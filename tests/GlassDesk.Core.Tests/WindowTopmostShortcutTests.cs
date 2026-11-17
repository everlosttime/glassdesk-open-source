using System.Text.Json;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Tests;

public sealed class WindowTopmostShortcutTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"shortcuts\":{\"toggleLauncher\":{\"enabled\":true,\"key\":\"F4\"}}}")]
    [InlineData("{\"shortcuts\":{\"selectWindowTopmost\":null}}")]
    public void NewAndLegacyProfilesOfferControlAltTForSelectingAWindow(string json)
    {
        var settings = Read(json);
        var binding = Binding(settings);
        Assert.True(HotkeyText.TryGetChord(binding, out var chord, out _));
        Assert.Equal(new HotkeyChord(3, 84), chord);
    }

    [Theory]
    [InlineData("{\"enabled\":false}", "")]
    [InlineData("{\"enabled\":true,\"key\":\"F9\"}", "F9")]
    public void SavedWindowSelectionBindingSurvivesNormalizationAndRoundTrip(string bindingJson, string expected)
    {
        var settings = Read("{\"shortcuts\":{\"selectWindowTopmost\":" + bindingJson + "}}");
        var roundTrip = SettingsPageEditor.Clone(settings);
        roundTrip.Normalize();
        Assert.Equal(expected, HotkeyText.Format(Binding(roundTrip)));
    }

    [Fact]
    public void ResettingShortcutsRestoresWindowSelectionWithoutChangingTheOriginal()
    {
        var settings = Read("{\"shortcuts\":{\"selectWindowTopmost\":{\"enabled\":true,\"key\":\"F9\"}}}");
        var reset = SettingsPageEditor.ResetPage(settings, "快捷操作");
        Assert.Equal("Ctrl+Alt+T", HotkeyText.Format(Binding(reset)));
        Assert.Equal("F9", HotkeyText.Format(Binding(settings)));
    }

    [Fact]
    public void WindowSelectionParticipatesInDuplicateShortcutValidation()
    {
        var settings = Read("{\"shortcuts\":{\"toggleLauncher\":{\"enabled\":true,\"control\":true,\"alt\":true,\"key\":\"T\"}}}");
        Assert.NotNull(SettingsPageEditor.Validate(settings));
    }

    private static GlassDeskSettings Read(string json)
    {
        var settings = JsonSerializer.Deserialize(json, GlassDeskJsonContext.Default.GlassDeskSettings)!;
        settings.Normalize();
        return settings;
    }

    private static HotkeyDefinition Binding(GlassDeskSettings settings) =>
        Assert.Single(HotkeyText.Bindings(settings.Shortcuts), item => item.Action.ToString() == "SelectWindowTopmost").Definition;
}
