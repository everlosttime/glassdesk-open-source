using GlassDesk.AppServices;

namespace GlassDesk.AppServices.Tests;

public sealed class StageWorkspaceTests
{
    [Fact]
    public void SwitchIsCommittedOnlyAfterTheLatestRequestSucceeds()
    {
        var workspace = new StageWorkspace();
        workspace.Add(1);
        workspace.Add(2);
        workspace.Add(3);

        var second = workspace.BeginSwitch(2);
        var third = workspace.BeginSwitch(3);

        Assert.False(workspace.Commit(second));
        Assert.Equal((nint)1, workspace.Active);
        Assert.True(workspace.Commit(third));
        Assert.Equal((nint)3, workspace.Active);
        Assert.Equal(new nint[] { 2, 1 }, workspace.Background);
    }

    [Fact]
    public void GroupsKeepTheirIdentityWhenTheFirstMemberCloses()
    {
        var workspace = new StageWorkspace();
        var group = workspace.Add(10);
        var incoming = workspace.Add(20);
        workspace.Add(30);

        Assert.True(workspace.MergeIntoActive(incoming));
        workspace.Remove(10);

        Assert.Equal(group, workspace.Active);
        Assert.Equal(new nint[] { 20 }, workspace.Members(group));
        Assert.Equal(group, workspace.GroupOf(20));
    }

    [Fact]
    public void SplittingAMemberCreatesAnIndependentBackgroundGroup()
    {
        var workspace = new StageWorkspace();
        var active = workspace.Add(10);
        Assert.True(workspace.MergeIntoActive(workspace.Add(20)));

        var split = workspace.Split(20);

        Assert.Equal(new nint[] { 10 }, workspace.Members(active));
        Assert.Equal(new nint[] { 20 }, workspace.Members(split));
        Assert.Equal(new[] { split }, workspace.Background);
    }

    [Fact]
    public void RemovingTheActiveGroupKeepsTheNextGroupInTheRailUntilRestored()
    {
        var workspace = new StageWorkspace();
        workspace.Add(1);
        workspace.Add(2);
        workspace.Remove(1);

        Assert.Equal(nint.Zero, workspace.Active);
        Assert.Equal(new nint[] { 2 }, workspace.Background);
        Assert.True(workspace.Commit(workspace.BeginSwitch(2)));
    }
}
