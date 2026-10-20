namespace GlassDesk.Core.Models;

/// <summary>编辑分离的启动台布局，由调用方决定何时提交结果。</summary>
public static class LauncherLayoutEditor
{
    /// <summary>Place an icon at its final grid slot; preview callers retain an immutable drag origin.</summary>
    public static LauncherLayout PlaceAtSlot(LauncherLayout source, string slotId, int finalIndex, int capacity, bool exchange)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var slots = Flatten(source);
        var old = slots.FindIndex(slot => SlotId(slot) == slotId);
        if (old < 0) throw new InvalidOperationException("The dragged item no longer exists.");
        var target = Math.Clamp(finalIndex, 0, slots.Count - 1);
        if (exchange)
            (slots[old], slots[target]) = (slots[target], slots[old]);
        else
        {
            var moving = slots[old];
            slots.RemoveAt(old);
            slots.Insert(target, moving);
        }
        return Pack(slots, capacity);
    }

    public static LauncherLayout Move(LauncherLayout source, string slotId, int page, int index, int capacity)
    {
        var slots = Flatten(source);
        var old = slots.FindIndex(slot => SlotId(slot) == slotId);
        if (old < 0) throw new InvalidOperationException("The dragged item no longer exists.");
        var moving = slots[old];
        slots.RemoveAt(old);
        var requested = (page * capacity) + index;
        var target = Math.Clamp(requested > old ? requested - 1 : requested, 0, slots.Count);
        slots.Insert(target, moving);
        return Pack(slots, capacity);
    }

    /// <summary>Remove a top-level application or group from Launcher while preserving its library entries.</summary>
    public static LauncherLayout RemoveShortcut(LauncherLayout source, string slotId, int capacity)
    {
        var slots = Flatten(source);
        var index = slots.FindIndex(slot => SlotId(slot) == slotId);
        if (index >= 0)
        {
            slots.RemoveAt(index);
            return Pack(slots, capacity);
        }

        var groupIndex = slots.FindIndex(slot => slot.Group?.ItemIds.Contains(slotId, StringComparer.Ordinal) == true);
        if (groupIndex < 0) throw new InvalidOperationException("The dragged item no longer exists.");
        var group = slots[groupIndex].Group!;
        if (!group.ItemIds.Remove(slotId)) throw new InvalidOperationException("The dragged item no longer exists.");
        if (group.ItemIds.Count == 0)
            slots.RemoveAt(groupIndex);
        else if (group.ItemIds.Count == 1)
            slots[groupIndex] = new LauncherSlot { ItemId = group.ItemIds[0] };
        return Pack(slots, capacity);
    }

    public static LauncherLayout CreateGroup(LauncherLayout source, string movingId, string targetId, string groupId, int capacity)
    {
        if (movingId == targetId) throw new InvalidOperationException("An item cannot be grouped with itself.");
        var slots = Flatten(source);
        var moving = slots.SingleOrDefault(slot => slot.Kind == LauncherSlotKind.App && slot.ItemId == movingId);
        var target = slots.SingleOrDefault(slot => slot.Kind == LauncherSlotKind.App && slot.ItemId == targetId);
        if (moving is null || target is null) throw new InvalidOperationException("Both items must be top-level applications.");
        slots.Remove(moving);
        var targetIndex = slots.IndexOf(target);
        slots[targetIndex] = new LauncherSlot { Kind = LauncherSlotKind.Group, Group = new LauncherGroup
        { Id = groupId, Name = "新建文件夹", ItemIds = [targetId, movingId] } };
        return Pack(slots, capacity);
    }

    public static LauncherLayout AddToGroup(LauncherLayout source, string itemId, string groupId, int capacity)
    {
        var slots = Flatten(source);
        var group = slots.SingleOrDefault(slot => slot.Kind == LauncherSlotKind.Group && slot.Group?.Id == groupId)?.Group
            ?? throw new InvalidOperationException("Group not found.");
        var item = slots.SingleOrDefault(slot => slot.Kind == LauncherSlotKind.App && slot.ItemId == itemId)
            ?? throw new InvalidOperationException("Only an application can enter a group.");
        slots.Remove(item);
        if (!group.ItemIds.Contains(itemId, StringComparer.Ordinal)) group.ItemIds.Add(itemId);
        return Pack(slots, capacity);
    }

    public static LauncherLayout RemoveFromGroup(LauncherLayout source, string itemId, string groupId, int page, int index, int capacity)
    {
        var slots = Flatten(source);
        var group = slots.SingleOrDefault(slot => slot.Group?.Id == groupId)?.Group
            ?? throw new InvalidOperationException("Group not found.");
        if (!group.ItemIds.Remove(itemId)) throw new InvalidOperationException("Item not in group.");
        if (group.ItemIds.Count == 0) slots.RemoveAll(slot => slot.Group?.Id == groupId);
        else if (group.ItemIds.Count == 1)
            slots[slots.FindIndex(slot => slot.Group?.Id == groupId)] = new LauncherSlot { ItemId = group.ItemIds[0] };
        slots.Insert(Math.Clamp((page * capacity) + index, 0, slots.Count), new LauncherSlot { ItemId = itemId });
        return Pack(slots, capacity);
    }

    public static LauncherLayout Dissolve(LauncherLayout source, string groupId, int capacity)
    {
        var slots = Flatten(source);
        var index = slots.FindIndex(slot => slot.Group?.Id == groupId);
        if (index < 0) throw new InvalidOperationException("Group not found.");
        var members = slots[index].Group!.ItemIds.Select(id => new LauncherSlot { ItemId = id }).ToList();
        slots.RemoveAt(index);
        slots.InsertRange(index, members);
        return Pack(slots, capacity);
    }

    public static LauncherLayout MoveGroupMember(LauncherLayout source, string groupId, string itemId, int index)
    {
        var draft = LauncherLayoutRules.DeepClone(source);
        var group = draft.Pages.SelectMany(page => page.Slots).SingleOrDefault(slot => slot.Group?.Id == groupId)?.Group
            ?? throw new InvalidOperationException("Group not found.");
        if (!group.ItemIds.Remove(itemId)) throw new InvalidOperationException("Item not in group.");
        group.ItemIds.Insert(Math.Clamp(index, 0, group.ItemIds.Count), itemId);
        return draft;
    }

    public static LauncherLayout RenameGroup(LauncherLayout source, string groupId, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 60) throw new ArgumentException("Group name must be 1–60 characters.", nameof(name));
        var draft = LauncherLayoutRules.DeepClone(source);
        var group = draft.Pages.SelectMany(page => page.Slots).SingleOrDefault(slot => slot.Group?.Id == groupId)?.Group
            ?? throw new InvalidOperationException("Group not found.");
        group.Name = trimmed;
        return draft;
    }

    private static List<LauncherSlot> Flatten(LauncherLayout source)
        => LauncherLayoutRules.DeepClone(source).Pages.SelectMany(page => page.Slots).ToList();

    private static string? SlotId(LauncherSlot slot) => slot.Kind == LauncherSlotKind.Group ? slot.Group?.Id : slot.ItemId;

    public static LauncherLayout AddShortcuts(LauncherLayout source, IEnumerable<string> ids, int capacity)
    {
        var slots = Flatten(source);
        var existing = LauncherLayoutRules.AppIds(source).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
            if (existing.Add(id)) slots.Add(new LauncherSlot { ItemId = id });
        return Pack(slots, capacity);
    }

    private static LauncherLayout Pack(List<LauncherSlot> slots, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var result = new LauncherLayout();
        for (var offset = 0; offset < slots.Count; offset += capacity)
            result.Pages.Add(new LauncherPage { Id = $"page-{(offset / capacity) + 1}", Slots = slots.Skip(offset).Take(capacity).ToList() });
        result.SchemaVersion = 2;
        return result;
    }
}
