using System.Text.Json;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class StatusBarSecondaryItemsSettingsTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NormalizationCopiesArraysAndRenderingUsesFixedOrder()
    {
        var input = new[] { "music", "keyboardBattery", "fan" };
        var settings = new GlassDeskSettings { Dock = new() { StatusBarSecondaryItems = input } };
        settings.Normalize();
        input[0] = "cpu";
        Assert.Equal(new[] { "fan", "keyboardBattery", "music" },
            StatusBarSecondaryItemCatalog.GetRenderItems(settings.Dock.StatusBarSecondaryItems));
        var clone = SettingsPageEditor.Clone(settings);
        clone.Dock.StatusBarSecondaryItems[0] = "memory";
        Assert.Equal("music", settings.Dock.StatusBarSecondaryItems[0]);
        var first = new DockSettings();
        var second = new DockSettings();
        first.StatusBarSecondaryItems[0] = "fan";
        Assert.Equal("cpu", second.StatusBarSecondaryItems[0]);
    }

    [Theory]
    [InlineData("{}", "cpu,memory,music")]
    [InlineData("{\"statusBarSecondaryItems\":null}", "cpu,memory,music")]
    [InlineData("{\"statusBarSecondaryItems\":[]}", "")]
    [InlineData("{\"statusBarSecondaryItems\":[\"invalid\",\"fan\",\"fan\",\"keyboardBattery\",\"cpu\",\"music\"]}", "fan,keyboardBattery,cpu")]
    public void MigratesFiltersAndPersistsAnIndependentSelection(string dockJson, string expected)
    {
        File.WriteAllText(_temp.Paths.SettingsFile, "{\"dock\":" + dockJson + "}");
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        store.Current.Dock.ShowThisPcEntry = false;
        store.Save();
        using var document = JsonDocument.Parse(File.ReadAllText(_temp.Paths.SettingsFile));
        Assert.True(document.RootElement.GetProperty("dock").TryGetProperty("statusBarSecondaryItems", out var items));
        Assert.Equal(expected, string.Join(',', items.EnumerateArray().Select(x => x.GetString())));
    }

    [Fact]
    public void DeviceBindingsSurviveCloneSaveAndUnrelatedResetAndDockResetClearsThem()
    {
        File.WriteAllText(_temp.Paths.SettingsFile,
            """{"dock":{"statusBarSecondaryItems":["headsetBattery"],"statusBarMouseDeviceKey":"mouse:receiver:1","statusBarHeadsetDeviceKey":"offline:headset","statusBarKeyboardDeviceKey":"keyboard:2"}}""");
        var store = new SettingsStore(_temp.Paths.SettingsFile);
        store.Load();
        var copy = SettingsPageEditor.ResetPage(SettingsPageEditor.Clone(store.Current), "性能");
        SettingsPageEditor.Apply(store.Current, copy);
        store.Save();
        using var document = JsonDocument.Parse(File.ReadAllText(_temp.Paths.SettingsFile));
        var dock = document.RootElement.GetProperty("dock");
        Assert.True(dock.TryGetProperty("statusBarHeadsetDeviceKey", out var key));
        Assert.Equal("offline:headset", key.GetString());
        Assert.Equal("mouse:receiver:1", dock.GetProperty("statusBarMouseDeviceKey").GetString());
        Assert.Equal("keyboard:2", dock.GetProperty("statusBarKeyboardDeviceKey").GetString());
        var reset = SettingsPageEditor.ResetPage(copy, "Dock");
        var bindings = typeof(DockSettings).GetProperty("StatusBarHeadsetDeviceKey")!;
        Assert.Null(bindings.GetValue(reset.Dock));
    }
}
