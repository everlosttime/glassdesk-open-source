using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

public sealed record DesktopDiscoveryPreviewRow(string SourcePath, string DisplayName, string CategoryName, string Reason)
{
    public string? IconPath { get; init; }
    public LauncherClassificationResult Classification { get; init; } = new("uncategorized", "未识别分类");
    public bool CategoryEdited { get; init; }
}
public sealed record DesktopDiscoveryPreview(IReadOnlyList<DesktopDiscoveryPreviewRow> Rows)
{
    internal IReadOnlyList<PreparedDesktopSource> Sources { get; init; } = [];
}
public sealed record DesktopDiscoveryApplyResult(int Added, int Skipped, int Failed, string Message);
public sealed record DesktopDiscoveryStatus(string Message, int Added = 0, int Failed = 0);

internal sealed record PreparedDesktopSource(string Path, string Fingerprint, byte[] Content,
    AppEntry? Entry, string LogicalKey, LauncherClassificationResult? Classification, bool Failed, string? Failure)
{
    public bool CategoryEdited { get; init; }
}

/// <summary>Watches desktop roots; parsing and icons run off-thread, shared stores mutate through the host dispatcher.</summary>
public sealed class DesktopAppDiscoveryService : IDisposable
{
    private readonly AppServiceHost _host;
    private readonly LauncherAutoOrganizationService _organization;
    private string[] _roots;
    private readonly bool _useDefaultRoots;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _lifecycle = new();
    private Func<Action, Task>? _dispatch;
    private CancellationTokenSource? _run;
    private CancellationTokenSource? _debounce;
    private int _generation;
    private int _recoveryActive;
    private bool _disposed;
    private readonly ConcurrentQueue<(string OldPath, string NewPath)> _renames = new();

    public DesktopAppDiscoveryService(AppServiceHost host, LauncherAutoOrganizationService organization, IReadOnlyList<string>? desktopRoots = null)
    {
        _host = host;
        _organization = organization;
        _useDefaultRoots = desktopRoots is null;
        _roots = (desktopRoots ?? [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)])
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool HasBaseline => _organization.Sync.State.BaselineEstablished;
    public bool IsRunning { get { lock (_lifecycle) return _run is { IsCancellationRequested: false }; } }
    public event EventHandler<DesktopDiscoveryStatus>? StatusChanged;

