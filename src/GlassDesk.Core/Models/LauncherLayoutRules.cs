namespace GlassDesk.Core.Models;

/// <summary>启动台有序单层布局的纯规则。</summary>
public static class LauncherLayoutRules
{
    public static bool Equivalent(LauncherLayout left, LauncherLayout right)
    {
        if (left.SchemaVersion != right.SchemaVersion) return false;
        if (!(left.FlatOrder ?? []).SequenceEqual(right.FlatOrder ?? [], StringComparer.Ordinal)) return false;
        var leftFilters = left.FilterGroups ?? [];
        var rightFilters = right.FilterGroups ?? [];
        if (leftFilters.Count != rightFilters.Count) return false;
        for (var i = 0; i < leftFilters.Count; i++)
            if (leftFilters[i].Id != rightFilters[i].Id || leftFilters[i].Name != rightFilters[i].Name
                || !(leftFilters[i].ItemIds ?? []).SequenceEqual(rightFilters[i].ItemIds ?? [], StringComparer.Ordinal)) return false;
        if (left.SchemaVersion < 2 && !(left.ExcludedAppIds ?? []).SequenceEqual(right.ExcludedAppIds ?? [], StringComparer.Ordinal)) return false;
        var leftPages = left.Pages ?? [];
        var rightPages = right.Pages ?? [];
        if (leftPages.Count != rightPages.Count) return false;
        for (var p = 0; p < leftPages.Count; p++)
            if (leftPages[p].Slots.Count != rightPages[p].Slots.Count) return false;
        var a = (left.Pages ?? []).SelectMany(page => page.Slots ?? []).ToList();
        var b = (right.Pages ?? []).SelectMany(page => page.Slots ?? []).ToList();
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != b[i].Kind || a[i].ItemId != b[i].ItemId) return false;
            var ga = a[i].Group;
            var gb = b[i].Group;
            if ((ga is null) != (gb is null)) return false;
            if (ga is not null && (ga.Id != gb!.Id || ga.Name != gb.Name || !ga.ItemIds.SequenceEqual(gb.ItemIds))) return false;
        }
        return true;
    }

    public static LauncherLayout DeepClone(LauncherLayout source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new LauncherLayout
        {
            SchemaVersion = source.SchemaVersion,
            ExcludedAppIds = [.. source.ExcludedAppIds ?? []],
            FlatOrder = [.. source.FlatOrder ?? []],
            FilterGroups = (source.FilterGroups ?? []).Select(group => new LauncherGroup
            { Id = group.Id, Name = group.Name, ItemIds = [.. group.ItemIds ?? []] }).ToList(),
            Pages = (source.Pages ?? []).Select(page => new LauncherPage
            {
                Id = page.Id,
                Slots = (page.Slots ?? []).Select(slot => new LauncherSlot
                {
                    Kind = slot.Kind,
                    ItemId = slot.ItemId,
                    Group = slot.Group is null ? null : new LauncherGroup
                    {
                        Id = slot.Group.Id,
                        Name = slot.Group.Name,
                        ItemIds = [.. slot.Group.ItemIds ?? []],
                    },
                }).ToList(),
            }).ToList()
        };
    }

    public static IReadOnlyList<string> AppIds(LauncherLayout layout)
        => (layout.Pages ?? []).SelectMany(page => page.Slots ?? [])
            .SelectMany(slot => slot.Kind == LauncherSlotKind.Group
                ? slot.Group?.ItemIds ?? []
                : string.IsNullOrWhiteSpace(slot.ItemId) ? [] : [slot.ItemId])
            .ToList();

    public static LauncherLayout Reconcile(LauncherLayout source, IEnumerable<string> libraryIds, int capacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(libraryIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        var ids = libraryIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToList();
        var valid = ids.ToHashSet(StringComparer.Ordinal);
        // Adopt the old visible layout once, then use explicit membership only.
        var excluded = (source.SchemaVersion < 2 ? source.ExcludedAppIds ?? [] : []).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToList();
        var hidden = excluded.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = new List<LauncherPage>();
        foreach (var sourcePage in source.Pages ?? [])
        {
            var slots = new List<LauncherSlot>();
            foreach (var slot in sourcePage.Slots ?? [])
            {
                if (slot.Kind == LauncherSlotKind.App)
                {
                    if (slot.ItemId is { } id && valid.Contains(id) && !hidden.Contains(id) && seen.Add(id))
                        slots.Add(new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = id });
                }
                else if (slot.Kind == LauncherSlotKind.Group && slot.Group is { } group)
                {
                    var members = (group.ItemIds ?? []).Where(id => valid.Contains(id) && !hidden.Contains(id) && seen.Add(id)).ToList();
                    if (members.Count == 1)
                        slots.Add(new LauncherSlot { ItemId = members[0] });
                    else if (members.Count > 1)
                        slots.Add(new LauncherSlot { Kind = LauncherSlotKind.Group, Group = new LauncherGroup
                        { Id = group.Id, Name = group.Name, ItemIds = members } });
                }
            }
            pages.Add(new LauncherPage
            {
                Id = string.IsNullOrWhiteSpace(sourcePage.Id) ? $"page-{pages.Count + 1}" : sourcePage.Id,
                Slots = slots,
            });
        }
        var result = LauncherLayoutEditor.Finish(new LauncherLayout { Pages = pages, SchemaVersion = 2 }, capacity);
        result = source.SchemaVersion < 2
            ? LauncherLayoutEditor.AddShortcuts(result, ids.Where(id => !hidden.Contains(id) && !seen.Contains(id)), capacity)
            : result;
        result.FlatOrder = [.. source.FlatOrder ?? []];
        result.FilterGroups = DeepClone(source).FilterGroups;
        LauncherFilterRules.Normalize(result);
        return result;
    }
}
