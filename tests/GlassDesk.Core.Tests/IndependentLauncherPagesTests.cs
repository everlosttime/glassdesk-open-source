using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class IndependentLauncherPagesTests
{
    [Fact]
    public void BrowsePageCountsRemainIndependentOfSearchAndKeepEmptyMiddlePage()
    {
        var view = new LauncherViewState(2, 2);
        view.SetPageCounts([1, 0, 2]); view.MoveToPage(2);
        Assert.Equal(3, view.PageCount); Assert.Equal(2, view.VisibleCount); Assert.Equal(3, view.ItemCount);
        view.Query = "search"; view.SetItemCount(5); view.MoveToPage(1);
        Assert.Equal(2, view.PageCount); Assert.Equal(1, view.VisibleCount);
        view.Query = "";
        Assert.Equal(2, view.Page); Assert.Equal(3, view.PageCount);
        view.MoveToPage(1); Assert.Equal(0, view.VisibleCount);
    }
    private static LauncherLayout Layout() => new() { SchemaVersion = 2, Pages = [
        new() { Id = "first", Slots = [new() { ItemId = "a" }, new() { ItemId = "b" }] },
        new() { Id = "second", Slots = [new() { ItemId = "c" }, new() { ItemId = "d" }] }] };
    private static string[] Ids(LauncherLayout layout, int page) => layout.Pages[page].Slots.Select(s => s.ItemId!).ToArray();

    [Fact]
    public void RemovalCompactsOnlyItsOwnPageAndReconcileKeepsBoundary()
    {
        var removed = LauncherLayoutEditor.RemoveShortcut(Layout(), "a", 3);
        Assert.Equal(["b"], Ids(removed, 0));
        Assert.Equal(["c", "d"], Ids(removed, 1));
        var reconciled = LauncherLayoutRules.Reconcile(removed, ["b", "c", "d"], 3);
        Assert.Equal(["b"], Ids(reconciled, 0));
        Assert.Equal(["c", "d"], Ids(reconciled, 1));
        Assert.True(LauncherLayoutRules.Equivalent(removed, reconciled));
    }

    [Fact]
    public void GroupingAndRemovingGroupMembersDoNotPullFromLaterPage()
    {
        var grouped = LauncherLayoutEditor.CreateGroup(Layout(), "a", "b", "g", 3);
        Assert.Single(grouped.Pages[0].Slots);
        Assert.Equal(["c", "d"], Ids(grouped, 1));
        var removed = LauncherLayoutEditor.RemoveShortcut(grouped, "a", 3);
        Assert.Equal(["b"], Ids(removed, 0));
        Assert.Equal(["c", "d"], Ids(removed, 1));
    }

    [Fact]
    public void MoveToNewPageSurvivesSaveAndReconciliationWithoutChangingOriginal()
    {
        var original = Layout();
        var moved = LauncherLayoutEditor.PlaceAtSlot(original, "b", 6, 3, false);
        Assert.Equal(["a"], Ids(moved, 0));
        Assert.Equal(["c", "d"], Ids(moved, 1));
        Assert.Equal(["b"], Ids(moved, 2));
        using var temp = new TempDataDirectory();
        var store = new LayoutStore(temp.Paths.DockLayoutFile, temp.Paths.LauncherLayoutFile);
        Assert.True(store.CommitLauncher(moved, store.LauncherVersion));
        var reload = new LayoutStore(temp.Paths.DockLayoutFile, temp.Paths.LauncherLayoutFile); reload.LoadLauncher();
        Assert.True(LauncherLayoutRules.Equivalent(moved, LauncherLayoutRules.Reconcile(reload.Launcher, ["a", "b", "c", "d"], 3)));
        Assert.Equal(["a", "b"], Ids(original, 0));
    }

    [Fact]
    public void CrossPageExchangePreservesBothPagesAndBlankTargetFallsBackToMove()
    {
        var swapped = LauncherLayoutEditor.PlaceAtSlot(Layout(), "a", 3, 3, true);
        Assert.Equal(["c", "b"], Ids(swapped, 0));
        Assert.Equal(["a", "d"], Ids(swapped, 1));
        var moved = LauncherLayoutEditor.PlaceAtSlot(swapped, "a", 6, 3, true);
        Assert.Equal(["d"], Ids(moved, 1)); Assert.Equal(["a"], Ids(moved, 2));
    }
}
