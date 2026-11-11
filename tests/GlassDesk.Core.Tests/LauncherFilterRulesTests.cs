using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherFilterRulesTests
{
    private static LauncherLayout Layout() => new()
    {
        SchemaVersion = 2,
        Pages = [new() { Id = "p", Slots = [
            new() { Kind = LauncherSlotKind.Group, Group = new() { Id = "folder", Name = "保留", ItemIds = ["a", "b"] } },
            new() { ItemId = "c" }, new() { ItemId = "d" }] }],
        FilterGroups = [new() { Id = "work", Name = "工作", ItemIds = ["b", "d"] }],
    };

    [Fact]
    public void CrossPagePlacementFillsEarlierHoleAndKeepsIconAtRequestedGridPosition()
    {
        var source = Layout();
        var projection = LauncherFilterRules.Project(source, null, 2);
        var moved = LauncherFilterRules.PlaceAtSlot(projection, "a", 2, 2, false);
        Assert.Equal(new[] { "b", "c", "a", "d" }, LauncherLayoutRules.AppIds(moved));
        Assert.Equal("a", moved.Pages[1].Slots[0].ItemId);
        var exchanged = LauncherFilterRules.PlaceAtSlot(projection, "a", 2, 2, true);
        Assert.Equal(new[] { "c", "b", "a", "d" }, LauncherLayoutRules.AppIds(exchanged));
    }

    [Fact]
    public void ProjectionFlattensFoldersWithoutChangingTheirOriginalContents()
    {
        var source = Layout(); source.FlatOrder = ["d", "b"];
        var view = LauncherFilterRules.Project(source, null, 2);
        Assert.Equal(new[] { "d", "b", "a", "c" }, LauncherLayoutRules.AppIds(view));
        Assert.Equal(new[] { 2, 2 }, view.Pages.Select(p => p.Slots.Count));
        Assert.All(view.Pages.SelectMany(p => p.Slots), slot => Assert.Equal(LauncherSlotKind.App, slot.Kind));
        Assert.Equal(new[] { "a", "b" }, source.Pages[0].Slots[0].Group!.ItemIds);
    }

    [Fact]
    public void FilterProjectionIncludesOnlyItsMembersInFlatOrder()
    {
        var source = Layout(); source.FlatOrder = ["d", "b", "a", "c"];
        Assert.Equal(new[] { "d", "b" }, LauncherLayoutRules.AppIds(LauncherFilterRules.Project(source, "work", 4)));
        Assert.Empty(LauncherLayoutRules.AppIds(LauncherFilterRules.Project(source, "missing", 4)));
    }

    [Fact]
    public void FlatReorderPreservesFoldersAndOtherGroupMemberships()
    {
        var source = Layout();
        var projected = LauncherFilterRules.Project(source, null, 4);
        projected = LauncherLayoutEditor.PlaceAtSlot(projected, "a", 3, 4, false);
        var result = LauncherFilterRules.ApplyProjection(source, projected, null, 4);
        Assert.Equal(new[] { "b", "c", "d", "a" }, result.FlatOrder);
        Assert.Equal(new[] { "a", "b" }, result.Pages[0].Slots[0].Group!.ItemIds);
        Assert.Equal(new[] { "b", "d" }, result.FilterGroups[0].ItemIds);
        Assert.Empty(source.FlatOrder);
    }

    [Fact]
    public void FilteredReorderOnlyReplacesItsMembersPositionsInOverallOrder()
    {
        var source = Layout();
        var projected = LauncherFilterRules.Project(source, "work", 4);
        projected = LauncherLayoutEditor.PlaceAtSlot(projected, "d", 0, 4, false);
        var result = LauncherFilterRules.ApplyProjection(source, projected, "work", 4);
        Assert.Equal(new[] { "a", "d", "c", "b" }, result.FlatOrder);
        Assert.Equal(new[] { "a", "b", "c", "d" }, LauncherLayoutRules.AppIds(result));
    }

    [Fact]
    public void TrashFromFilteredViewRemovesOnlyTheChosenShortcutAndPrunesReferences()
    {
        var source = Layout();
        var projected = LauncherLayoutEditor.RemoveShortcut(LauncherFilterRules.Project(source, "work", 4), "b", 4);
        var result = LauncherFilterRules.ApplyProjection(source, projected, "work", 4);
        Assert.Equal(new[] { "a", "c", "d" }, LauncherLayoutRules.AppIds(result));
        Assert.Equal(new[] { "d" }, result.FilterGroups[0].ItemIds);
        Assert.DoesNotContain("b", result.FlatOrder);
    }

    [Fact]
    public void CreateRenameAndDeleteDoNotRemoveIconsOrDisallowOverlappingMembership()
    {
        var source = Layout();
        var added = LauncherFilterRules.AddGroup(source, "games", "  游戏  ", new[] { "b", "c", "b", "not-in-launcher" });
        Assert.Equal(new[] { "b", "c" }, added.FilterGroups[1].ItemIds);
        Assert.Equal("游戏", added.FilterGroups[1].Name);
        Assert.Equal(new[] { "b", "d" }, added.FilterGroups[0].ItemIds);
        var renamed = LauncherFilterRules.RenameGroup(added, "games", "娱乐");
        Assert.Equal("娱乐", renamed.FilterGroups[1].Name);
        var removed = LauncherFilterRules.RemoveGroup(renamed, "work");
        Assert.Equal("games", Assert.Single(removed.FilterGroups).Id);
        Assert.Equal(new[] { "a", "b", "c", "d" }, LauncherLayoutRules.AppIds(removed));
        Assert.Single(source.FilterGroups);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNamesAreRejectedWithoutMutatingSource(string name)
    {
        var source = Layout();
        Assert.Throws<ArgumentException>(() => LauncherFilterRules.AddGroup(source, "new", name, new[] { "a" }));
        Assert.Single(source.FilterGroups);
    }
}
