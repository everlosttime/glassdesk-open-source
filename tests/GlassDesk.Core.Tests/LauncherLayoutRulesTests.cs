using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LauncherLayoutRulesTests
{
    [Fact]
    public void Reconcile_PreservesFirstReferenceAndAppendsNewEntries()
    {
        var layout = new LauncherLayout { Pages = [new LauncherPage { Id = "p", Slots =
        [
            new LauncherSlot { ItemId = "a" },
            new LauncherSlot { Kind = LauncherSlotKind.Group, Group = new LauncherGroup { Id = "g", Name = "工具", ItemIds = ["b", "a", "missing"] } },
            new LauncherSlot { ItemId = "b" },
        ] }] };

        var result = LauncherLayoutRules.Reconcile(layout, ["a", "b", "c"], 35);

        Assert.Equal(["a", "b", "c"], LauncherLayoutRules.AppIds(result));
        Assert.Equal("b", result.Pages[0].Slots[1].ItemId);
        Assert.Equal(LauncherSlotKind.App, result.Pages[0].Slots[1].Kind);
        Assert.Equal("c", result.Pages[0].Slots[2].ItemId);
        Assert.Equal(result.Pages[0].Slots.Count, LauncherLayoutRules.Reconcile(result, ["a", "b", "c"], 35).Pages[0].Slots.Count);
    }

    [Fact]
    public void DeepClone_DoesNotShareNestedMembers()
    {
        var original = new LauncherLayout { Pages = [new LauncherPage { Id = "p", Slots =
        [new LauncherSlot { Kind = LauncherSlotKind.Group, Group = new LauncherGroup { Id = "g", ItemIds = ["a"] } }] }] };
        var clone = LauncherLayoutRules.DeepClone(original);
        clone.Pages[0].Slots[0].Group!.ItemIds.Add("b");
        Assert.Equal(["a"], original.Pages[0].Slots[0].Group!.ItemIds);
    }

    [Fact]
    public void SaveLauncher_FailureRestoresSharedLayout()
    {
        using var temp = new TempDataDirectory();
        var store = new LayoutStore(temp.Paths.DockLayoutFile, temp.Paths.LauncherLayoutFile);
        store.Launcher.Pages.Add(new LauncherPage { Id = "p", Slots = [new LauncherSlot { ItemId = "a" }] });
        store.SaveLauncher();
        store.Launcher.Pages[0].Slots[0].ItemId = "b";
        File.SetAttributes(temp.Paths.LauncherLayoutFile, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<StorageException>(() => store.SaveLauncher());
            Assert.Equal("a", store.Launcher.Pages[0].Slots[0].ItemId);
        }
        finally { File.SetAttributes(temp.Paths.LauncherLayoutFile, FileAttributes.Normal); }
    }

    [Fact]
    public void CommitLauncher_RejectsStalePreview()
    {
        using var temp = new TempDataDirectory();
        var store = new LayoutStore(temp.Paths.DockLayoutFile, temp.Paths.LauncherLayoutFile);
        var version = store.LauncherVersion;
        var draft = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a"], 35);
        Assert.True(store.CommitLauncher(draft, version));
        Assert.False(store.CommitLauncher(new LauncherLayout(), version));
        Assert.Equal("a", store.Launcher.Pages[0].Slots[0].ItemId);
    }

    [Fact]
    public void FailedGroupEdit_RestoresNestedMembersInSharedLayoutAndDisk()
    {
        using var temp = new TempDataDirectory();
        var store = new LayoutStore(temp.Paths.DockLayoutFile, temp.Paths.LauncherLayoutFile);
        store.Launcher.Pages.Add(new LauncherPage { Id = "p", Slots =
        [new LauncherSlot { Kind = LauncherSlotKind.Group, Group = new LauncherGroup
        { Id = "g", Name = "工具", ItemIds = ["a", "b"] } }] });
        store.SaveLauncher();
        var bytes = File.ReadAllBytes(temp.Paths.LauncherLayoutFile);
        var version = store.LauncherVersion;
        var draft = LauncherLayoutRules.DeepClone(store.Launcher);
        draft.Pages[0].Slots[0].Group!.ItemIds.Reverse();
        using (File.Open(temp.Paths.LauncherLayoutFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<StorageException>(() => store.CommitLauncher(draft, version));
        Assert.Equal(["a", "b"], store.Launcher.Pages[0].Slots[0].Group!.ItemIds);
        Assert.Equal(bytes, File.ReadAllBytes(temp.Paths.LauncherLayoutFile));
        Assert.Equal(version, store.LauncherVersion);
    }
}
