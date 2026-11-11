namespace GlassDesk.Core.Models;

/// <summary>平铺排列和筛选分组独立于原来的文件夹布局。</summary>
public static class LauncherFilterRules
{
    private static List<string> OrderedIds(LauncherLayout layout)
    {
        var ids = LauncherLayoutRules.AppIds(layout).Distinct(StringComparer.Ordinal).ToList();
        var valid = ids.ToHashSet(StringComparer.Ordinal);
        return (layout.FlatOrder ?? []).Where(valid.Contains).Concat(ids).Distinct(StringComparer.Ordinal).ToList();
    }

    public static LauncherLayout Project(LauncherLayout source, string? groupId, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        IEnumerable<string> ids = OrderedIds(source);
        if (groupId is not null)
        {
            var members = (source.FilterGroups?.FirstOrDefault(group => group.Id == groupId)?.ItemIds ?? [])
                .ToHashSet(StringComparer.Ordinal);
            ids = ids.Where(members.Contains);
        }
        return Pack(ids, capacity);
    }

    /// <summary>Flat pages fill earlier gaps so a cross-page drop keeps its requested grid position after saving.</summary>
    public static LauncherLayout PlaceAtSlot(LauncherLayout projection, string id, int finalIndex, int capacity, bool exchange)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var ids = LauncherLayoutRules.AppIds(projection).ToList();
        var origin = ids.IndexOf(id);
        if (origin < 0) throw new InvalidOperationException("拖动的图标已不存在。");
        var target = Math.Clamp(finalIndex, 0, ids.Count - 1);
        if (exchange) (ids[origin], ids[target]) = (ids[target], ids[origin]);
        else { ids.RemoveAt(origin); ids.Insert(target, id); }
        return Pack(ids, capacity);
    }

    private static LauncherLayout Pack(IEnumerable<string> ids, int capacity)
        => new()
        {
            SchemaVersion = 2,
            Pages = ids.Chunk(capacity).Select((chunk, index) => new LauncherPage
            { Id = $"flat-{index}", Slots = chunk.Select(id => new LauncherSlot { ItemId = id }).ToList() }).ToList(),
        };

    /// <summary>Apply a flat drag without replacing the original folder tree or unrelated group members.</summary>
    public static LauncherLayout ApplyProjection(LauncherLayout source, LauncherLayout projection, string? groupId, int capacity)
    {
        var before = LauncherLayoutRules.AppIds(Project(source, groupId, capacity)).ToHashSet(StringComparer.Ordinal);
        var after = LauncherLayoutRules.AppIds(projection).ToList();
        if (after.Count != after.Distinct(StringComparer.Ordinal).Count() || after.Any(id => !before.Contains(id))
            || projection.Pages.SelectMany(page => page.Slots).Any(slot => slot.Kind != LauncherSlotKind.App))
            throw new InvalidOperationException("平铺整理包含无效的图标，请重试。");
        var result = LauncherLayoutRules.DeepClone(source);
        var remaining = after.ToHashSet(StringComparer.Ordinal);
        foreach (var removed in before.Where(id => !remaining.Contains(id)))
            result = LauncherLayoutEditor.RemoveShortcut(result, removed, capacity);
        var order = OrderedIds(result);
        var replacements = new Queue<string>(after);
        result.FlatOrder = order.Select(id => before.Contains(id) ? replacements.Dequeue() : id).ToList();
        Normalize(result);
        return result;
    }

    public static LauncherLayout AddGroup(LauncherLayout source, string id, string name, IEnumerable<string> itemIds)
    {
        var title = ValidName(name);
        if (string.IsNullOrWhiteSpace(id) || (source.FilterGroups ?? []).Any(group => group.Id == id))
            throw new ArgumentException("分组标识已存在，请重试。", nameof(id));
        var members = LauncherLayoutRules.AppIds(source).ToHashSet(StringComparer.Ordinal);
        var selected = itemIds.Where(members.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count == 0) throw new ArgumentException("请至少选择一个图标。", nameof(itemIds));
        var result = LauncherLayoutRules.DeepClone(source);
        result.FilterGroups.Add(new() { Id = id, Name = title, ItemIds = selected });
        return result;
    }

    public static LauncherLayout RenameGroup(LauncherLayout source, string id, string name)
    {
        var title = ValidName(name);
        var result = LauncherLayoutRules.DeepClone(source);
        var group = result.FilterGroups.FirstOrDefault(group => group.Id == id)
            ?? throw new InvalidOperationException("分组已不存在，请重试。");
        group.Name = title;
        return result;
    }

    public static LauncherLayout UpdateGroupMembers(LauncherLayout source, string id, IEnumerable<string> itemIds)
    {
        var result = LauncherLayoutRules.DeepClone(source);
        var group = result.FilterGroups.FirstOrDefault(group => group.Id == id)
            ?? throw new InvalidOperationException("分组已不存在，请重试。");
        var members = LauncherLayoutRules.AppIds(source).ToHashSet(StringComparer.Ordinal);
        group.ItemIds = itemIds.Where(members.Contains).Distinct(StringComparer.Ordinal).ToList();
        return result;
    }

    public static LauncherLayout RemoveGroup(LauncherLayout source, string id)
    {
        var result = LauncherLayoutRules.DeepClone(source);
        result.FilterGroups.RemoveAll(group => group.Id == id);
        return result;
    }

    private static string ValidName(string name)
    {
        var title = name.Trim();
        if (title.Length is < 1 or > 60) throw new ArgumentException("分组名称应为 1–60 个字符。", nameof(name));
        return title;
    }

    public static void Normalize(LauncherLayout layout)
    {
        var members = LauncherLayoutRules.AppIds(layout).ToHashSet(StringComparer.Ordinal);
        // Do not create a second order for users who have never used group mode.
        layout.FlatOrder = (layout.FlatOrder ?? []).Where(members.Contains).Distinct(StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        layout.FilterGroups = (layout.FilterGroups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group.Id) && seen.Add(group.Id)).ToList();
        foreach (var group in layout.FilterGroups)
            group.ItemIds = (group.ItemIds ?? []).Where(members.Contains).Distinct(StringComparer.Ordinal).ToList();
    }
}
