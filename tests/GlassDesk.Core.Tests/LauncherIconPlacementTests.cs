using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherIconPlacementTests
{
    private static LauncherLayout Layout() => LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "c", "d", "e"], 3);

    private static LauncherLayout Place(LauncherLayout layout, string id, int index, bool swap = false)
        => LauncherLayoutEditor.PlaceAtSlot(layout, id, index, 3, swap);

    [Fact]
    public void InsertUsesFinalGridSlotAndShiftsInterveningIcons()
    {
        var original = Layout();
        Assert.Equal(["b", "c", "d", "a", "e"], LauncherLayoutRules.AppIds(Place(original, "a", 3)));
        Assert.Equal(["a", "b", "c", "d", "e"], LauncherLayoutRules.AppIds(original));
    }

    [Fact]
    public void InsertBackwardsPullsIconsForwardAcrossPageBoundary()
        => Assert.Equal(["a", "e", "b", "c", "d"], LauncherLayoutRules.AppIds(Place(Layout(), "e", 1)));

    [Fact]
    public void ExchangeAcrossPagesOnlyChangesTheTwoParticipants()
        => Assert.Equal(["e", "b", "c", "d", "a"], LauncherLayoutRules.AppIds(Place(Layout(), "a", 4, true)));

    [Fact]
    public void EmptyGridSpaceSnapsToLastAvailableSlot()
        => Assert.Equal(["b", "c", "d", "e", "a"], LauncherLayoutRules.AppIds(Place(Layout(), "a", 34)));

    [Fact]
    public void RepeatedPreviewAgainstOriginDoesNotAccumulateMoves()
    {
        var original = Layout();
        _ = Place(original, "a", 4, true);
        Assert.Equal(["c", "b", "a", "d", "e"], LauncherLayoutRules.AppIds(Place(original, "a", 2, true)));
        Assert.Equal(["a", "b", "c", "d", "e"], LauncherLayoutRules.AppIds(Place(original, "a", 0)));
    }
}
