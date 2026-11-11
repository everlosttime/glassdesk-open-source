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

    [Fact]
    public void RepeatedSearch_DoesNotRebuildPinyinForEveryUnchangedName()
    {
        var entries = Enumerable.Range(0, 200).Select(i => new AppEntry
        {
            Id = "app-" + i, DisplayName = "示例应用工具 Application " + i, Target = "unused",
        }).ToArray();
        var search = new SearchService(() => entries, () => new LauncherLayout());
        for (var i = 0; i < 10; i++) search.SearchApps("not-present", 500);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++) Assert.Empty(search.SearchApps("not-present", 500));
        var perQuery = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
        // Recomputing all rune strings/builders produces over 200 KB per query.
        // Allow normalization and normal query bookkeeping, with ample headroom.
        Assert.True(perQuery < 80_000, $"Repeated search allocated {perQuery} bytes/query.");
        Assert.All(entries, entry =>
        {
            Assert.Null(entry.PinyinIndex);
            Assert.Null(entry.PinyinInitials);
        });
    }

    [Fact]
    public void WarmSearch_ImmediatelySeesRenamedPinyinAndInPlaceAliasChanges()
    {
        var item = new AppEntry { Id = "a", DisplayName = "启动台", Target = "unused", SearchAliases = ["old-alias"] };
        var search = new SearchService(() => [item], () => new LauncherLayout());
        Assert.Single(search.SearchApps("qdt", 500));
        Assert.Single(search.SearchApps("old-alias", 500));
        item.DisplayName = "工具";
        item.SearchAliases[0] = "new-alias";
        Assert.Empty(search.SearchApps("qdt", 500));
        Assert.Empty(search.SearchApps("old-alias", 500));
        Assert.Single(search.SearchApps("gj", 500));
        Assert.Single(search.SearchApps("gongju", 500));
        Assert.Single(search.SearchApps("NEW-ALIAS", 500));
    }

    [Fact]
    public void WarmSearch_UsesCurrentEntriesLayoutAndLimit()
    {
        var items = new List<AppEntry>
        {
            new() { Id = "a", DisplayName = "工具甲", Target = "unused" },
            new() { Id = "b", DisplayName = "工具乙", Target = "unused" },
        };
        var layout = new LauncherLayout { Pages = [new() { Slots = [new() { ItemId = "b" }, new() { ItemId = "a" }] }] };
        var search = new SearchService(() => items, () => layout);
        Assert.Equal("b", Assert.Single(search.SearchApps("gj", 1)).Item.Id);
        layout.Pages[0].Slots.Reverse();
        Assert.Equal("a", Assert.Single(search.SearchApps("gj", 1)).Item.Id);
        var replacement = new AppEntry { Id = "a", DisplayName = "工具甲", Target = "replacement" };
        items[0] = replacement;
        Assert.Same(replacement, search.SearchApps("gj", 2)[0].Item);
        items.RemoveAt(1);
        Assert.Same(replacement, Assert.Single(search.SearchApps("gj", 500)).Item);
    }

    [Fact]
    public void Search_StillFindsOldNamesAfterManyDifferentNames()
    {
        var item = new AppEntry { Id = "a", DisplayName = "启动台", Target = "unused" };
        var search = new SearchService(() => [item], () => new LauncherLayout());
        Assert.Single(search.SearchApps("qdt", 1));
        for (var i = 0; i < 1200; i++)
        {
            item.DisplayName = "应用工具" + i;
            Assert.Single(search.SearchApps("gj", 1));
        }
        item.DisplayName = "启动台";
        Assert.Single(search.SearchApps("qdt", 1));
        item.DisplayName = new string('工', 140_000);
        Assert.Single(search.SearchApps("ggg", 1));
        item.DisplayName = "启动台";
        Assert.Single(search.SearchApps("qdt", 1));
    }
}
