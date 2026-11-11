using System.Text.Json;
using GlassDesk.Core.Models;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LauncherFilterPersistenceTests
{
    private const string LayoutJson = """
        {"schemaVersion":2,"pages":[{"id":"p","slots":[
          {"kind":"Group","group":{"id":"folder","name":"旧文件夹","itemIds":["a","b"]}},
          {"itemId":"c"}]}],"flatOrder":["c","a","b"],
          "filterGroups":[{"id":"work","name":"工作","itemIds":["a","c"]}]}
        """;

    private static LauncherLayout Read(string json = LayoutJson)
        => JsonSerializer.Deserialize(json, GlassDeskJsonContext.Default.LauncherLayout)!;

    private static JsonElement Document(LauncherLayout layout)
        => JsonSerializer.SerializeToElement(layout, GlassDeskJsonContext.Default.LauncherLayout);

    [Fact]
    public void SavingAndReloadingRetainsFiltersAlongsideOriginalFolders()
    {
        using var temp = new TempDataDirectory();
        var data = new GlassDeskDataStore(temp.Paths); data.LoadAll();
        Assert.True(data.Layouts.CommitLauncher(Read(), data.Layouts.LauncherVersion));
        var reloaded = new GlassDeskDataStore(temp.Paths); reloaded.LoadAll();
        var json = Document(reloaded.Layouts.Launcher);
        Assert.True(json.TryGetProperty("filterGroups", out var groups), "Named filter groups must survive a layout save.");
        Assert.Equal("工作", groups[0].GetProperty("name").GetString());
        Assert.Equal(new[] { "c", "a", "b" }, json.GetProperty("flatOrder").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "a", "b" }, reloaded.Layouts.Launcher.Pages[0].Slots[0].Group!.ItemIds);
    }

    [Fact]
    public void ClonePreservesFilterMetadataForDetachedEdits()
    {
        var clone = Document(LauncherLayoutRules.DeepClone(Read()));
        Assert.True(clone.TryGetProperty("filterGroups", out var groups), "Detached edits must include filter metadata.");
        Assert.Equal("work", groups[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("工作", "游戏")]
    [InlineData("[\"c\",\"a\",\"b\"]", "[\"b\",\"a\",\"c\"]")]
    public void MetadataOnlyEditsAreNotDiscardedAsEquivalent(string before, string after)
        => Assert.False(LauncherLayoutRules.Equivalent(Read(), Read(LayoutJson.Replace(before, after, StringComparison.Ordinal))));

    [Fact]
    public void ReconcileKeepsFilterAndPrunesMembersMissingFromLauncher()
    {
        var result = Document(LauncherLayoutRules.Reconcile(Read(), ["a", "b"], 4));
        Assert.True(result.TryGetProperty("filterGroups", out var groups), "Reconcile must preserve named filters.");
        Assert.Equal(new[] { "a" }, groups[0].GetProperty("itemIds").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "a", "b" }, result.GetProperty("flatOrder").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void FailedSaveRestoresGroupNamesAndFlatOrderInMemoryAndOnDisk()
    {
        using var temp = new TempDataDirectory();
        var data = new GlassDeskDataStore(temp.Paths); data.LoadAll();
        data.Layouts.CommitLauncher(Read(), data.Layouts.LauncherVersion);
        var before = File.ReadAllBytes(temp.Paths.LauncherLayoutFile);
        var edit = LauncherLayoutRules.DeepClone(data.Layouts.Launcher);
        edit.FilterGroups[0].Name = "不应保存";
        edit.FlatOrder.Reverse();
        using (var blocker = new FileStream(temp.Paths.LauncherLayoutFile + AtomicFile.TempSuffix, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<StorageException>(() => data.Layouts.CommitLauncher(edit, data.Layouts.LauncherVersion));
        Assert.Equal(before, File.ReadAllBytes(temp.Paths.LauncherLayoutFile));
        Assert.Equal("工作", data.Layouts.Launcher.FilterGroups[0].Name);
        Assert.Equal(new[] { "c", "a", "b" }, data.Layouts.Launcher.FlatOrder);
    }

    [Fact]
    public void GroupModeSettingSurvivesSaveAndReload()
    {
        using var temp = new TempDataDirectory();
        File.WriteAllText(temp.Paths.SettingsFile, """{"launcher":{"groupModeEnabled":true}}""");
        var store = new SettingsStore(temp.Paths.SettingsFile); store.Load(); store.Save();
        using var json = JsonDocument.Parse(File.ReadAllText(temp.Paths.SettingsFile));
        Assert.True(json.RootElement.GetProperty("launcher").TryGetProperty("groupModeEnabled", out var enabled), "Mode must be persisted.");
        Assert.True(enabled.GetBoolean());
    }
}