    public void Configure(Func<Action, Task> dispatch) => _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        _organization.Sync.Reload();
        if (!_host.LauncherSettings.AutoAddDesktopAppsEnabled) return;
        if (_organization.Sync.IsBlocked) { Report("桌面同步记录无法恢复，已暂停自动添加。请从备份恢复。"); return; }
        if (!HasBaseline) { Report("请先在设置中预览已有桌面项目，再开启自动添加。"); return; }
        if (_dispatch is null) { Report("自动添加尚未连接宿主线程。"); return; }
        lock (_lifecycle)
        {
            _run = new();
            InstallWatchers();
        }
        QueueScan(TimeSpan.Zero);
        if (_watchers.Count < _roots.Length) OnError(this, new ErrorEventArgs(new IOException("部分桌面目录暂不可用。")));
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            _generation++;
            _run?.Cancel(); _run?.Dispose(); _run = null;
            _debounce?.Cancel(); _debounce?.Dispose(); _debounce = null;
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }

    public async Task StopAsync()
    {
        Stop();
        await _scanGate.WaitAsync().ConfigureAwait(false);
        _scanGate.Release();
    }

    public async Task<DesktopDiscoveryPreview> PreviewExistingAsync(CancellationToken cancellationToken = default)
    {
        var sources = await Task.Run(() => PrepareSources(cancellationToken), cancellationToken).ConfigureAwait(false);
        var rows = sources.Where(source => source.Entry is not null).Select(source => new DesktopDiscoveryPreviewRow(
            source.Path, source.Entry!.DisplayName, LauncherCategoryCatalog.Find(source.Classification!.CategoryId)?.Name ?? "待分类",
            source.Classification.Reason) { Classification = source.Classification, IconPath = _organization.PreviewIconFor(source.Entry) }).ToArray();
        return new(rows) { Sources = sources };
    }

    public async Task<DesktopDiscoveryApplyResult> EnableWithBaselineAsync(DesktopDiscoveryPreview preview,
        IReadOnlyCollection<string> selectedPaths, CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selected = selectedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = preview.Rows.ToDictionary(row => row.SourcePath, StringComparer.OrdinalIgnoreCase);
            var sources = await Task.Run(() => PrepareSources(cancellationToken), cancellationToken).ConfigureAwait(false);
            var original = preview.Sources.ToDictionary(source => source.Path, StringComparer.OrdinalIgnoreCase);
            if (sources.Any(source => selected.Contains(source.Path)
                    && (!original.TryGetValue(source.Path, out var previous) || previous.Fingerprint != source.Fingerprint))
                || selected.Any(path => !sources.Any(source => source.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
                return new(0, 0, selected.Count, "桌面项目已变化，请重新预览。");
            var prepared = await Task.Run(() => sources.Select(source => selected.Contains(source.Path)
                ? StageSource(rows.TryGetValue(source.Path, out var row) && row.CategoryEdited
                    ? source with { Classification = row.Classification, CategoryEdited = true } : source, cancellationToken)
                : source).ToArray(), cancellationToken).ConfigureAwait(false);
            var added = 0; var skipped = 0; var failed = 0;
            foreach (var source in prepared)
            {
                if (!original.ContainsKey(source.Path)) continue;
                await Dispatch(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _generation) throw new OperationCanceledException();
                    if (_organization.Sync.IsBlocked) throw new StorageException("桌面同步记录无法恢复，请先恢复备份。", Path.Combine(_host.Paths.RootDirectory, "desktop-sync.json"));
                    if (!selected.Contains(source.Path)) { RememberSeen(source); skipped++; return; }
                    var outcome = CommitSource(source, explicitSelection: true);
                    if (outcome > 0) added++; else if (outcome == 0) skipped++; else failed++;
                }).ConfigureAwait(false);
                // Each source is a separate dispatcher operation so a large desktop never monopolizes the UI queue.
                await Task.Yield();
            }
            await Dispatch(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _generation) throw new OperationCanceledException();
                if (failed == 0)
                {
                    _organization.Sync.State.BaselineEstablished = true;
                    _organization.Sync.State.ScopeVersion = 2;
                }
                _organization.Sync.Save();
            }).ConfigureAwait(false);
            var result = new DesktopDiscoveryApplyResult(added, skipped, failed, $"已添加 {added} 项，跳过 {skipped} 项，失败 {failed} 项。");
            Report(result.Message, result.Added, result.Failed);
            return result;
        }
        finally { _scanGate.Release(); }
    }

    public async Task<int> RepairShortcutIconsAsync(CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RepairShortcutIconsCoreAsync(generation, cancellationToken).ConfigureAwait(false); }
        finally { _scanGate.Release(); }
    }

    private async Task<int> RepairShortcutIconsCoreAsync(int generation, CancellationToken token)
    {
        Task<IReadOnlyList<InternetShortcutIconRepair>>? preparation = null;
        await Dispatch(() =>
        {
            token.ThrowIfCancellationRequested();
            if (generation == _generation) preparation = _host.Maintenance.PrepareInternetShortcutIconRepairsAsync(token);
        }).ConfigureAwait(false);
        if (preparation is null) return 0;
        var repairs = await preparation.ConfigureAwait(false);
        var repaired = 0;
        await Dispatch(() =>
        {
            token.ThrowIfCancellationRequested();
            if (generation == _generation) repaired = _host.Maintenance.ApplyInternetShortcutIconRepairs(repairs);
        }).ConfigureAwait(false);
        return repaired;
    }

    public async Task ScanNowAsync(CancellationToken cancellationToken = default)
    {
        var generation = _generation;
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, DesktopSyncSource> known = new(StringComparer.OrdinalIgnoreCase);
            var enabled = false;
            var expandingScope = false;
            await Dispatch(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                ApplyPendingRenames();
                enabled = _host.LauncherSettings.AutoAddDesktopAppsEnabled && HasBaseline && !_organization.Sync.IsBlocked;
                expandingScope = _organization.Sync.State.ScopeVersion < 2;
                known = _organization.Sync.State.Sources.ToDictionary(item => item.SourcePath, item => new DesktopSyncSource
                { SourcePath = item.SourcePath, Fingerprint = item.Fingerprint, LogicalKey = item.LogicalKey,
                    AppId = item.AppId, Outcome = item.Outcome, FailureCount = item.FailureCount,
                    LastAttemptUtc = item.LastAttemptUtc, FirstFailureUtc = item.FirstFailureUtc },
                    StringComparer.OrdinalIgnoreCase);
            }).ConfigureAwait(false);
            if (!enabled) return;
            await RepairShortcutIconsCoreAsync(generation, cancellationToken).ConfigureAwait(false);
            var sources = await Task.Run(() => PrepareSources(cancellationToken, known)
                .Select(source => StageSource(source, cancellationToken)).ToArray(), cancellationToken).ConfigureAwait(false);
            var rootSnapshot = await Task.Run(ReadAccessibleRootSnapshot, cancellationToken).ConfigureAwait(false);
            var added = 0; var failed = 0; var removed = 0; TimeSpan? retryDelay = null;
            foreach (var source in sources)
            {
                await Dispatch(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _generation || !_host.LauncherSettings.AutoAddDesktopAppsEnabled) return;
                    // A software-only baseline never asked the user about existing documents.
                    // Keep those as existing items; later creations and explicit preview selections qualify.
                    if (expandingScope && !known.ContainsKey(source.Path) && !WasSoftwareShortcut(source))
                    { RememberSeen(source); return; }
                    var outcome = CommitSource(source, explicitSelection: false);
                    if (outcome > 0) added++; else if (outcome < 0) failed++;
                }).ConfigureAwait(false);
                await Task.Yield();
            }
            await Dispatch(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _generation || !_host.LauncherSettings.AutoAddDesktopAppsEnabled) return;
                var deletion = ReconcileDeletedSources(rootSnapshot);
                removed = deletion.Removed;
                if (deletion.NeedsConfirmation) retryDelay = TimeSpan.FromMilliseconds(800);
                if (expandingScope)
                {
                    _organization.Sync.State.ScopeVersion = 2;
                    _organization.Sync.Save();
                }
                var pending = _organization.Sync.State.Sources.Where(source => source.Outcome == "pending"
                    && source.FirstFailureUtc is { } first && DateTimeOffset.UtcNow - first < TimeSpan.FromMinutes(10)).ToArray();
                if (failed > 0 && pending.Length > 0 && retryDelay is null)
                    retryDelay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, pending.Max(source => source.FailureCount)))));
            }).ConfigureAwait(false);
            if (added > 0 || removed > 0 || failed > 0) Report($"已同步：添加 {added} 项，移除 {removed} 项，待重试 {failed} 项。", added, failed);
            if (retryDelay is { } delay) QueueScan(delay);
        }
        finally { _scanGate.Release(); }
    }

    private int CommitSource(PreparedDesktopSource source, bool explicitSelection)
    {
        var state = _organization.Sync.State;
        var previous = state.Sources.FirstOrDefault(item => item.SourcePath.Equals(source.Path, StringComparison.OrdinalIgnoreCase));
        if (source.Entry is null)
        {
            if (!source.Failed) { RememberSeen(source); return 0; }
            var pending = Upsert(source); pending.Outcome = "pending"; pending.FailureCount++;
            pending.FirstFailureUtc ??= DateTimeOffset.UtcNow;
            pending.LastAttemptUtc = DateTimeOffset.UtcNow; _organization.Sync.Save();
            return -1;
        }
        if (_organization.IsSuppressed(source.LogicalKey) && !explicitSelection) { RememberSeen(source); return 0; }
        if (!explicitSelection && state.Sources.Any(item => item.LogicalKey == source.LogicalKey && item.Outcome == "seen"))
        { RememberSeen(source); return 0; }
        var existing = state.Sources.Where(item => item.LogicalKey == source.LogicalKey && item.AppId is not null)
                .Select(item => _host.Library.Find(item.AppId)).FirstOrDefault(item => item is not null)
            ?? _host.Library.FindByKey(AppEntryKey.Compute(source.Entry));
        var wasPending = previous?.Outcome == "pending" && previous.LogicalKey == source.LogicalKey;
        var desktopRemoved = existing is not null && state.DesktopRemovedAppIds.Contains(existing.Id, StringComparer.Ordinal);
        var visible = existing is not null && LauncherLayoutRules.AppIds(_host.Layouts.Launcher).Contains(existing.Id);
        if (existing is not null && !visible && !wasPending && !desktopRemoved && !explicitSelection)
        {
            RememberSeen(source, existing.Id); return 0;
        }
        if (!explicitSelection && previous is { Outcome: "seen" or "imported" }
            && previous.Fingerprint == source.Fingerprint) return 0;
        var record = Upsert(source);
        record.Outcome = "pending"; record.AppId = existing?.Id ?? source.Entry.Id;
        record.LastAttemptUtc = DateTimeOffset.UtcNow;
        _organization.Sync.Save();
        try
        {
            if (explicitSelection) _organization.ClearSuppressionForDesktopImport(existing ?? source.Entry);
            AppEntry imported;
            if (existing is not null)
            {
                imported = existing;
                if ((wasPending || desktopRemoved || explicitSelection) && !visible) _host.Import.ImportPreparedItems([existing], restoreExisting: true);
            }
            else imported = _host.Import.ImportPreparedItems([source.Entry], restoreExisting: true).Added
                .Concat(_host.Library.Items.Where(item => item.Id == source.Entry.Id)).First();
            if (desktopRemoved && imported.IsTargetMissing && !source.Entry.IsTargetMissing)
            {
                var repaired = LauncherAutoOrganizationService.Clone(imported);
                repaired.IsTargetMissing = false;
                if (_host.Library.Update(repaired))
                {
                    try { _host.Library.Save(); }
                    catch { _host.Library.Update(imported); throw; }
                    imported = repaired;
                }
            }
            if (source.CategoryEdited) _organization.ClassifyExplicit(imported.Id, source.Classification!);
            else if (existing is null || wasPending || desktopRemoved) _organization.ClassifyNew(imported.Id, source.Classification!);
            state.DesktopRemovedAppIds.Remove(imported.Id);
            record.AppId = imported.Id; record.Outcome = "imported"; record.FailureCount = 0;
            record.FirstFailureUtc = null;
            _organization.Sync.Save();
            return existing is null || (wasPending || desktopRemoved || explicitSelection) && !visible ? 1 : 0;
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            record.Outcome = "pending"; record.FailureCount++;
            record.FirstFailureUtc ??= DateTimeOffset.UtcNow;
            try { _organization.Sync.Save(); } catch (StorageException) { }
            Report("自动添加暂未完成，将重试：" + ex.Message, failed: 1);
            return -1;
        }
    }

    private static bool WasSoftwareShortcut(PreparedDesktopSource source)
        => source.LogicalKey.StartsWith("protocol:", StringComparison.Ordinal)
            || Path.GetExtension(source.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                && (source.Entry is null || source.Entry.Kind is AppItemKind.Program or AppItemKind.Shortcut);

    private Dictionary<string, HashSet<string>> ReadAccessibleRootSnapshot()
    {
        var snapshots = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            try
            {
                // An incomplete enumeration is never evidence of deletion.
                snapshots[root] = Directory.GetFileSystemEntries(root).Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return snapshots;
    }

    private (int Removed, bool NeedsConfirmation) ReconcileDeletedSources(Dictionary<string, HashSet<string>> roots)
    {
        var state = _organization.Sync.State;
        var confirmed = new HashSet<DesktopSyncSource>();
        var changed = false;
        var needsConfirmation = false;
        var now = DateTimeOffset.UtcNow;
        foreach (var source in state.Sources)
        {
            var parent = Path.GetDirectoryName(source.SourcePath);
            if (parent is null || !roots.TryGetValue(parent, out var present) || !Directory.Exists(parent)) continue;
            if (present.Contains(source.SourcePath) || File.Exists(source.SourcePath) || Directory.Exists(source.SourcePath))
            {
                if (source.FirstMissingUtc is not null) { source.FirstMissingUtc = null; changed = true; }
                continue;
            }
            if (source.FirstMissingUtc is null) { source.FirstMissingUtc = now; changed = true; }
            if (now - source.FirstMissingUtc.Value >= TimeSpan.FromMilliseconds(750)) confirmed.Add(source);
            else needsConfirmation = true;
        }
        var removed = 0;
        foreach (var entry in _host.Library.Items.Where(item => item is { IsUsable: true }
            && !string.IsNullOrWhiteSpace(item.AutomaticSourceKey)).ToArray())
        {
            if (state.ManuallyManagedAppIds.Contains(entry.Id, StringComparer.Ordinal)) continue;
            var related = state.Sources.Where(source => source.AppId == entry.Id || source.LogicalKey == entry.AutomaticSourceKey).ToArray();
            if (related.Length == 0 || related.Any(source => !confirmed.Contains(source))) continue;
            if (!LauncherLayoutRules.AppIds(_host.Layouts.Launcher).Contains(entry.Id)) continue;
            // Journal desktop removal separately from the user's explicit suppression.
            // This lets a recreated desktop source restore the same ID without resurrecting user-hidden apps.
            if (!state.DesktopRemovedAppIds.Contains(entry.Id, StringComparer.Ordinal))
            {
                state.DesktopRemovedAppIds.Add(entry.Id);
                _organization.Sync.Save();
            }
            var draft = LauncherLayoutEditor.RemoveShortcut(_host.Layouts.Launcher, entry.Id,
                Math.Clamp(_host.LauncherSettings.Rows, 2, 10) * Math.Clamp(_host.LauncherSettings.Columns, 2, 12));
            if (!_host.Layouts.CommitLauncher(draft, _host.Layouts.LauncherVersion))
                throw new StorageException("桌面删除同步时布局已变化，请重试。", _host.Layouts.LauncherFilePath);
            removed++;
        }
        if (confirmed.Count > 0)
        {
            state.Sources.RemoveAll(confirmed.Contains);
            changed = true;
        }
        if (changed || removed > 0) _organization.Sync.Save();
        if (removed > 0) _host.OnLibraryChanged();
        return (removed, needsConfirmation);
    }

    private DesktopSyncSource Upsert(PreparedDesktopSource source)
    {
        var state = _organization.Sync.State;
        var record = state.Sources.FirstOrDefault(item => item.SourcePath.Equals(source.Path, StringComparison.OrdinalIgnoreCase));
        if (record is null) { record = new() { SourcePath = source.Path }; state.Sources.Add(record); }
        record.Fingerprint = source.Fingerprint; record.LogicalKey = source.LogicalKey;
        return record;
    }

    private void RememberSeen(PreparedDesktopSource source, string? appId = null)
    {
        var record = Upsert(source);
        record.Outcome = "seen"; record.AppId = appId; record.FailureCount = 0;
        _organization.Sync.Save();
    }

    private List<PreparedDesktopSource> PrepareSources(CancellationToken token, Dictionary<string, DesktopSyncSource>? known = null)
    {
        var prepared = new List<PreparedDesktopSource>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            string[] paths;
            try { paths = Directory.GetFileSystemEntries(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report("无法读取桌面：" + ex.Message); continue; }
            foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(path);

                // Roots are ordered personal desktop first, matching the merged Windows desktop.
                if (!names.Add(Path.GetFileName(path))) continue;
                try
                {
                    if ((File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var directory = Directory.Exists(path);
                    var shortcut = !directory && extension.ToLowerInvariant() is ".lnk" or ".url";
                    // Ordinary files are referenced in place. Never load documents, images or large downloads into memory.
                    byte[] bytes = [];
                    string fingerprint;
                    if (shortcut)
                    {
                        var info = new FileInfo(path);
                        if (info.Length > 1024 * 1024)
                        {
                            var largeFingerprint = "large-link:" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                            if (known?.TryGetValue(path, out var previousLarge) == true && previousLarge.Fingerprint == largeFingerprint) continue;
                            var plain = new AppEntry { Id = AppLibraryStore.NewId(), Target = path, Kind = AppItemKind.File, DisplayName = Path.GetFileName(path), IconSource = path };
                            var largeKey = AppEntryKey.Compute(plain); plain.AutomaticSourceKey = largeKey;
                            prepared.Add(new(path, largeFingerprint, [], plain, largeKey, AppClassificationService.Classify(plain), false, null));
                            continue;
                        }
                        bytes = File.ReadAllBytes(path);
                        fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
                    }
                    else
                    {
                        fingerprint = directory ? "directory:" + Directory.GetCreationTimeUtc(path).Ticks
                            : "file:" + File.GetCreationTimeUtc(path).Ticks + ":" + new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks;
                    }
                    if (known?.TryGetValue(path, out var old) == true && old.Fingerprint == fingerprint && old.Outcome != "pending") continue;
                    var source = shortcut ? PrepareSource(path, fingerprint, bytes) : PreparePlainSource(path, fingerprint);
                    prepared.Add(!shortcut || File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) ? source
                        : new(path, fingerprint, [], null, string.Empty, null, true, "桌面项目正在写入，稍后重试。"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { prepared.Add(new(path, string.Empty, [], null, string.Empty, null, true, ex.Message)); }
            }
        }
        return prepared;
    }

    private static PreparedDesktopSource PrepareSource(string path, string fingerprint, byte[] bytes)
    {
        var name = ShellDisplayNames.GetFileName(path, removeExtension: true);
        string? protocol = null;
        AppEntry? entry;
        string? description = null;
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            protocol = GameShortcutProtocol.Read(path);
            if (protocol is null) return PreparePlainSource(path, fingerprint, bytes);
            entry = new() { Id = AppLibraryStore.NewId(), Kind = AppItemKind.Shortcut, Target = path, DisplayName = name,
                IconSource = InternetShortcutReader.ReadIconSource(path) ?? path };
        }
        else
        {
            var resolved = ShortcutResolver.Resolve(path);
            if (resolved is null) return new(path, fingerprint, bytes, null, string.Empty, null, true, "快捷方式尚未写入完成或无法解析。");
            description = resolved.Description;
            var result = AppEntryFactory.TryCreateFromPath(path, name);
            entry = result.Entry;
            if (entry is null) return new(path, fingerprint, bytes, null, string.Empty, null, true, result.Message);
            if (entry.IsTargetMissing)
                return new(path, fingerprint, bytes, null, string.Empty, null, true, "快捷方式目标暂不可用，稍后重试。");
        }
        var key = protocol is null ? LauncherAutoOrganizationService.GetLogicalKey(entry) : "protocol:" + protocol;
        entry.AutomaticSourceKey = key;
        var classification = AppClassificationService.Classify(entry, description, protocol);
        return new(path, fingerprint, bytes, entry, key, classification, false, null);
    }

    private static PreparedDesktopSource PreparePlainSource(string path, string fingerprint, byte[]? content = null)
    {
        var result = AppEntryFactory.TryCreateFromPath(path);
        if (result.Entry is not { } entry)
            return new(path, fingerprint, [], null, string.Empty, null, true, result.Message);
        var key = LauncherAutoOrganizationService.GetLogicalKey(entry);
        entry.AutomaticSourceKey = key;
        return new(path, fingerprint, content ?? [], entry, key, AppClassificationService.Classify(entry), false, null);
    }
    private PreparedDesktopSource StageSource(PreparedDesktopSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Entry is null) return source;
        if (Directory.Exists(source.Path) || Path.GetExtension(source.Path).ToLowerInvariant() is not (".lnk" or ".url") || source.Content.Length == 0)
        {
            _organization.PreviewIconFor(source.Entry);
            return source;
        }
        try
        {
            var managed = Path.Combine(_host.Paths.ManagedShortcutsDirectory,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.LogicalKey))).ToLowerInvariant() + Path.GetExtension(source.Path).ToLowerInvariant());
            Directory.CreateDirectory(_host.Paths.ManagedShortcutsDirectory);
            // Stable filenames make interrupted imports and duplicate desktop copies idempotent.
            if (!File.Exists(managed) || !File.ReadAllBytes(managed).AsSpan().SequenceEqual(source.Content))
            {
                var temporary = managed + ".tmp";
                File.WriteAllBytes(temporary, source.Content);
                File.Move(temporary, managed, overwrite: true);
            }
            var entry = LauncherAutoOrganizationService.Clone(source.Entry);
            if (entry.Kind == AppItemKind.Shortcut || entry.Target.Equals(source.Path, StringComparison.OrdinalIgnoreCase)) entry.Target = managed;
            if (entry.IconSource == source.Path) entry.IconSource = managed;
            new IconCache(_host.Paths).EnsureFor(entry.IconSource);
            return source with { Entry = entry };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return source with { Entry = null, Failed = true, Failure = ex.Message }; }
    }

    private Task Dispatch(Action action) => _dispatch is null ? RunInline(action) : _dispatch(action);
    private static Task RunInline(Action action) { action(); return Task.CompletedTask; }
    private void ApplyPendingRenames()
    {
        while (_renames.TryDequeue(out var rename))
        {
            var record = _organization.Sync.State.Sources.FirstOrDefault(source => source.SourcePath.Equals(rename.OldPath, StringComparison.OrdinalIgnoreCase));
            if (record is null) continue;
            var oldKey = record.LogicalKey;
            var key = oldKey;
            var entry = _host.Library.Find(record.AppId);
            if (entry is not null && entry.Target.Equals(rename.OldPath, StringComparison.OrdinalIgnoreCase))
            {
                var updated = LauncherAutoOrganizationService.Clone(entry);
                updated.Target = rename.NewPath;
                if (updated.IconSource?.Equals(rename.OldPath, StringComparison.OrdinalIgnoreCase) == true) updated.IconSource = rename.NewPath;
                if (updated.DisplayName == ItemKindDetector.DeriveDisplayName(rename.OldPath, updated.Kind))
                    updated.DisplayName = ItemKindDetector.DeriveDisplayName(rename.NewPath, updated.Kind);
                updated.Kind = ItemKindDetector.Detect(rename.NewPath, Directory.Exists(rename.NewPath));
                key = AppEntryKey.Compute(updated);
                updated.AutomaticSourceKey = key;
                if (!_host.Library.Update(updated)) continue;
                try { _host.Library.Save(); }
                catch { _host.Library.Update(entry); _renames.Enqueue(rename); throw; }
                if (_host.LauncherSettings.GroupModeEnabled && _host.LauncherSettings.SmartGroupingEnabled)
                {
                    var grouped = LauncherAutoGroupingRules.ApplyClassification(_host.Layouts.Launcher,
                        updated.Id, AppClassificationService.Classify(updated), replaceExisting: true);
                    if (!_host.Layouts.CommitLauncher(grouped, _host.Layouts.LauncherVersion))
                        throw new StorageException("改名后的分类尚未保存，请重试。", _host.Layouts.LauncherFilePath);
                }
                _host.OnLibraryChanged();
            }
            else if (oldKey == AppEntryKey.Compute(new AppEntry { Target = rename.OldPath }))
                key = AppEntryKey.Compute(new AppEntry { Target = rename.NewPath });
            if (key != oldKey)
            {
                var draft = LauncherLayoutRules.DeepClone(_host.Layouts.Launcher);
                for (var i = 0; i < draft.SuppressedAutoAddKeys.Count; i++)
                    if (draft.SuppressedAutoAddKeys[i] == oldKey) draft.SuppressedAutoAddKeys[i] = key;
                if (!_host.Layouts.CommitLauncher(draft, _host.Layouts.LauncherVersion))
                    throw new StorageException("桌面项目改名后布局已变化，请重试。", _host.Layouts.LauncherFilePath);
                for (var i = 0; i < _organization.Sync.State.SuppressedKeys.Count; i++)
                    if (_organization.Sync.State.SuppressedKeys[i] == oldKey) _organization.Sync.State.SuppressedKeys[i] = key;
            }
            record.SourcePath = rename.NewPath;
            record.LogicalKey = key;
            _organization.Sync.Save();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    { QueueScan(TimeSpan.FromMilliseconds(750)); }
    private void OnRenamed(object sender, RenamedEventArgs e)
    { _renames.Enqueue((e.OldFullPath, e.FullPath)); QueueScan(TimeSpan.FromMilliseconds(750)); }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Report("桌面监听发生变化，正在恢复监听并重新扫描。");
        if (Interlocked.CompareExchange(ref _recoveryActive, 1, 0) != 0) return;
        CancellationToken token;
        var generation = _generation;
        lock (_lifecycle)
        {
            if (_run is null) { Interlocked.Exchange(ref _recoveryActive, 0); return; }
            token = _run.Token;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 300; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    var complete = false;
                    await Dispatch(() =>
                    {
                        if (generation != _generation || !IsRunning) return;
                        lock (_lifecycle) { InstallWatchers(); complete = _watchers.Count == _roots.Length; }
                    }).ConfigureAwait(false);
                    QueueScan(TimeSpan.FromMilliseconds(750));
                    if (complete || generation != _generation) break;
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Report("恢复桌面监听失败：" + ex.Message, failed: 1); }
            finally { Interlocked.Exchange(ref _recoveryActive, 0); }
        }, CancellationToken.None);
    }

    // Called only on the serialized host thread; recovery never reloads shared stores from a worker.
    private void InstallWatchers()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        if (_useDefaultRoots)
            _roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }
                .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in _roots.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Created += OnChanged; watcher.Changed += OnChanged; watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed; watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Report("部分桌面暂时无法监听：" + ex.Message); }
        }
    }

    private void QueueScan(TimeSpan delay)
    {
        CancellationToken token;
        lock (_lifecycle)
        {
            if (_run is null || _run.IsCancellationRequested) return;
            _debounce?.Cancel(); _debounce?.Dispose();
            _debounce = CancellationTokenSource.CreateLinkedTokenSource(_run.Token);
            token = _debounce.Token;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, token).ConfigureAwait(false); await ScanNowAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Report("自动添加已暂停本次扫描：" + ex.Message, failed: 1); }
        }, CancellationToken.None);
    }

    private void Report(string message, int added = 0, int failed = 0)
    {
        var handlers = StatusChanged?.GetInvocationList();
        if (handlers is null) return;
        foreach (EventHandler<DesktopDiscoveryStatus> handler in handlers)
            try { handler(this, new(message, added, failed)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* An observer cannot break the watcher callback. */ }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
}
