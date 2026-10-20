using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Tests;

public sealed class Phase6InputTests
{
    [Theory]
    [InlineData(" Ctrl+Alt+Space ", 3u, 32u)]
    [InlineData("Shift+F12", 4u, 123u)]
    [InlineData("Win+PageDown", 8u, 34u)]
    public void Hotkey_ParsesCanonicalWin32Combination(string text, uint modifiers, uint key)
    {
        Assert.True(HotkeyText.TryParse(text, out var definition, out _));
        Assert.True(HotkeyText.TryGetChord(definition, out var chord, out _));
        Assert.Equal(new HotkeyChord(modifiers, key), chord);
    }

    [Theory]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+Unknown")]
    [InlineData("A")]
    [InlineData("Ctrl+")]
    public void Hotkey_RejectsInvalidOrUnsafeTypingKey(string text)
        => Assert.False(HotkeyText.TryParse(text, out _, out _));

    [Fact]
    public void Hotkey_EmptyDisablesAndRoundtripPreservesBinding()
    {
        Assert.True(HotkeyText.TryParse("", out var empty, out _));
        Assert.False(empty.Enabled);
        Assert.True(HotkeyText.TryParse("alt+control+q", out var definition, out _));
        Assert.Equal("Ctrl+Alt+Q", HotkeyText.Format(definition));
    }

    [Fact]
    public void Dwell_TriggersOnceAndResetsWhenSuppressedOrLeaving()
    {
        var dwell = new HotCornerDwell();
        Assert.False(dwell.Update("left", 0, 300, false));
        Assert.False(dwell.Update("left", 299, 300, false));
        Assert.True(dwell.Update("left", 300, 300, false));
        Assert.False(dwell.Update("left", 900, 300, false));
        Assert.False(dwell.Update(null, 901, 300, false));
        Assert.False(dwell.Update("left", 1000, 300, true));
        Assert.False(dwell.Update("left", 1500, 300, false));
        Assert.True(dwell.Update("left", 1800, 300, false));
    }

    [Fact]
    public void FullscreenStateCache_BlocksPassiveActionsWhileUnknownOrFullscreen()
    {
        var cache = new FullscreenStateCache();

        Assert.False(cache.CanRunPassive(suppressWhenFullscreen: true));
        cache.Update(isFullscreen: false);
        Assert.True(cache.CanRunPassive(suppressWhenFullscreen: true));

        cache.MarkUnknown();
        Assert.False(cache.CanRunPassive(suppressWhenFullscreen: true));
        cache.Update(isFullscreen: true);
        Assert.False(cache.CanRunPassive(suppressWhenFullscreen: true));
        Assert.True(cache.CanRunPassive(suppressWhenFullscreen: false));
    }

    [Fact]
    public void FullscreenStateCache_StaleWindowedRefreshCannotUndoInvalidation()
    {
        var cache = new FullscreenStateCache();
        cache.Update(isFullscreen: false);
        Assert.True(cache.CanRunPassive(suppressWhenFullscreen: true));

        // Model a recompute that began before the event marked the state Unknown,
        // then returned its older windowed result afterward.
        var staleRefresh = () => cache.Update(isFullscreen: false);
        var currentGeneration = cache.MarkUnknown();
        staleRefresh();

        Assert.False(cache.CanRunPassive(suppressWhenFullscreen: true));
        cache.Update(currentGeneration, isFullscreen: false);
        Assert.True(cache.CanRunPassive(suppressWhenFullscreen: true));
    }

    [Fact]
    public void Geometry_UsesNegativePhysicalCoordinatesAndExclusiveBounds()
    {
        Assert.True(HotCornerDwell.IsInCorner(-1599, -239, -1600, -240, 1600, 2560, HotCorner.TopLeft, 3));
        Assert.False(HotCornerDwell.IsInCorner(-1595, -239, -1600, -240, 1600, 2560, HotCorner.TopLeft, 3));
        Assert.False(HotCornerDwell.IsInCorner(0, 2320, -1600, -240, 1600, 2560, HotCorner.BottomRight, 3));
        Assert.True(HotCornerDwell.IsInCorner(-1, 2319, -1600, -240, 1600, 2560, HotCorner.BottomRight, 3));
    }

    [Fact]
    public void PageReset_DeepCopiesAndPreservesOtherPages()
    {
        var settings = new GlassDeskSettings();
        settings.Launcher.Rows = 8;
        settings.Dock.IconSize = 64;
        settings.Shortcuts.ToggleLauncher = new() { Enabled = true, Control = true, Key = "Q" };
        var reset = SettingsPageEditor.ResetPage(settings, "启动台");
        Assert.Equal(5, reset.Launcher.Rows);
        Assert.Equal(64, reset.Dock.IconSize);
        reset.Shortcuts.ToggleLauncher.Key = "W";
        Assert.Equal("Q", settings.Shortcuts.ToggleLauncher.Key);
        Assert.Equal(8, settings.Launcher.Rows);
    }

    [Fact]
    public void PageReset_PreservesDockCornerAcrossAppearanceAndDisplayAcrossDock()
    {
        var settings = new GlassDeskSettings();
        settings.Appearance.DockCornerRadius = 31;
        settings.Dock.TargetDisplayId = "DISPLAY2";
        settings.Display.DockDisplayId = "DISPLAY2";
        var appearance = SettingsPageEditor.ResetPage(settings, "外观");
        var dock = SettingsPageEditor.ResetPage(settings, "Dock");
        Assert.Equal(31, appearance.Appearance.DockCornerRadius);
        Assert.Equal("DISPLAY2", dock.Dock.TargetDisplayId);
        Assert.Equal("DISPLAY2", dock.Display.DockDisplayId);
        Assert.Equal(40, dock.Appearance.DockCornerRadius);
    }

    [Fact]
    public void Validation_RejectsOutOfRangeAndDuplicateBindingsWithoutMutation()
    {
        var settings = new GlassDeskSettings();
        settings.Launcher.Rows = 100;
        Assert.NotNull(SettingsPageEditor.Validate(settings));
        Assert.Equal(100, settings.Launcher.Rows);
        settings.Launcher.Rows = 5;
        settings.Shortcuts.ToggleLauncher = new() { Enabled = true, Control = true, Key = "Q" };
        settings.Shortcuts.HideLauncher = new() { Enabled = true, Control = true, Key = "Q" };
        Assert.NotNull(SettingsPageEditor.Validate(settings));
        settings.Shortcuts.HideLauncher.Enabled = false;
        Assert.Null(SettingsPageEditor.Validate(settings));
    }
}
