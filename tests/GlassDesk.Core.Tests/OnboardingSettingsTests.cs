using System.Text.Json;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class OnboardingSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-Onboarding-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings.json");

    [Fact]
    public void FreshProfileRemainsEligibleAfterSettingsAreSavedAndReloaded()
    {
        var store = new SettingsStore(SettingsPath);
        store.Load();
        store.Save();
        var reloaded = new SettingsStore(SettingsPath);
        reloaded.Load();
        Assert.False(Dismissed(reloaded.Current));
    }

    [Fact]
    public void LegacyProfileDoesNotInterruptExistingUsers()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, "{\"schemaVersion\":1,\"dock\":{\"iconSize\":64}}");
        var store = new SettingsStore(SettingsPath);
        store.Load();
        Assert.True(Dismissed(store.Current));
        Assert.Equal(64, store.Current.Dock.IconSize);
    }

    [Fact]
    public void DismissalSurvivesReloadAndPageReset()
    {
        var store = new SettingsStore(SettingsPath);
        store.Load();
        Dismiss(store);
        var reloaded = new SettingsStore(SettingsPath);
        reloaded.Load();
        Assert.True(Dismissed(reloaded.Current));
        var reset = SettingsPageEditor.ResetPage(reloaded.Current, "外观");
        SettingsPageEditor.Apply(reloaded.Current, reset);
        reloaded.Save();
        Assert.True(Dismissed(reloaded.Current));
    }

    [Fact]
    public void FailedDismissalDoesNotPretendProgressWasSaved()
    {
        Directory.CreateDirectory(_root);
        var store = new SettingsStore(SettingsPath);
        store.Load();
        Directory.CreateDirectory(SettingsPath);
        Assert.Throws<StorageException>(() => Dismiss(store));
        Assert.False(Dismissed(store.Current));
    }

    private static bool Dismissed(GlassDeskSettings settings)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(settings, GlassDeskJsonContext.Default.GlassDeskSettings));
        Assert.True(json.RootElement.TryGetProperty("onboardingDismissed", out var state), "Missing onboarding eligibility state");
        return state.GetBoolean();
    }

    private static void Dismiss(SettingsStore store)
    {
        store.DismissOnboarding();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
