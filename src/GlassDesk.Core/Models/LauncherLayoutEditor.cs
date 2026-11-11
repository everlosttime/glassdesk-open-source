namespace GlassDesk.Core.Models;

/// <summary>Edits detached launcher pages; holes compact within their own page only.</summary>
public static class LauncherLayoutEditor
{
    /// <summary>finalIndex encodes a grid coordinate (page * capacity + slot), including a new trailing page.</summary>
    public static LauncherLayout PlaceAtSlot(LauncherLayout source, string slotId, int finalIndex, int capacity, bool exchange)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var draft = LauncherLayoutRules.DeepClone(source);
        var (origin, old) = Find(draft, slotId);
        var page = Math.Clamp(finalIndex / capacity, 0, draft.Pages.Count);
        var index = Math.Max(0, finalIndex % capacity);
        EnsurePage(draft, page);
        var target = draft.Pages[page];
        var moving = origin.Slots[old];
        if (exchange && index < target.Slots.Count)
            (origin.Slots[old], target.Slots[index]) = (target.Slots[index], moving);
        else
        {
            origin.Slots.RemoveAt(old);
            target.Slots.Insert(Math.Min(index, target.Slots.Count), moving);
        }
        return Finish(draft, capacity);
    }

    public static LauncherLayout Move(LauncherLayout source, string slotId, int page, int index, int capacity)
    {
        var (origin, old) = Find(source, slotId);
        if (source.Pages.IndexOf(origin) == page && index > old) index--;
        return PlaceAtSlot(source, slotId, Math.Max(0, page) * capacity + Math.Max(0, index), capacity, false);
    }

    public static LauncherLayout RemoveShortcut(LauncherLayout source, string slotId, int capacity)
    {
        var draft = LauncherLayoutRules.DeepClone(source);
        foreach (var page in draft.Pages)
        {
            var index = page.Slots.FindIndex(slot => SlotId(slot) == slotId);
            if (index >= 0) { page.Slots.RemoveAt(index); return Finish(draft, capacity); }
            var groupSlot = page.Slots.FirstOrDefault(slot => slot.Group?.ItemIds.Contains(slotId, StringComparer.Ordinal) == true);
            if (groupSlot?.Group is not { } group) continue;
            group.ItemIds.Remove(slotId);
            CollapseGroup(page, groupSlot);
            return Finish(draft, capacity);
        }
        throw new InvalidOperationException("The dragged item no longer exists.");
    }

    public static LauncherLayout CreateGroup(LauncherLayout source, string movingId, string targetId, string groupId, int capacity)
    {
        if (movingId == targetId) throw new InvalidOperationException("An item cannot be grouped with itself.");
        var draft = LauncherLayoutRules.DeepClone(source);
        var (origin, old) = Find(draft, movingId);
        var (targetPage, targetIndex) = Find(draft, targetId);
        var moving = origin.Slots[old]; var target = targetPage.Slots[targetIndex];
        if (moving.Kind != LauncherSlotKind.App || target.Kind != LauncherSlotKind.App)
            throw new InvalidOperationException("Both items must be top-level applications.");
        origin.Slots.Remove(moving);
        targetPage.Slots[targetPage.Slots.IndexOf(target)] = new LauncherSlot { Kind = LauncherSlotKind.Group,
            Group = new() { Id = groupId, Name = "新建文件夹", ItemIds = [targetId, movingId] } };
        return Finish(draft, capacity);
    }

    public static LauncherLayout AddToGroup(LauncherLayout source, string itemId, string groupId, int capacity)
    {
        var draft = LauncherLayoutRules.DeepClone(source);
        var (origin, old) = Find(draft, itemId);
        var group = FindGroup(draft, groupId).Slot.Group!;
        if (origin.Slots[old].Kind != LauncherSlotKind.App) throw new InvalidOperationException("Only an application can enter a group.");
        origin.Slots.RemoveAt(old);
        if (!group.ItemIds.Contains(itemId, StringComparer.Ordinal)) group.ItemIds.Add(itemId);
        return Finish(draft, capacity);
    }

    public static LauncherLayout RemoveFromGroup(LauncherLayout source, string itemId, string groupId, int page, int index, int capacity)
    {
        var draft = LauncherLayoutRules.DeepClone(source);
        var (groupPage, slot) = FindGroup(draft, groupId);
        if (!slot.Group!.ItemIds.Remove(itemId)) throw new InvalidOperationException("Item not in group.");
        CollapseGroup(groupPage, slot);
        page = Math.Clamp(page, 0, draft.Pages.Count);
        EnsurePage(draft, page);
        draft.Pages[page].Slots.Insert(Math.Clamp(index, 0, draft.Pages[page].Slots.Count), new() { ItemId = itemId });
        return Finish(draft, capacity);
    }

    public static LauncherLayout Dissolve(LauncherLayout source, string groupId, int capacity)
    {
        var draft = LauncherLayoutRules.DeepClone(source);
        var (page, slot) = FindGroup(draft, groupId);
        var index = page.Slots.IndexOf(slot);
        page.Slots.RemoveAt(index);
        page.Slots.InsertRange(index, slot.Group!.ItemIds.Select(id => new LauncherSlot { ItemId = id }));
        return Finish(draft, capacity);
    }

    public static LauncherLayout MoveGroupMember(LauncherLayout source, string groupId, string itemId, int index)
    {
        var draft = LauncherLayoutRules.DeepClone(source);
        var group = FindGroup(draft, groupId).Slot.Group!;
        if (!group.ItemIds.Remove(itemId)) throw new InvalidOperationException("Item not in group.");
        group.ItemIds.Insert(Math.Clamp(index, 0, group.ItemIds.Count), itemId);
        return draft;
    }

    public static LauncherLayout RenameGroup(LauncherLayout source, string groupId, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 60) throw new ArgumentException("Group name must be 1–60 characters.", nameof(name));
        var draft = LauncherLayoutRules.DeepClone(source);
        FindGroup(draft, groupId).Slot.Group!.Name = trimmed;
        return draft;
    }

    public static LauncherLayout AddShortcuts(LauncherLayout source, IEnumerable<string> ids, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var draft = LauncherLayoutRules.DeepClone(source);
        var existing = LauncherLayoutRules.AppIds(source).ToHashSet(StringComparer.Ordinal);
        EnsurePage(draft, 0);
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (!existing.Add(id)) continue;
            if (draft.Pages[^1].Slots.Count >= capacity) EnsurePage(draft, draft.Pages.Count);
            draft.Pages[^1].Slots.Add(new() { ItemId = id });
        }
        return Finish(draft, capacity);
    }

    private static string? SlotId(LauncherSlot slot) => slot.Kind == LauncherSlotKind.Group ? slot.Group?.Id : slot.ItemId;

    private static (LauncherPage Page, int Index) Find(LauncherLayout layout, string id)
    {
        foreach (var page in layout.Pages)
        {
            var index = page.Slots.FindIndex(slot => SlotId(slot) == id);
            if (index >= 0) return (page, index);
        }
        throw new InvalidOperationException("The dragged item no longer exists.");
    }

    private static (LauncherPage Page, LauncherSlot Slot) FindGroup(LauncherLayout layout, string id)
    {
        var (page, index) = Find(layout, id);
        if (page.Slots[index].Group is null) throw new InvalidOperationException("Group not found.");
        return (page, page.Slots[index]);
    }

    private static void CollapseGroup(LauncherPage page, LauncherSlot slot)
    {
        if (slot.Group!.ItemIds.Count == 0) page.Slots.Remove(slot);
        else if (slot.Group.ItemIds.Count == 1)
            page.Slots[page.Slots.IndexOf(slot)] = new() { ItemId = slot.Group.ItemIds[0] };
    }

    private static void EnsurePage(LauncherLayout layout, int index)
    {
        while (layout.Pages.Count <= index)
            layout.Pages.Add(new() { Id = "page-" + Guid.NewGuid().ToString("N") });
    }

    /// <summary>Overflow travels forward; underfilled pages never borrow from following pages.</summary>
    internal static LauncherLayout Finish(LauncherLayout layout, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        for (var p = 0; p < layout.Pages.Count; p++)
        {
            var slots = layout.Pages[p].Slots;
            if (slots.Count <= capacity) continue;
            EnsurePage(layout, p + 1);
            layout.Pages[p + 1].Slots.InsertRange(0, slots.Skip(capacity));
            slots.RemoveRange(capacity, slots.Count - capacity);
        }
        while (layout.Pages.Count > 0 && layout.Pages[^1].Slots.Count == 0) layout.Pages.RemoveAt(layout.Pages.Count - 1);
        layout.SchemaVersion = 2;
        LauncherFilterRules.Normalize(layout);
        return layout;
    }
}
