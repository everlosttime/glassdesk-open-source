using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class SearchServiceTests
{
    [Theory]
    [InlineData("启动台")]
    [InlineData("qidongtai")]
    [InlineData("qdt")]
    public void ChineseName_SearchesByNameFullPinyinAndInitials(string query)
    {
        var item = new AppEntry { Id = "a", DisplayName = "启动台", Target = "unused" };
        var search = new SearchService(() => [item], () => new LauncherLayout());
        Assert.Equal("a", Assert.Single(search.SearchApps(query, 500)).Item.Id);
    }

    [Fact]
    public void Search_UsesStableRankingAndAliases()
    {
        AppEntry[] entries =
        [
            new() { Id = "exact", DisplayName = "Code", Target = "a" },
            new() { Id = "prefix", DisplayName = "Code Editor", Target = "b" },
            new() { Id = "contains", DisplayName = "My Code", Target = "c" },
            new() { Id = "alias", DisplayName = "重庆工具", SearchAliases = ["code", "chongqing", "cq"], Target = "d" },
        ];
        var search = new SearchService(() => entries, () => new LauncherLayout());
        Assert.Equal(["exact", "prefix", "contains", "alias"], search.SearchApps("CODE", 500).Select(x => x.Item.Id));
        Assert.Equal("alias", Assert.Single(search.SearchApps("cq", 500)).Item.Id);
    }

    [Fact]
    public void Search_SeesRenamedEntryWithoutWritingTheLibrary()
    {
        var item = new AppEntry { Id = "a", DisplayName = "旧名称", Target = "unused" };
        var search = new SearchService(() => [item], () => new LauncherLayout());
        Assert.Single(search.SearchApps("旧名称", 500));
        item.DisplayName = "新名称";
        Assert.Empty(search.SearchApps("旧名称", 500));
        Assert.Single(search.SearchApps("新名称", 500));
    }
}
