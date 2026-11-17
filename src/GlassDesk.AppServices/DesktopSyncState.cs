using System.Text.Json.Serialization;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices;

public sealed class DesktopSyncState
{
    public int SchemaVersion { get; set; } = 1;
    public bool BaselineEstablished { get; set; }
    public int ScopeVersion { get; set; } = 1;
    public List<DesktopSyncSource> Sources { get; set; } = [];
    public List<string> SuppressedKeys { get; set; } = [];
    public List<string> ManuallyManagedAppIds { get; set; } = [];
    public List<string> DesktopRemovedAppIds { get; set; } = [];
}

public sealed class DesktopSyncSource
{
    public string SourcePath { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string LogicalKey { get; set; } = string.Empty;
    public string? AppId { get; set; }
    public string Outcome { get; set; } = "seen";
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? FirstFailureUtc { get; set; }
    public DateTimeOffset? FirstMissingUtc { get; set; }
    public int FailureCount { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DesktopSyncState))]
public partial class DesktopSyncJsonContext : JsonSerializerContext;

internal sealed class DesktopSyncStateStore
{
    private readonly JsonFileStore<DesktopSyncState> _store;
    public DesktopSyncStateStore(string root)
    {
        _store = new(Path.Combine(root, "desktop-sync.json"), DesktopSyncJsonContext.Default.DesktopSyncState);
        Reload();
    }

    public DesktopSyncState State { get; private set; } = new();
    public bool IsBlocked { get; private set; }

    public void Reload()
    {
        try
        {
            var raw = AtomicFile.ReadAllTextOrNull(_store.Path);
            if (raw is not null && !DesktopSyncDocumentValidator.IsValid(raw))
            {
                var backup = AtomicFile.ReadAllTextOrNull(_store.Path + AtomicFile.BackupSuffix);
                if (backup is null || !DesktopSyncDocumentValidator.IsValid(backup))
                { IsBlocked = true; return; }
                // Let the typed store recover from the validated backup, preserving bad input as evidence.
                AtomicFile.PreserveCorrupt(_store.Path, DateTimeOffset.UtcNow);
            }
        }
        catch (StorageException) { IsBlocked = true; return; }
        var loaded = _store.Load(static () => new());
        State = loaded.Value;
        State.Sources ??= [];
        State.SuppressedKeys ??= [];
        State.ManuallyManagedAppIds ??= [];
        State.DesktopRemovedAppIds ??= [];
        IsBlocked |= loaded.Status == LoadStatus.Unrecoverable || State.SchemaVersion != 1 || State.ScopeVersion is < 1 or > 2
            || State.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.SourcePath)
                || source.Fingerprint is null || source.LogicalKey is null || source.FailureCount < 0
                || source.Outcome is not ("seen" or "imported" or "pending"))
            || State.Sources.Select(source => source.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != State.Sources.Count;
    }

    public void Save()
    {
        if (IsBlocked) throw new StorageException("桌面同步记录无法恢复，已暂停自动添加。请从备份恢复。", _store.Path);
        _store.Save(State);
    }
}
