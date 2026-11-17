using GlassDesk.AppServices;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class WindowTopmostHotkeyRegistrationTests
{
    [Theory]
    [InlineData("T", true, true, 84u)]
    [InlineData("F9", false, false, 120u)]
    public void StartupSkipsUnavailableWindowSelectionWithoutChangingTheProfile(string key, bool control, bool alt, uint rejectedKey)
    {
        using var fixture = new Fixture();
        var shortcuts = Shortcuts();
        shortcuts.SelectWindowTopmost = new() { Enabled = true, Control = control, Alt = alt, Key = key };
        fixture.Data.Settings.Current.Shortcuts = shortcuts;
        fixture.Data.Settings.Save();
        var before = File.ReadAllBytes(fixture.Data.Settings.FilePath);
        fixture.Registry.RejectKey = rejectedKey;

        Assert.Null(Initialize(fixture.Service, shortcuts, out var warning));

        Assert.NotNull(warning);
        Assert.Contains(shortcuts.SelectWindowTopmost.Describe(), warning);
        Assert.Contains("1409", warning);
        Assert.Equal(new[] { InputAction.ToggleLauncher, InputAction.ToggleGameSleep },
            fixture.Registry.Items.Keys.Select(fixture.Service.ResolveAction).OrderBy(action => action));
        Assert.Equal(shortcuts.SelectWindowTopmost.Describe(), fixture.Data.Settings.Current.Shortcuts.SelectWindowTopmost.Describe());
        Assert.Equal(before, File.ReadAllBytes(fixture.Data.Settings.FilePath));
    }

    [Fact]
    public void StartupPreservesAnExistingActionUsingTheWindowSelectionChord()
    {
        using var fixture = new Fixture();
        var shortcuts = Shortcuts();
        shortcuts.ToggleLauncher = new() { Enabled = true, Control = true, Alt = true, Key = "T" };
        fixture.Data.Settings.Current.Shortcuts = shortcuts;
        fixture.Data.Settings.Save();
        var before = File.ReadAllBytes(fixture.Data.Settings.FilePath);

        Assert.Null(Initialize(fixture.Service, shortcuts, out var warning));

        Assert.Contains("Ctrl+Alt+T", warning);
        var launcher = Assert.Single(fixture.Registry.Items, item => item.Value == new HotkeyChord(3, 84));
        Assert.Equal(InputAction.ToggleLauncher, fixture.Service.ResolveAction(launcher.Key));
        Assert.DoesNotContain(fixture.Registry.Items.Keys, id => fixture.Service.ResolveAction(id) == InputAction.SelectWindowTopmost);
        Assert.Equal("Ctrl+Alt+T", fixture.Data.Settings.Current.Shortcuts.SelectWindowTopmost.Describe());
        Assert.Equal(before, File.ReadAllBytes(fixture.Data.Settings.FilePath));
    }

    [Fact]
    public void StartupRegistersAvailableWindowSelectionAndIgnoresAnExplicitlyDisabledBinding()
    {
        using var fixture = new Fixture();
        var shortcuts = Shortcuts();
        Assert.Null(Initialize(fixture.Service, shortcuts, out var warning));
        Assert.Null(warning);
        var selected = Assert.Single(fixture.Registry.Items, item => item.Value == new HotkeyChord(3, 84));
        Assert.Equal(InputAction.SelectWindowTopmost, fixture.Service.ResolveAction(selected.Key));
        shortcuts.SelectWindowTopmost = new();
        Assert.Null(Initialize(fixture.Service, shortcuts, out warning));
        Assert.Null(warning);
        Assert.Equal(InputAction.None, fixture.Service.ResolveAction(selected.Key));
    }

    [Fact]
    public void InteractiveSaveKeepsTheOldRegistrationsAndFileWhenWindowSelectionConflicts()
    {
        using var fixture = new Fixture();
        var shortcuts = Shortcuts();
        shortcuts.SelectWindowTopmost = new();
        Assert.Null(fixture.Service.Apply(shortcuts, new()));
        var before = File.ReadAllBytes(fixture.Data.Settings.FilePath);
        var ids = fixture.Registry.Items.Keys.Order().ToArray();
        shortcuts.SelectWindowTopmost = new() { Enabled = true, Key = "F9" };
        fixture.Registry.RejectKey = 120;

        Assert.NotNull(fixture.Service.Apply(shortcuts, new()));

        Assert.Equal(ids, fixture.Registry.Items.Keys.Order());
        Assert.Equal(before, File.ReadAllBytes(fixture.Data.Settings.FilePath));
        Assert.False(fixture.Data.Settings.Current.Shortcuts.SelectWindowTopmost.Enabled);
        shortcuts.SelectWindowTopmost = new() { Enabled = true, Key = "F4" };
        Assert.NotNull(fixture.Service.Apply(shortcuts, new()));
        Assert.Equal(ids, fixture.Registry.Items.Keys.Order());
    }

    [Fact]
    public void StartupStillRollsBackWhenAnExistingActionCannotRegister()
    {
        using var fixture = new Fixture();
        fixture.Registry.RejectKey = 71;
        Assert.NotNull(Initialize(fixture.Service, Shortcuts(), out _));
        Assert.Empty(fixture.Registry.Items);
    }

    private static string? Initialize(InteractionSettingsService service, ShortcutSettings shortcuts, out string? warning) =>
        service.Initialize(shortcuts, new(), out warning);

    private static ShortcutSettings Shortcuts() => new()
    {
        ToggleLauncher = new() { Enabled = true, Key = "F4" },
        NextPage = new(), PreviousPage = new(),
    };

    private sealed class Registry : IHotkeyRegistry
    {
        public readonly Dictionary<int, HotkeyChord> Items = [];
        public uint RejectKey;
        public bool TryRegister(int id, HotkeyChord chord, out int errorCode)
        {
            errorCode = 1409;
            if (chord.VirtualKey == RejectKey || Items.ContainsValue(chord)) return false;
            Items.Add(id, chord); errorCode = 0; return true;
        }
        public bool Unregister(int id) => Items.Remove(id);
        public void Dispose() => Items.Clear();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "GlassDesk-topmost-hotkey-" + Guid.NewGuid().ToString("N"));
        public GlassDeskDataStore Data { get; }
        public Registry Registry { get; } = new();
        public InteractionSettingsService Service { get; }
        public Fixture() { Data = new(new AppPaths(root)); Data.LoadAll(); Service = new(Data.Settings, Registry); }
        public void Dispose() { Service.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
