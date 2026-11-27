using System.Text.Json;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Tests;

public sealed class StageWindowShortcutTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"shortcuts\":{\"selectStageWindow\":null}}")]
    public void ExistingProfilesGetAnIndependentStageSelectionShortcut(string json)
    {
        var settings = JsonSerializer.Deserialize(json, GlassDeskJsonContext.Default.GlassDeskSettings)!;
        settings.Normalize();

        Assert.Equal("Ctrl+Alt+J", settings.Shortcuts.SelectStageWindow.Describe());
        Assert.Equal("Ctrl+Alt+T", settings.Shortcuts.SelectWindowTopmost.Describe());
        Assert.Contains(HotkeyText.Bindings(settings.Shortcuts), binding =>
            binding.Action == InputAction.SelectStageWindow && binding.Definition == settings.Shortcuts.SelectStageWindow);
    }
}
