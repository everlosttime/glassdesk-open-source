namespace GlassDesk.Core.Models;

/// <summary>启动台有序单层布局的纯规则。</summary>
public static class LauncherLayoutRules
{
    public static bool Equivalent(LauncherLayout left, LauncherLayout right)
    {
        if (left.SchemaVersion != right.SchemaVersion) return false;
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
        var slots = new List<LauncherSlot>();
        foreach (var slot in (source.Pages ?? []).SelectMany(page => page.Slots ?? []))
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
        if (source.SchemaVersion < 2)
            foreach (var id in ids)
                if (!hidden.Contains(id) && seen.Add(id)) slots.Add(new LauncherSlot { Kind = LauncherSlotKind.App, ItemId = id });

        var pages = new List<LauncherPage>();
        for (var i = 0; i < slots.Count; i += capacity)
        {
            var oldId = (source.Pages ?? []).ElementAtOrDefault(pages.Count)?.Id;
            pages.Add(new LauncherPage
            {
                Id = string.IsNullOrWhiteSpace(oldId) ? $"page-{pages.Count + 1}" : oldId,
                Slots = slots.Skip(i).Take(capacity).ToList(),
            });
        }
        return new LauncherLayout { Pages = pages, SchemaVersion = 2 };
    }
}
