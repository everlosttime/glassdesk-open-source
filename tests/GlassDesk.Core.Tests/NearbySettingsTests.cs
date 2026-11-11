using System.Text.Json;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class NearbySettingsTests
{
    [Theory]
    [InlineData("{}", false, true)]
    [InlineData("{\"nearby\":{\"enabled\":true},\"dock\":{\"showNearbyEntry\":false}}", true, false)]
    [InlineData("{\"nearby\":{\"enabled\":false},\"dock\":{\"showNearbyEntry\":true}}", false, true)]
    public void LoadSaveAndOtherPageEditsPreserveExplicitChoices(string json, bool enabled, bool visible)
    {
        using var temp = new TempDataDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Paths.SettingsFile)!);
        File.WriteAllText(temp.Paths.SettingsFile, json);
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        var copy = SettingsPageEditor.Clone(store.Current);
        copy.Dock.SizeScale = 1.2;
        SettingsPageEditor.Apply(store.Current, copy);
        store.Save();
        using var saved = JsonDocument.Parse(File.ReadAllText(temp.Paths.SettingsFile));
        Assert.True(saved.RootElement.TryGetProperty("nearby", out var nearby));
        Assert.Equal(enabled, nearby.GetProperty("enabled").GetBoolean());
        Assert.Equal(visible, saved.RootElement.GetProperty("dock").GetProperty("showNearbyEntry").GetBoolean());
    }

    [Fact]
    public void ResetNearbyKeepsOtherPageChoices()
    {
        var source = JsonSerializer.Deserialize("{\"nearby\":{\"enabled\":true,\"deviceName\":\"我的电脑\"}}", GlassDeskJsonContext.Default.GlassDeskSettings)!;
        source.Dock.SizeScale = 1.25;
        var reset = SettingsPageEditor.ResetPage(source, "附近传输");
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(reset, GlassDeskJsonContext.Default.GlassDeskSettings));
        Assert.False(result.RootElement.GetProperty("nearby").GetProperty("enabled").GetBoolean());
        Assert.Equal(1.25, reset.Dock.SizeScale);
    }

    [Fact]
    public void BackupRestoresNearbySettingsAndRebasesItsManagedIcon()
    {
        using var source = new TempDataDirectory(); using var archive = new TempDataDirectory(); using var target = new TempDataDirectory();
        var data = source.CreateStore(); data.LoadAll();
        data.Settings.Current.Nearby.Enabled = true;
        data.Settings.Current.Nearby.DeviceName = "媒体电脑";
        data.Settings.Current.Dock.ShowNearbyEntry = false;
        var icon = Path.Combine(data.Paths.IconCacheDirectory, "custom-nearby.png");
        File.WriteAllBytes(icon, [1, 2, 3]); data.Settings.Current.Dock.NearbyCustomIconPath = icon;
        data.Settings.Save();
        Assert.True(data.Backup.BackupTo(archive.Paths.RootDirectory).Succeeded);
        Assert.True(new BackupService(target.Paths).RestoreFrom(archive.Paths.RootDirectory).Succeeded);
        var loaded = new SettingsStore(target.Paths.SettingsFile); loaded.Load();
        Assert.True(loaded.Current.Nearby.Enabled); Assert.False(loaded.Current.Dock.ShowNearbyEntry);
        Assert.Equal("媒体电脑", loaded.Current.Nearby.DeviceName);
        Assert.Equal(Path.Combine(target.Paths.IconCacheDirectory, "custom-nearby.png"), loaded.Current.Dock.NearbyCustomIconPath);
    }
}
