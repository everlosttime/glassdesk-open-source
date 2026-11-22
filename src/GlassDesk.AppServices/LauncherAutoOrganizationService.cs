using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices;

public sealed record AutoOrganizationPreviewRow(string AppId, string DisplayName, string CategoryName,
    string Reason, bool IsManual, LauncherClassificationResult Classification)
{
    public bool CategoryEdited { get; init; }
    public string? IconPath { get; init; }
}

public sealed record AutoOrganizationPreview(IReadOnlyList<AutoOrganizationPreviewRow> Rows, long LayoutVersion, long LibraryVersion);
public sealed record AutoOrganizationApplyResult(int Applied, int Skipped, int Failed, string Message);

/// <summary>Builds detached previews off-thread and commits only on the caller's host thread.</summary>
public sealed class LauncherAutoOrganizationService
{
    private readonly AppServiceHost _host;
    internal DesktopSyncStateStore Sync { get; }

    public LauncherAutoOrganizationService(AppServiceHost host)
    {
        _host = host;
        Sync = new(host.Paths.RootDirectory);
    }

    public Task<AutoOrganizationPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var layout = LauncherLayoutRules.DeepClone(_host.Layouts.Launcher);
        var layoutVersion = _host.Layouts.LauncherVersion;
        var libraryVersion = _host.Library.Version;
        var ids = LauncherLayoutRules.AppIds(layout).ToHashSet(StringComparer.Ordinal);
        var snapshots = _host.Library.Items.Where(entry => entry is { IsUsable: true } && ids.Contains(entry.Id))
            .Select(entry => (Entry: Clone(entry), Manual: IsManual(layout, entry.Id))).ToArray();
        return Task.Run(() =>
        {
            var rows = new List<AutoOrganizationPreviewRow>();
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var classified = AppClassificationService.Classify(snapshot.Entry);
                rows.Add(new(snapshot.Entry.Id, snapshot.Entry.DisplayName,
                    LauncherCategoryCatalog.Find(classified.CategoryId)?.Name ?? "待分类",
                    snapshot.Manual ? "已手动整理，将保留当前分组" : classified.Reason,
                    snapshot.Manual, classified) { IconPath = PreviewIconFor(snapshot.Entry) });
            }
            return new AutoOrganizationPreview(rows, layoutVersion, libraryVersion);
        }, cancellationToken);
    }

    public AutoOrganizationApplyResult Apply(AutoOrganizationPreview preview, IReadOnlyCollection<string>? selectedAppIds = null)
    {
        if (!_host.LauncherSettings.GroupModeEnabled)
            return new(0, preview.Rows.Count, 0, "请先开启分组模式。");
        if (preview.LayoutVersion != _host.Layouts.LauncherVersion || preview.LibraryVersion != _host.Library.Version)
            return new(0, 0, Math.Max(1, preview.Rows.Count), "应用或布局已变化，请重新预览。");
        var selected = selectedAppIds?.ToHashSet(StringComparer.Ordinal);
        var draft = LauncherLayoutRules.DeepClone(_host.Layouts.Launcher);
        var applied = 0;
        foreach (var row in preview.Rows)
        {
            if (row.IsManual || selected is not null && !selected.Contains(row.AppId)) continue;
            draft = row.CategoryEdited
                ? LauncherAutoGroupingRules.SetManualCategory(draft, row.AppId, row.Classification.CategoryId)
                : LauncherAutoGroupingRules.ApplyClassification(draft, row.AppId, row.Classification, replaceExisting: true);
            applied++;
        }
        try
        {
            if (!_host.Layouts.CommitLauncher(draft, preview.LayoutVersion))
                return new(0, 0, Math.Max(1, preview.Rows.Count), "布局已变化，请重新预览。");
        }
        catch (StorageException ex) { return new(0, 0, Math.Max(1, applied), ex.Message); }
        if (applied > 0) _host.OnLibraryChanged();
        return new(applied, preview.Rows.Count - applied, 0, $"已整理 {applied} 项，保留 {preview.Rows.Count - applied} 项。");
    }

    public void SuppressAutoAdd(string appId)
    {
        var entry = _host.Library.Find(appId);
        if (entry is null) return;
        var key = GetLogicalKey(entry);
        if (!Sync.State.SuppressedKeys.Contains(key, StringComparer.Ordinal))
        {
            Sync.State.SuppressedKeys.Add(key);
            Sync.Save();
        }
    }

    public void ClearSuppressionForManualImport(AppEntry entry)
        => ClearSuppression(entry, preserveAsManual: true);

    internal void ClearSuppressionForDesktopImport(AppEntry entry)
        => ClearSuppression(entry, preserveAsManual: false);

    private void ClearSuppression(AppEntry entry, bool preserveAsManual)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { GetLogicalKey(entry), AppEntryKey.Compute(entry) };
        foreach (var source in Sync.State.Sources.Where(source => source.AppId == entry.Id)) keys.Add(source.LogicalKey);
        var removed = Sync.State.SuppressedKeys.RemoveAll(keys.Contains);
        if (preserveAsManual && !Sync.State.ManuallyManagedAppIds.Contains(entry.Id, StringComparer.Ordinal))
        { Sync.State.ManuallyManagedAppIds.Add(entry.Id); removed++; }
        if (Sync.State.DesktopRemovedAppIds.Remove(entry.Id)) removed++;
        var draft = LauncherLayoutRules.DeepClone(_host.Layouts.Launcher);
        var layoutChanged = draft.SuppressedAutoAddKeys.RemoveAll(keys.Contains) > 0;
        if (layoutChanged && !_host.Layouts.CommitLauncher(draft, _host.Layouts.LauncherVersion))
            throw new StorageException("解除自动添加排除失败，请重试。", _host.Layouts.LauncherFilePath);
        if (removed > 0 && !Sync.IsBlocked) Sync.Save();
    }

    public static string GetLogicalKey(AppEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.AutomaticSourceKey)) return entry.AutomaticSourceKey;
        if (entry.Kind == AppItemKind.Shortcut && !string.IsNullOrWhiteSpace(entry.AppUserModelId))
            return "aumid:" + entry.AppUserModelId.ToLowerInvariant() + "\0" + (entry.Arguments ?? string.Empty).Trim();
        return AppEntryKey.Compute(entry);
    }

    internal string? PreviewIconFor(AppEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.CustomIconPath) && File.Exists(entry.CustomIconPath)) return entry.CustomIconPath;
        try { return _host.Icons.EnsureFor(entry.IconSource)?.Path; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.ExternalException) { return null; }
    }

    internal bool IsSuppressed(string logicalKey) => Sync.State.SuppressedKeys.Contains(logicalKey, StringComparer.Ordinal)
        || _host.Layouts.Launcher.SuppressedAutoAddKeys.Contains(logicalKey, StringComparer.Ordinal);

    internal void ClassifyNew(string appId, LauncherClassificationResult classification)
    {
        if (!_host.LauncherSettings.GroupModeEnabled || !_host.LauncherSettings.SmartGroupingEnabled) return;
        var draft = LauncherAutoGroupingRules.ApplyClassification(_host.Layouts.Launcher, appId, classification);
        if (!_host.Layouts.CommitLauncher(draft, _host.Layouts.LauncherVersion))
            throw new StorageException("分类提交失败，请稍后重试。", _host.Layouts.LauncherFilePath);
        _host.OnLibraryChanged();
    }

    internal void ClassifyExplicit(string appId, LauncherClassificationResult classification)
    {
        if (!_host.LauncherSettings.GroupModeEnabled) return;
        var draft = LauncherAutoGroupingRules.SetManualCategory(_host.Layouts.Launcher, appId, classification.CategoryId);
        if (!_host.Layouts.CommitLauncher(draft, _host.Layouts.LauncherVersion))
            throw new StorageException("分类提交失败，请稍后重试。", _host.Layouts.LauncherFilePath);
        _host.OnLibraryChanged();
    }

    private static bool IsManual(LauncherLayout layout, string appId)
    {
        var state = layout.AutoClassifications.FirstOrDefault(item => item.ItemId == appId);
        if (state is { IsManualOverride: true }) return true;
        return layout.FilterGroups.Any(group => group.Id != state?.AutomaticGroupId && group.ItemIds.Contains(appId));
    }

    internal static AppEntry Clone(AppEntry entry) => new()
    {
        Id = entry.Id, DisplayName = entry.DisplayName, Kind = entry.Kind, Target = entry.Target,
        Arguments = entry.Arguments, WorkingDirectory = entry.WorkingDirectory, AppUserModelId = entry.AppUserModelId,
        AutomaticSourceKey = entry.AutomaticSourceKey, IconSource = entry.IconSource, CustomIconPath = entry.CustomIconPath,
        SearchAliases = [.. entry.SearchAliases], PinyinIndex = entry.PinyinIndex, PinyinInitials = entry.PinyinInitials,
        AddedAtUtc = entry.AddedAtUtc, IsTargetMissing = entry.IsTargetMissing,
    };
}
