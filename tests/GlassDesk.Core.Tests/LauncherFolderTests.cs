using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LauncherFolderTests
{
    [Fact]
    public void DraggingOutPenultimateMemberDissolvesAtOriginalSlot()
    {
        var layout = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "c"], 6);
        var grouped = LauncherLayoutEditor.CreateGroup(layout, "b", "a", "folder", 6);
        var moved = LauncherLayoutEditor.RemoveFromGroup(grouped, "b", "folder", 0, 2, 6);
        var slots = moved.Pages.SelectMany(p => p.Slots).ToList();
        Assert.All(slots, slot => Assert.Equal(LauncherSlotKind.App, slot.Kind));
        Assert.Equal(["a", "c", "b"], slots.Select(s => s.ItemId));
        Assert.Equal(2, grouped.Pages[0].Slots[0].Group!.ItemIds.Count);
    }

    [Fact]
    public void ReconcileDissolvesSingletonAfterLibraryRemoval()
    {
        var layout = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "c"], 6);
        var grouped = LauncherLayoutEditor.CreateGroup(layout, "b", "a", "folder", 6);
        var reconciled = LauncherLayoutRules.Reconcile(grouped, ["a", "c"], 6);
        Assert.All(reconciled.Pages.SelectMany(p => p.Slots), slot => Assert.Equal(LauncherSlotKind.App, slot.Kind));
        Assert.Equal(["a", "c"], LauncherLayoutRules.AppIds(reconciled));
    }

    [Fact]
    public void RenameReorderAndMembershipSurviveStoreReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LayoutStore(Path.Combine(root, "dock.json"), Path.Combine(root, "launcher.json"));
            store.LoadLauncher();
            var layout = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "c", "d"], 6);
            layout = LauncherLayoutEditor.CreateGroup(layout, "b", "a", "folder", 6);
            layout = LauncherLayoutEditor.AddToGroup(layout, "c", "folder", 6);
            layout = LauncherLayoutEditor.MoveGroupMember(layout, "folder", "c", 0);
            layout = LauncherLayoutEditor.RenameGroup(layout, "folder", "工作工具");
            Assert.True(store.CommitLauncher(layout, store.LauncherVersion));
            var reloaded = new LayoutStore(Path.Combine(root, "dock.json"), Path.Combine(root, "launcher.json"));
            reloaded.LoadLauncher();
            Assert.True(LauncherLayoutRules.Equivalent(layout, reloaded.Launcher));
        }
        finally { Directory.Delete(root, true); }
    }
}
