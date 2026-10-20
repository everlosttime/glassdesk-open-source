namespace GlassDesk.Dock;

/// <summary>
/// 把导入结果合并进 Dock 的固定列表。
/// </summary>
/// <remarks>
/// <strong>重复条目也要参与固定。</strong>从资源管理器拖一个已经在应用库里的文件进来时，
/// 导入服务报告的是「重复 0、新增 1」——只看新增会让手势完全没有反应：
/// 用户把一条从 Dock 移出的应用再拖回来，界面上什么都不会发生
/// （条目本来就在库里，导入当然不新增）。
/// </remarks>
public static class DockPinMerge
{
    /// <summary>
    /// 计算把 <paramref name="added"/> 与 <paramref name="duplicates"/> 合并固定之后的新列表。
    /// </summary>
    /// <param name="pinned">当前固定列表，顺序保留。</param>
    /// <param name="added">本次新导入的条目 id。</param>
    /// <param name="duplicates">本次判定为「已在库中」的条目 id。</param>
    /// <returns>新列表；已固定的 id 不会被重复追加。</returns>
    public static List<string> Merge(
        IReadOnlyList<string> pinned,
        IEnumerable<string> added,
        IEnumerable<string> duplicates)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(duplicates);

        var merged = new List<string>(pinned);
        var seen = new HashSet<string>(merged, StringComparer.Ordinal);

        foreach (var id in added.Concat(duplicates))
        {
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
            {
                merged.Add(id);
            }
        }

        return merged;
    }

    /// <summary>本次合并真正新增的固定项数量（诊断与状态文字用）。</summary>
    public static int CountNewlyPinned(
        IReadOnlyList<string> pinned,
        IEnumerable<string> added,
        IEnumerable<string> duplicates)
        => Merge(pinned, added, duplicates).Count - pinned.Count;
}