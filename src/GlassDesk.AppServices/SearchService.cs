using System.Text;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;

namespace GlassDesk.AppServices;

/// <summary>对共享应用库执行只读且结果稳定的搜索。</summary>
public sealed class SearchService : ISearchService
{
    private readonly Func<IEnumerable<AppEntry>> _entries;
    private readonly Func<LauncherLayout> _layout;

    public SearchService(Func<IEnumerable<AppEntry>> entries, Func<LauncherLayout> layout)
    {
        _entries = entries;
        _layout = layout;
    }

    public IReadOnlyList<SearchHit> SearchApps(string query, int limit)
    {
        var needle = Normalize(query);
        if (needle.Length == 0 || limit <= 0) return [];
        var ordered = LauncherLayoutRules.AppIds(_layout());
        var positions = ordered.Select((id, index) => (id, index))
            .GroupBy(pair => pair.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        return _entries().Where(item => item is { IsUsable: true })
            .Select(item => (item, kind: Match(item, needle)))
            .Where(pair => pair.kind is not null)
            .OrderBy(pair => pair.kind)
            .ThenBy(pair => positions.GetValueOrDefault(pair.item.Id, int.MaxValue))
            .ThenBy(pair => pair.item.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(pair => new SearchHit { Item = pair.item, MatchKind = pair.kind!.Value, Score = 100 - (int)pair.kind.Value })
            .ToList();
    }

    private static SearchMatchKind? Match(AppEntry item, string needle)
    {
        var name = Normalize(item.DisplayName);
        if (name == needle) return SearchMatchKind.ExactName;
        if (name.StartsWith(needle, StringComparison.Ordinal)) return SearchMatchKind.NamePrefix;
        var (full, initials) = OfflinePinyin.Index(item.DisplayName);
        if (full.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.Pinyin;
        if (initials.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.PinyinInitials;
        if (name.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.NameContains;
        if ((item.SearchAliases ?? []).Any(alias => Normalize(alias).Contains(needle, StringComparison.Ordinal)))
            return SearchMatchKind.Alias;
        return null;
    }

    private static string Normalize(string? text)
        => (text ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}
