using System.Text.Json;
using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherRemovalTests
{
    [Fact]
    public void TrashRemovalSurvivesReloadWithoutKeepingAHiddenList()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["keep", "remove"], 35);
        var removed = LauncherLayoutEditor.RemoveShortcut(initial, "remove", 35);
        var json = JsonSerializer.Serialize(removed);
        Assert.DoesNotContain("excludedAppIds", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(removed.ExcludedAppIds);
        var reloaded = JsonSerializer.Deserialize<LauncherLayout>(json)!;
        Assert.Equal(["keep"], LauncherLayoutRules.AppIds(
            LauncherLayoutRules.Reconcile(reloaded, ["keep", "remove", "dock-only"], 35)));
        Assert.Equal(["keep", "remove"], LauncherLayoutRules.AppIds(initial));
    }

    [Fact]
    public void RemovingAGroupDoesNotReturnItsMembersOnRefresh()
    {
        var initial = LauncherLayoutRules.Reconcile(new LauncherLayout(), ["a", "b", "keep"], 35);
        var grouped = LauncherLayoutEditor.CreateGroup(initial, "a", "b", "group", 35);
        var removed = LauncherLayoutEditor.RemoveShortcut(grouped, "group", 35);
        Assert.Empty(removed.ExcludedAppIds);
        Assert.Equal(["keep"], LauncherLayoutRules.AppIds(
            LauncherLayoutRules.Reconcile(removed, ["a", "b", "keep"], 35)));
    }

    [Fact]
    public void OldHiddenPreferencesMigrateToVisibleMembershipOnly()
    {
        var old = JsonSerializer.Deserialize<LauncherLayout>(
            """{"Pages":[{"Id":"p","Slots":[{"ItemId":"keep"}]}],"excludedAppIds":["old-hidden"]}""")!;
        var migrated = LauncherLayoutRules.Reconcile(old, ["keep", "old-hidden"], 35);
        Assert.Equal(["keep"], LauncherLayoutRules.AppIds(migrated));
        Assert.DoesNotContain("excludedAppIds", JsonSerializer.Serialize(migrated), StringComparison.OrdinalIgnoreCase);
    }
}
