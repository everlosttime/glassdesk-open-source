using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GlassDesk.Core.Storage;

/// <summary>
/// 加载并保存单个 JSON 文档，并支持从备份恢复。
/// </summary>
/// <remarks>
/// 序列化走源生成的 <see cref="JsonTypeInfo{T}"/> 而不是
/// 反射，因此每个持久化文档的确切形状在编译期就可见，
/// 裁剪器也无法悄悄剥掉我们依赖的属性。
/// </remarks>
public sealed class JsonFileStore<T>
    where T : class
{
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly TimeProvider _timeProvider;

    public JsonFileStore(string path, JsonTypeInfo<T> typeInfo, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);

        Path = path;
        _typeInfo = typeInfo;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>本存储负责的文档路径。</summary>
    public string Path { get; }

    /// <summary>
    /// 读取文档；主文件不可用时回退到备份。
    /// 文件损坏时绝不抛异常：返回默认值并报告实际发生的情况。
    /// </summary>
    public LoadOutcome<T> Load(Func<T> createDefault)
    {
        ArgumentNullException.ThrowIfNull(createDefault);

        var raw = TryRead(Path);
        if (raw is not null)
        {
            if (TryDeserialize(raw, out var parsed))
            {
                return new LoadOutcome<T>(parsed!, LoadStatus.Loaded, null, null);
            }

            // 不可用。先保留证据，再尝试并列的备份。
            var preserved = AtomicFile.PreserveCorrupt(Path, _timeProvider.GetUtcNow());
            return RecoverOrFail(createDefault, preserved);
        }

        // 没有主文件。备份仍可能存在，例如文档被手动删除，
        // 或上次替换在重命名完成前被打断。
        if (File.Exists(Path + AtomicFile.BackupSuffix))
        {
            return RecoverOrFail(createDefault, preservedFile: null);
        }

        return new LoadOutcome<T>(createDefault(), LoadStatus.FirstRun, null, null);
    }

    /// <summary>原子地写入文档，并把原有内容降级为备份。</summary>
    /// <exception cref="StorageException">文档写入失败时抛出。</exception>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.Serialize(value, _typeInfo);
        AtomicFile.WriteAllText(Path, json);
    }

    private LoadOutcome<T> RecoverOrFail(Func<T> createDefault, string? preservedFile)
    {
        var backupPath = Path + AtomicFile.BackupSuffix;
        var backupRaw = TryRead(backupPath);

        if (backupRaw is not null && TryDeserialize(backupRaw, out var recovered))
        {
            return new LoadOutcome<T>(recovered!, LoadStatus.RecoveredFromBackup, preservedFile, null);
        }

        if (preservedFile is null && !File.Exists(Path))
        {
            // 磁盘上确实什么也没有，也没有可抢救的内容。
            return new LoadOutcome<T>(createDefault(), LoadStatus.FirstRun, null, null);
        }

        return new LoadOutcome<T>(
            createDefault(),
            LoadStatus.Unrecoverable,
            preservedFile,
            new JsonException("配置文件与其备份均无法解析。"));
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 对我们来说「读不了」和「坏了」没有区别：直接转入备份路径，
            // 而不是让整次加载失败。
            return null;
        }
    }

    private bool TryDeserialize(string raw, out T? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(raw, _typeInfo);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}