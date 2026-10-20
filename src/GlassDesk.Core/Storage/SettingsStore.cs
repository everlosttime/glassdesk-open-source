using System.Text.Json;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Storage;

/// <summary>
/// 负责 <c>settings.json</c>：加载时执行 schema 迁移，保存时保证原子性。
/// </summary>
public sealed class SettingsStore
{
    private readonly JsonFileStore<GlassDeskSettings> _store;

    public SettingsStore(string path, TimeProvider? timeProvider = null)
    {
        _store = new JsonFileStore<GlassDeskSettings>(
            path,
            GlassDeskJsonContext.Default.GlassDeskSettings,
            timeProvider);
    }

    /// <summary>内存中的设置。由 <see cref="Load"/> 替换。</summary>
    public GlassDeskSettings Current { get; private set; } = new();

    public string FilePath => _store.Path;

    /// <summary>
    /// 从磁盘读取设置，迁移旧版文档，并补齐缺失的节。
    /// </summary>
    public LoadOutcome<GlassDeskSettings> Load()
    {
        var outcome = _store.Load(static () => new GlassDeskSettings());

        var settings = outcome.Value;
        settings.Normalize();
        Migrate(settings, outcome.Status == LoadStatus.Loaded ? ReadSchemaVersionOnDisk() : settings.SchemaVersion);

        Current = settings;
        return outcome;
    }

    /// <summary>以原子方式持久化当前设置。</summary>
    /// <exception cref="StorageException">文件无法写入。</exception>
    public void Save()
    {
        Current.Normalize();
        _store.Save(Current);
    }

    /// <summary>
    /// 重新读取版本戳，让 <see cref="Migrate"/> 看到磁盘上真正的值，
    /// 而不是反序列化器回填的默认值。
    /// </summary>
    private int ReadSchemaVersionOnDisk()
    {
        var raw = AtomicFile.ReadAllTextOrNull(_store.Path);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return GlassDeskSettings.CurrentSchemaVersion;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("schemaVersion", out var version) &&
                version.TryGetInt32(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // 文档已经通过强类型路径解析过一次，所以这里只是结构出乎意料；
            // 按当前版本处理是更安全的解读。
        }

        return GlassDeskSettings.CurrentSchemaVersion;
    }

    private static void Migrate(GlassDeskSettings settings, int fromVersion)
    {
        if (fromVersion > GlassDeskSettings.CurrentSchemaVersion)
        {
            // 由更新的版本写入。我们保留自己能理解的部分并按本版本打戳；
            // GlassDeskDataStore 会在下次保存前留存原始副本，
            // 免得更新的字段被直接丢掉。
            settings.SchemaVersion = GlassDeskSettings.CurrentSchemaVersion;
            return;
        }

        // 版本 1 是首个随产品发布的 schema，目前还没有需要逐级迁移的内容。
        // switch 放在这里，是为了让下一次 schema 变更有个明确的落点。
        settings.SchemaVersion = GlassDeskSettings.CurrentSchemaVersion;
    }
}