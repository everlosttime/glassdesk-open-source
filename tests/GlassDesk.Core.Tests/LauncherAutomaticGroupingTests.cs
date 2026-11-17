using System.Text.Json;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class LauncherAutomaticGroupingTests
{
    private static LauncherLayout Apps() => new()
    {
        SchemaVersion = 2,
        Pages = [new() { Id = "p", Slots = [new() { ItemId = "editor" }, new() { ItemId = "chat" }] }],
        FlatOrder = ["chat", "editor"],
        FilterGroups = [new() { Id = "manual", Name = "开发工具", ItemIds = ["editor"] }],
    };

    [Fact]
    public void LayoutEditsPreserveAutomaticCategoryOwnershipAndDisabledCategories()
    {
        var source = JsonSerializer.Deserialize<LauncherLayout>("""
            {"SchemaVersion":2,"Pages":[{"Id":"p","Slots":[{"ItemId":"editor"}]}],
             "FilterGroups":[{"Id":"auto-development","Name":"开发工具","CategoryId":"development","ItemIds":["editor"]}],
             "AutoClassifications":[{"ItemId":"editor","CategoryId":"development","AutomaticGroupId":"auto-development","IsManualOverride":true,"RuleVersion":1}],
             "DisabledAutomaticCategoryIds":["games"]}
            """)!;

        var edited = LauncherFilterRules.RenameGroup(source, "auto-development", "写代码");
        var json = JsonSerializer.SerializeToElement(edited);

        Assert.True(json.GetProperty("FilterGroups")[0].TryGetProperty("CategoryId", out var category));
        Assert.Equal("development", category.GetString());
        Assert.True(json.TryGetProperty("AutoClassifications", out var assignments));
        Assert.True(assignments[0].GetProperty("IsManualOverride").GetBoolean());
        Assert.True(json.TryGetProperty("DisabledAutomaticCategoryIds", out var disabled));
        Assert.Equal("games", disabled[0].GetString());
    }

    [Fact]
    public void ClassificationCreatesDistinctAutomaticGroupAndPreservesManualMembershipAndOrder()
    {
        var source = Apps();
        var result = LauncherAutoGroupingRules.ApplyClassification(source, "editor", new("development", "known executable"));
        var automatic = Assert.Single(result.FilterGroups, group => group.CategoryId == "development");
        Assert.Equal("开发工具（自动）", automatic.Name);
        Assert.Equal(["editor"], automatic.ItemIds);
        Assert.Equal(["editor"], result.FilterGroups.Single(group => group.Id == "manual").ItemIds);
        Assert.Equal(["chat", "editor"], result.FlatOrder);
        Assert.Single(source.FilterGroups);
        Assert.True(LauncherLayoutRules.Equivalent(result,
            LauncherAutoGroupingRules.ApplyClassification(result, "editor", new("games", "new rules"))));
    }

    [Fact]
    public void ExplicitManualChoiceReplacesOnlyOwnedMembershipAndSurvivesReclassification()
    {
        var classified = LauncherAutoGroupingRules.ApplyClassification(Apps(), "editor", new("development", "code"));
        var manual = LauncherAutoGroupingRules.SetManualCategory(classified, "editor", "office-study");
        Assert.Empty(manual.FilterGroups.Single(group => group.CategoryId == "development").ItemIds);
        Assert.Equal(["editor"], manual.FilterGroups.Single(group => group.CategoryId == "office-study").ItemIds);
        Assert.Equal(["editor"], manual.FilterGroups.Single(group => group.Id == "manual").ItemIds);
        Assert.True(Assert.Single(manual.AutoClassifications).IsManualOverride);
        var reapplied = LauncherAutoGroupingRules.ApplyClassification(manual, "editor", new("games", "rules"), true);
        Assert.True(LauncherLayoutRules.Equivalent(manual, reapplied));
    }

    [Fact]
    public void EditingAutomaticGroupLocksRemovedMembersAndDoesNotReAddThem()
    {
        var classified = LauncherAutoGroupingRules.ApplyClassification(Apps(), "editor", new("development", "code"));
        var group = classified.FilterGroups.Single(item => item.CategoryId == "development");
        var edited = LauncherFilterRules.UpdateGroupMembers(classified, group.Id, []);
        Assert.True(Assert.Single(edited.AutoClassifications).IsManualOverride);
        Assert.Null(Assert.Single(edited.AutoClassifications).AutomaticGroupId);
        Assert.True(LauncherLayoutRules.Equivalent(edited,
            LauncherAutoGroupingRules.ApplyClassification(edited, "editor", new("development", "code"), true)));
    }

    [Fact]
    public void DeletedCategoryFallsBackWithoutResurrectionAndRestoreOnlyEnablesFutureAssignments()
    {
        var classified = LauncherAutoGroupingRules.ApplyClassification(Apps(), "editor", new("development", "code"));
        var group = classified.FilterGroups.Single(item => item.CategoryId == "development");
        var deleted = LauncherFilterRules.RemoveGroup(classified, group.Id);
        Assert.Contains("development", deleted.DisabledAutomaticCategoryIds);
        var next = LauncherAutoGroupingRules.ApplyClassification(deleted, "chat", new("development", "code"));
        Assert.DoesNotContain(next.FilterGroups, item => item.CategoryId == "development");
        var pending = Assert.Single(next.FilterGroups, item => item.CategoryId == "uncategorized");
        Assert.Equal(["chat"], pending.ItemIds);
        var restored = LauncherAutoGroupingRules.RestoreAutomaticGroups(next);
        Assert.Empty(restored.DisabledAutomaticCategoryIds);
        Assert.Equal(next.FilterGroups.Select(item => item.Id), restored.FilterGroups.Select(item => item.Id));
        var noPending = LauncherFilterRules.RemoveGroup(next, pending.Id);
        var onlyAll = LauncherAutoGroupingRules.ApplyClassification(noPending, "editor", new("development", "code"), true);
        Assert.DoesNotContain(onlyAll.FilterGroups, item => item.CategoryId is not null);
    }

    [Fact]
    public void OldLayoutsHaveNoAutomaticStateAndDetachedClonesDoNotShareMetadata()
    {
        var old = JsonSerializer.Deserialize<LauncherLayout>("{\"SchemaVersion\":2,\"Pages\":[]}")!;
        Assert.Empty(old.AutoClassifications);
        Assert.Empty(old.DisabledAutomaticCategoryIds);
        var classified = LauncherAutoGroupingRules.ApplyClassification(Apps(), "editor", new("development", "code"));
        var clone = LauncherLayoutRules.DeepClone(classified);
        clone.AutoClassifications[0].IsManualOverride = true;
        clone.DisabledAutomaticCategoryIds.Add("games");
        Assert.False(classified.AutoClassifications[0].IsManualOverride);
        Assert.Empty(classified.DisabledAutomaticCategoryIds);
        Assert.False(LauncherLayoutRules.Equivalent(classified, clone));
    }

    [Fact]
    public void RemovalSuppressionSurvivesDetachedEditsAndTheSameLayoutCommit()
    {
        var source = JsonSerializer.Deserialize<LauncherLayout>("""
            {"SchemaVersion":2,"Pages":[],"SuppressedAutoAddKeys":["program-key"]}
            """)!;
        using var temp = new TempDataDirectory();
        var data = new GlassDeskDataStore(temp.Paths); data.LoadAll();
        Assert.True(data.Layouts.CommitLauncher(LauncherLayoutRules.DeepClone(source), data.Layouts.LauncherVersion));
        var reloaded = new GlassDeskDataStore(temp.Paths); reloaded.LoadAll();
        var json = JsonSerializer.SerializeToElement(reloaded.Layouts.Launcher);
        Assert.True(json.TryGetProperty("SuppressedAutoAddKeys", out var suppression));
        Assert.Equal("program-key", suppression[0].GetString());
    }

    [Fact]
    public void FailedLayoutSaveRestoresOwnershipManualLocksAndRemovalSuppressionTogether()
    {
        using var temp = new TempDataDirectory();
        var data = new GlassDeskDataStore(temp.Paths); data.LoadAll();
        var original = LauncherAutoGroupingRules.ApplyClassification(Apps(), "editor", new("development", "code"));
        original.SuppressedAutoAddKeys.Add("old-identity");
        Assert.True(data.Layouts.CommitLauncher(original, data.Layouts.LauncherVersion));
        var before = File.ReadAllBytes(temp.Paths.LauncherLayoutFile);
        var draft = LauncherAutoGroupingRules.SetManualCategory(data.Layouts.Launcher, "editor", "games");
        draft.DisabledAutomaticCategoryIds.Add("social");
        draft.SuppressedAutoAddKeys.Add("should-not-persist");
        using (var blocker = new FileStream(temp.Paths.LauncherLayoutFile + AtomicFile.TempSuffix, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<StorageException>(() => data.Layouts.CommitLauncher(draft, data.Layouts.LauncherVersion));
        Assert.Equal(before, File.ReadAllBytes(temp.Paths.LauncherLayoutFile));
        Assert.True(LauncherLayoutRules.Equivalent(original, data.Layouts.Launcher));
    }
}
