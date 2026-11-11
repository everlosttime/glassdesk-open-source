using System.Text.Json;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Tests;

public sealed class GameSleepShortcutTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"shortcuts\":{\"toggleLauncher\":{\"enabled\":true,\"key\":\"F4\"}}}")]
    public void NewAndLegacySettingsRegisterControlAltGForGameSleep(string json)
    {
        var settings = JsonSerializer.Deserialize(json, GlassDeskJsonContext.Default.GlassDeskSettings)!;
        settings.Normalize();
        var binding = Assert.Single(HotkeyText.Bindings(settings.Shortcuts), item => item.Action.ToString() == "ToggleGameSleep");
        Assert.True(HotkeyText.TryGetChord(binding.Definition, out var chord, out _));
        Assert.Equal(new HotkeyChord(3, 71), chord);
    }

    [Theory]
    [InlineData("{\"enabled\":false}", "")]
    [InlineData("{\"enabled\":true,\"key\":\"F8\"}", "F8")]
    public void ExplicitGameSleepBindingSurvivesSaveAndNormalization(string bindingJson, string expected)
    {
        var settings = JsonSerializer.Deserialize("{\"shortcuts\":{\"toggleGameSleep\":" + bindingJson + "}}",
            GlassDeskJsonContext.Default.GlassDeskSettings)!;
        settings.Normalize();
        var roundTrip = SettingsPageEditor.Clone(settings);
        roundTrip.Normalize();
        var binding = Assert.Single(HotkeyText.Bindings(roundTrip.Shortcuts), item => item.Action.ToString() == "ToggleGameSleep");
        Assert.Equal(expected, HotkeyText.Format(binding.Definition));
    }
}
