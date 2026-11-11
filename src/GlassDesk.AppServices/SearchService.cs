using System.Text;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;

namespace GlassDesk.AppServices;

/// <summary>对共享应用库执行只读且结果稳定的搜索。</summary>
public sealed class SearchService : ISearchService
{
    private readonly Func<IEnumerable<AppEntry>> _entries;
    private readonly Func<LauncherLayout> _layout;
    // Store derived strings only, never entries or search results. Rename, alias,
    // membership and layout changes therefore remain visible on the next query.
    private const int MaximumPinyinNames = 512;
    private const int MaximumPinyinCharacters = 128 * 1024;
    private readonly Lock _pinyinGate = new();
    private readonly Dictionary<string, LinkedListNode<PinyinText>> _pinyin = new(StringComparer.Ordinal);
    private readonly LinkedList<PinyinText> _pinyinLru = new();
    private int _pinyinCharacters;
    private sealed record PinyinText(string Name, string Full, string Initials)
    {
        internal long Characters => (long)Name.Length + Full.Length + Initials.Length;
    }

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

    private SearchMatchKind? Match(AppEntry item, string needle)
    {
        var name = Normalize(item.DisplayName);
        if (name == needle) return SearchMatchKind.ExactName;
        if (name.StartsWith(needle, StringComparison.Ordinal)) return SearchMatchKind.NamePrefix;
        var (full, initials) = PinyinFor(item.DisplayName);
        if (full.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.Pinyin;
        if (initials.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.PinyinInitials;
        if (name.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.NameContains;
        if ((item.SearchAliases ?? []).Any(alias => Normalize(alias).Contains(needle, StringComparison.Ordinal)))
            return SearchMatchKind.Alias;
        return null;
    }

    private (string Full, string Initials) PinyinFor(string? text)
    {
        var name = text ?? string.Empty;
        lock (_pinyinGate)
        {
            if (_pinyin.TryGetValue(name, out var node))
            {
                _pinyinLru.Remove(node);
                _pinyinLru.AddLast(node);
                return (node.Value.Full, node.Value.Initials);
            }

            var index = OfflinePinyin.Index(name);
            var derived = new PinyinText(name, index.Full, index.Initials);
            // Oversized names can still be searched, but never retained in the cache.
            if (derived.Characters > MaximumPinyinCharacters) return index;
            while (_pinyin.Count >= MaximumPinyinNames
                || _pinyinCharacters + derived.Characters > MaximumPinyinCharacters)
            {
                var oldest = _pinyinLru.First!;
                _pinyinLru.RemoveFirst();
                _pinyin.Remove(oldest.Value.Name);
                _pinyinCharacters -= (int)oldest.Value.Characters;
            }
            _pinyin.Add(name, _pinyinLru.AddLast(derived));
            _pinyinCharacters += (int)derived.Characters;
            return index;
        }
    }

    private static string Normalize(string? text)
        => (text ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}
