using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherLayoutEditorTests
{
    private static LauncherLayout Layout(int count, int capacity = 2)
        => LauncherLayoutRules.Reconcile(new LauncherLayout(), Enumerable.Range(0, count).Select(i => $"a{i}"), capacity);

    [Fact]
    public void MoveAcrossPages_KeepsEveryItemOnce()
    {
        var original = Layout(5);
        var changed = LauncherLayoutEditor.Move(original, "a0", 2, 1, 2);
        Assert.Equal(["a1", "a2", "a3", "a4", "a0"], LauncherLayoutRules.AppIds(changed));
        Assert.Equal(["a0", "a1", "a2", "a3", "a4"], LauncherLayoutRules.AppIds(original));
    }

    [Fact]
    public void MoveToTargetIndex_AccountsForRemovedSource()
    {
        var changed = LauncherLayoutEditor.Move(Layout(3, 3), "a0", 0, 2, 3);
        Assert.Equal(["a1", "a0", "a2"], LauncherLayoutRules.AppIds(changed));
    }

    [Fact]
    public void CreateAndDissolveGroup_PreservesMembersAndPosition()
    {
        var grouped = LauncherLayoutEditor.CreateGroup(Layout(3), "a0", "a1", "g", 2);
        Assert.Equal(LauncherSlotKind.Group, grouped.Pages[0].Slots[0].Kind);
        Assert.Equal(["a1", "a0"], grouped.Pages[0].Slots[0].Group!.ItemIds);
        var dissolved = LauncherLayoutEditor.Dissolve(grouped, "g", 2);
        Assert.Equal(["a1", "a0", "a2"], LauncherLayoutRules.AppIds(dissolved));
        Assert.All(dissolved.Pages.SelectMany(p => p.Slots), slot => Assert.Equal(LauncherSlotKind.App, slot.Kind));
    }

    [Fact]
    public void GroupCannotBeNested()
    {
        var grouped = LauncherLayoutEditor.CreateGroup(Layout(4), "a0", "a1", "g", 2);
        Assert.Throws<InvalidOperationException>(() => LauncherLayoutEditor.AddToGroup(grouped, "g", "g", 2));
    }

    [Fact]
    public void GroupMembersCanBeReorderedWithoutMovingOtherSlots()
    {
        var grouped = LauncherLayoutEditor.CreateGroup(Layout(3), "a0", "a1", "g", 2);
        var changed = LauncherLayoutEditor.MoveGroupMember(grouped, "g", "a0", 0);
        Assert.Equal(["a0", "a1"], changed.Pages[0].Slots[0].Group!.ItemIds);
        Assert.Equal("a2", changed.Pages[0].Slots[1].ItemId);
    }
}
