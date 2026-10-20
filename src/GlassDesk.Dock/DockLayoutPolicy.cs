namespace GlassDesk.Dock;

/// <summary>
/// Dock 布局里「什么能写、什么不能写」的唯一裁决者。
/// </summary>
/// <remarks>
/// <para>
/// <b>内置入口不是应用库条目，绝不能进布局文件。</b>
/// 启动台、显示桌面、回收站三个磁贴属于 Dock 自身，任何时刻都在，位置也由 Dock 决定；
/// 而 <c>dock-layout.json</c> 的 <c>PinnedItemIds</c> 记的是「用户固定了哪些应用、什么顺序」。
/// 早先的排序把整行磁贴（含三个入口）原样写回布局，于是布局文件里混进了
/// <c>entry:launcher</c> 这类伪 id——它们在下一次加载时找不到对应条目而被跳过，
/// 看起来「没事」，但布局文件已经不再表示它该表示的东西，去重与顺序语义都被污染。
/// </para>
/// <para>
/// 三个内置入口用固定前缀标识（<see cref="EntryTilePrefix"/>），与库条目 id 天然不冲突。
/// </para>
/// </remarks>
public static class DockLayoutPolicy
{
    /// <summary>内置入口磁贴的 id 前缀。它们不是应用库条目。</summary>
    public const string EntryTilePrefix = "entry:";

    /// <summary>这个 id 是不是内置入口磁贴。</summary>
    public static bool IsEntryTile(string? itemId)
        => itemId is not null && itemId.StartsWith(EntryTilePrefix, StringComparison.Ordinal);

    /// <summary>
    /// 把一份固定列表清洗成可以写进布局的形式：**只保留应用库里的真实条目**，
    /// 去掉内置入口与找不到的 id，去重，并保持原顺序。
    /// </summary>
    /// <param name="pinned">待清洗的固定列表。</param>
    /// <param name="isLibraryEntry">
    /// 判断一个 id 是否存在于应用库。调用方在库**尚未加载**时应先放弃清洗
    /// （传一个恒为 true 的实现或干脆不调用），否则会把整份布局当成陌生 id 清空。
    /// </param>
    /// <returns>清洗后的列表；与输入等价时返回的列表内容相同（调用方据此决定要不要写盘）。</returns>
    public static List<string> Sanitize(IEnumerable<string> pinned, Func<string, bool> isLibraryEntry)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(isLibraryEntry);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in pinned)
        {
            if (string.IsNullOrWhiteSpace(id) || IsEntryTile(id))
            {
                continue;
            }

            if (!isLibraryEntry(id) || !seen.Add(id))
            {
                continue;
            }

            result.Add(id);
        }

        return result;
    }

    /// <summary>两份固定列表的内容是否完全一致（顺序敏感）。</summary>
    public static bool SameOrder(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}