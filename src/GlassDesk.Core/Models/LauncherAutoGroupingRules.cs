namespace GlassDesk.Core.Models;

/// <summary>Classifies ungrouped apps and manages their automatic group membership.</summary>
public static class LauncherAutoGroupingRules
{
    public static LauncherLayout ApplyClassification(LauncherLayout source, string itemId,
        LauncherClassificationResult classification, bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(classification);
        var result = LauncherLayoutRules.DeepClone(source);
        if (!LauncherLayoutRules.AppIds(result).Contains(itemId)) return result;
        var state = result.AutoClassifications.FirstOrDefault(item => item.ItemId == itemId);
        if (state is { IsManualOverride: true } || (state is not null && !replaceExisting)) return result;
        if (result.FilterGroups.Any(group => group.Id != state?.AutomaticGroupId && group.ItemIds.Contains(itemId)))
            return result;
        state = GetState(result, itemId);
        RemoveOwnedMembership(result, state);
        var categoryId = LauncherCategoryCatalog.Find(classification.CategoryId)?.Id ?? LauncherCategoryCatalog.Uncategorized;
        if (result.DisabledAutomaticCategoryIds.Contains(categoryId)) categoryId = LauncherCategoryCatalog.Uncategorized;
        state.CategoryId = categoryId;
        state.RuleVersion = classification.RuleVersion;
        if (result.DisabledAutomaticCategoryIds.Contains(categoryId)) return result;
        var group = EnsureCategoryGroup(result, categoryId);
        if (!group.ItemIds.Contains(itemId)) group.ItemIds.Add(itemId);
        state.AutomaticGroupId = group.Id;
        return result;
    }

    public static LauncherLayout SetManualCategory(LauncherLayout source, string itemId, string categoryId)
    {
        if (LauncherCategoryCatalog.Find(categoryId) is null) throw new ArgumentException("分类不存在。", nameof(categoryId));
        var result = LockManual(source, itemId);
        AssignManualGroup(result, itemId, EnsureCategoryGroup(result, categoryId));
        return result;
    }

    public static LauncherLayout SetManualGroup(LauncherLayout source, string itemId, string groupId)
    {
        var result = LockManual(source, itemId);
        var group = result.FilterGroups.FirstOrDefault(item => item.Id == groupId)
            ?? throw new InvalidOperationException("分组已不存在，请重试。");
        AssignManualGroup(result, itemId, group);
        return result;
    }

    private static void AssignManualGroup(LauncherLayout layout, string itemId, LauncherGroup group)
    {
        foreach (var other in layout.FilterGroups.Where(other => other != group))
            other.ItemIds.RemoveAll(id => id == itemId);
        if (!group.ItemIds.Contains(itemId)) group.ItemIds.Add(itemId);
        var state = GetState(layout, itemId);
        state.CategoryId = group.CategoryId;
        state.AutomaticGroupId = group.CategoryId is null ? null : group.Id;
    }

    public static LauncherLayout LockManual(LauncherLayout source, string itemId)
    {
        var result = LauncherLayoutRules.DeepClone(source);
        if (!LauncherLayoutRules.AppIds(result).Contains(itemId)) throw new InvalidOperationException("图标已不在启动台中，请重试。");
        var state = GetState(result, itemId);
        RemoveOwnedMembership(result, state);
        state.CategoryId = null;
        state.IsManualOverride = true;
        return result;
    }

    public static LauncherLayout MarkManualGroupEdit(LauncherLayout source, IEnumerable<string> beforeMembers, IEnumerable<string> afterMembers)
    {
        var result = LauncherLayoutRules.DeepClone(source);
        var changed = beforeMembers.ToHashSet(StringComparer.Ordinal);
        changed.SymmetricExceptWith(afterMembers);
        var valid = LauncherLayoutRules.AppIds(result).ToHashSet(StringComparer.Ordinal);
        foreach (var id in changed.Where(valid.Contains)) GetState(result, id).IsManualOverride = true;
        Normalize(result);
        return result;
    }

    public static LauncherLayout RestoreAutomaticGroups(LauncherLayout source)
    {
        var result = LauncherLayoutRules.DeepClone(source);
        result.DisabledAutomaticCategoryIds.Clear();
        return result;
    }

    internal static void Normalize(LauncherLayout layout)
    {
        var valid = LauncherLayoutRules.AppIds(layout).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        layout.AutoClassifications = (layout.AutoClassifications ?? [])
            .Where(item => valid.Contains(item.ItemId) && seen.Add(item.ItemId)).ToList();
        layout.DisabledAutomaticCategoryIds = (layout.DisabledAutomaticCategoryIds ?? [])
            .Where(id => LauncherCategoryCatalog.Find(id) is not null).Distinct(StringComparer.Ordinal).ToList();
        layout.SuppressedAutoAddKeys = (layout.SuppressedAutoAddKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var state in layout.AutoClassifications)
            if (state.AutomaticGroupId is { } groupId && !layout.FilterGroups.Any(group => group.Id == groupId && group.ItemIds.Contains(state.ItemId)))
                state.AutomaticGroupId = null;
    }

    private static LauncherAutoClassification GetState(LauncherLayout layout, string itemId)
    {
        var state = layout.AutoClassifications.FirstOrDefault(item => item.ItemId == itemId);
        if (state is not null) return state;
        state = new() { ItemId = itemId };
        layout.AutoClassifications.Add(state);
        return state;
    }

    private static void RemoveOwnedMembership(LauncherLayout layout, LauncherAutoClassification state)
    {
        if (state.AutomaticGroupId is { } id)
            layout.FilterGroups.FirstOrDefault(group => group.Id == id)?.ItemIds.Remove(state.ItemId);
        state.AutomaticGroupId = null;
    }

    private static LauncherGroup EnsureCategoryGroup(LauncherLayout layout, string categoryId)
    {
        var existing = layout.FilterGroups.FirstOrDefault(group => group.CategoryId == categoryId);
        if (existing is not null) return existing;
        var category = LauncherCategoryCatalog.Find(categoryId)!;
        var name = category.Name;
        if (layout.FilterGroups.Any(group => group.Name == name)) name += "（自动）";
        var baseId = "auto-" + categoryId;
        var id = baseId;
        var suffix = 2;
        while (layout.FilterGroups.Any(group => group.Id == id)) id = baseId + "-" + suffix++;
        var created = new LauncherGroup { Id = id, Name = name, CategoryId = categoryId };
        layout.FilterGroups.Add(created);
        return created;
    }
}
