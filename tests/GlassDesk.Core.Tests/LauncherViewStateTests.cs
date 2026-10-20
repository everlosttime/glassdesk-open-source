using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherViewStateTests
{
    [Fact]
    public void Paging_CoversFiveHundredItems()
    {
        var view = new LauncherViewState(5, 7);
        view.SetItemCount(500);
        Assert.Equal(15, view.PageCount);
        view.MoveToPage(14);
        Assert.Equal(10, view.VisibleCount);
        view.SetGrid(4, 5);
        Assert.Equal(25, view.PageCount);
        Assert.Equal(500, view.ItemCount);
    }

    [Fact]
    public void Escape_ClosesGroupThenSearchThenWindow()
    {
        var view = new LauncherViewState(5, 7) { Query = "test", OpenGroupId = "group" };
        Assert.Equal(LauncherEscapeAction.CloseGroup, view.Escape());
        Assert.Equal(LauncherEscapeAction.ClearSearch, view.Escape());
        Assert.Equal(LauncherEscapeAction.HideWindow, view.Escape());
    }

    [Fact]
    public void SearchPage_DoesNotReplaceRememberedBrowsePage()
    {
        var view = new LauncherViewState(5, 7);
        view.SetItemCount(100);
        view.MoveToPage(2);
        view.Query = "a";
        view.SetItemCount(80);
        view.MoveToPage(1);
        view.Query = "";
        view.SetItemCount(100);
        Assert.Equal(2, view.Page);
    }
}
