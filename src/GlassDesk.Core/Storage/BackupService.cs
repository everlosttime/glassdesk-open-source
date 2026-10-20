using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Storage;

/// <summary>一次备份或恢复做了什么，可直接呈现给用户。</summary>
public sealed class TransferResult
{
    public bool Succeeded { get; init; }

    /// <summary>写入的目标目录（备份）或读取的来源目录（恢复）。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>被复制文件的相对名称。</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>恢复前快照写入的位置，前提是确实拍过快照。</summary>
    public string? SafetyCopyDirectory { get; init; }

    /// <summary>
    /// 恢复后被改回新数据目录的托管图标引用数量。
    /// </summary>
    /// <remarks>
    /// 备份来自旧目录时，归档里的引用指着那里；恢复必须把它们改到新目录，
    /// 否则文件复制过去了、图标却还是失效。为 0 表示没有需要改的。
    /// </remarks>
    public int RepairedIconReferences { get; init; }

    /// <summary><see cref="Succeeded"/> 为 false 时面向用户的说明。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 把本产品自己的数据复制到某个文件夹，也能从那里复制回来。它从不读写
/// 参考产品的私有存储——方案明确把第一版限定在
/// GlassDesk 自身的数据，外加从开始菜单重新导入。
/// </summary>
public sealed class BackupService
{
    private const string ManifestFileName = "manifest.json";

    private readonly AppPaths _paths;
    private readonly TimeProvider _timeProvider;

    public BackupService(AppPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 把设置、应用库和布局写一份自描述的副本到
    /// <paramref name="destinationDirectory"/>。
    /// </summary>
    /// <remarks>
    /// 自定义图标一并写入，且保留 <c>icons/</c> 这层子目录，恢复时才能回到原位。
    /// </remarks>
    public TransferResult BackupTo(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            var copied = new List<string>();

            foreach (var relativePath in SourcesToArchive())
            {
                CopyPreservingRelativePath(
                    Path.Combine(_paths.RootDirectory, relativePath),
                    destinationDirectory,
                    relativePath);

                copied.Add(relativePath);
            }

            var manifest = new BackupManifest
            {
                ProductVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(),
                CreatedAtUtc = _timeProvider.GetUtcNow(),
                Files = copied,
            };

            File.WriteAllText(
                Path.Combine(destinationDirectory, ManifestFileName),
                JsonSerializer.Serialize(manifest, GlassDeskJsonContext.Default.BackupManifest));

            return new TransferResult
            {
                Succeeded = true,
                Directory = destinationDirectory,
                Files = copied,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = destinationDirectory,
                Error = $"备份失败：{ex.Message}",
            };
        }
    }

    /// <summary>
    /// 从先前由 <see cref="BackupTo"/> 生成的文件夹恢复。
    /// </summary>
    /// <remarks>
    /// 当前数据会先快照到 <c>backup/pre-restore-*</c>，因此误触的
    /// 恢复本身也是可回退的。
    /// </remarks>
    public TransferResult RestoreFrom(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        if (!Directory.Exists(sourceDirectory))
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "所选文件夹不存在。",
            };
        }

        var manifestPath = Path.Combine(sourceDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "所选文件夹中没有 GlassDesk 备份清单，无法确认来源。",
            };
        }

        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath),
                GlassDeskJsonContext.Default.BackupManifest);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = $"备份清单无法读取：{ex.Message}",
            };
        }

        if (manifest is null || !string.Equals(manifest.Product, "GlassDesk", StringComparison.Ordinal))
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "该备份不属于本产品。",
            };
        }

        if (manifest.SchemaVersion > GlassDeskSettings.CurrentSchemaVersion)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = "该备份来自更新的版本，请先升级后再恢复。",
            };
        }

        // 在动任何现有数据之前，先把整份备份读一遍。
        //
        // 只校验清单是不够的：清单可以完好，而里面的设置文件是坏 JSON。那种情况下逐个
        // 拷贝会用坏数据覆盖用户当前正常的配置，然后还报「恢复成功」——用户既丢了
        // 原数据，又以为一切正常。所以宁可整份拒绝。
        var unreadable = FindUnreadableDocuments(sourceDirectory);
        if (unreadable.Count > 0)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                Error = $"备份中的以下文件无法读取，已放弃恢复（现有数据未改动）：{string.Join('、', unreadable)}",
            };
        }

        var safetyCopy = SnapshotCurrent();

        try
        {
            _paths.EnsureCreated();
            var restored = new List<string>();

            // 恢复的清单以**归档里有什么**为准，而不是数据目录里现在有什么：
            // 恢复前缓存往往已经被清空，按现状枚举会一个图标都找不到。
            foreach (var relativePath in RestoreCandidates(sourceDirectory))
            {
                CopyPreservingRelativePath(
                    Path.Combine(sourceDirectory, relativePath),
                    _paths.RootDirectory,
                    relativePath);

                restored.Add(relativePath);
            }

            // 归档里的引用指着备份时的那个数据目录。文件已经复制到位，
            // 但引用不跟着改的话，换台机器或换个目录恢复，图标照样失效——
            // 这正是把文件复制过去解决不了的那一半。
            var repaired = RepairManagedIconReferences();

            return new TransferResult
            {
                Succeeded = true,
                Directory = sourceDirectory,
                Files = restored,
                SafetyCopyDirectory = safetyCopy,
                RepairedIconReferences = repaired,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TransferResult
            {
                Succeeded = false,
                Directory = sourceDirectory,
                SafetyCopyDirectory = safetyCopy,
                Error = $"恢复失败：{ex.Message}",
            };
        }
    }

    /// <summary>逐个试解析备份里的文档，返回其中读不出来的文件名。</summary>
    /// <remarks>
    /// 用文档在磁盘上的真实路径去查校验器，而不是靠文件名猜测，免得哪天改了文件名之后
    /// 校验被悄悄跳过。查不到校验器一律视为「无法校验」，同样拒绝恢复——宁可让恢复
    /// 失败得响亮，也不要静默地不校验就覆盖用户数据。
    ///
    /// 只有那几份 JSON 在此列。自定义图标是字节文件，没有可解析的文档结构，
    /// 不会因为「解不出来」而拒绝整份备份。
    /// </remarks>
    private static List<string> FindUnreadableDocuments(string sourceDirectory)
    {
        var validators = new Dictionary<string, Func<string, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["settings.json"] = text => CanDeserialize(text, GlassDeskJsonContext.Default.GlassDeskSettings),
            ["apps.json"] = text => CanDeserialize(text, GlassDeskJsonContext.Default.AppLibraryData),
            ["dock-layout.json"] = text => CanDeserialize(text, GlassDeskJsonContext.Default.DockLayout),
            ["launcher-layout.json"] = text => CanDeserialize(text, GlassDeskJsonContext.Default.LauncherLayout),
        };

        var unreadable = new List<string>();

        foreach (var relativePath in AppPaths.BackupDocumentRelativePaths())
        {
            var candidate = Path.Combine(sourceDirectory, relativePath);

            // 缺文件不是错误：用户还没自定义过任何东西时拍的备份，本来就会少几份。
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (!validators.TryGetValue(relativePath, out var canDeserialize) || !CanRead(candidate, canDeserialize))
            {
                unreadable.Add(relativePath);
            }
        }

        return unreadable;
    }

    private static bool CanRead(string path, Func<string, bool> canDeserialize)
    {
        try
        {
            return canDeserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanDeserialize<T>(string text, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize(text, typeInfo) is not null;
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

    private string? SnapshotCurrent()
    {
        try
        {
            var stamp = _timeProvider.GetUtcNow().UtcDateTime
                .ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var directory = Path.Combine(_paths.BackupDirectory, $"pre-restore-{stamp}");
            Directory.CreateDirectory(directory);

            foreach (var relativePath in SourcesToArchive())
            {
                CopyPreservingRelativePath(
                    Path.Combine(_paths.RootDirectory, relativePath),
                    directory,
                    relativePath);
            }

            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缺少安全副本不应阻断用户要求的恢复；调用方
            // 只在该路径确实存在时才报告它。
            return null;
        }
    }

    /// <summary>
    /// 把刚恢复的应用库里、指向旧数据目录的托管图标引用改到当前目录并写盘。
    /// </summary>
    /// <remarks>
    /// 自愈逻辑只有一份，在 <see cref="AppLibraryStore.Load"/> 里；
    /// 这里只是**为它写一次盘**——数据层的 Load 从不写文件，
    /// 而恢复完不写回去的话，落盘的 apps.json 仍指着旧目录，
    /// 要等到下一次启动才被修正。
    ///
    /// <para>
    /// 没有需要修的引用就<strong>不写</strong>：文档刚被原样复制过来，
    /// 没理由再改写一遍。写失败也不让整次恢复报失败——文件已经到位，
    /// 引用会在下次启动时再被修正一次。
    /// </para>
    /// </remarks>
    private int RepairManagedIconReferences()
    {
        try
        {
            var store = new AppLibraryStore(_paths.AppLibraryFile);
            store.Load();

            if (store.RepairedIconReferenceCount == 0)
            {
                return 0;
            }

            var repaired = store.RepairedIconReferenceCount;
            store.Save();
            return repaired;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 备份要带走的文件：四份文档（缺哪份跳哪份）加上磁盘上现有的自定义图标。
    /// </summary>
    private IEnumerable<string> SourcesToArchive()
    {
        foreach (var relativePath in AppPaths.BackupDocumentRelativePaths())
        {
            // 这份文档还没有保存过；文件不存在不算失败。
            if (File.Exists(Path.Combine(_paths.RootDirectory, relativePath)))
            {
                yield return relativePath;
            }
        }

        foreach (var relativePath in _paths.CustomIconRelativePaths())
        {
            yield return relativePath;
        }
    }

    /// <summary>
    /// 恢复要写回的文件：归档里有的文档，加上归档里带着的自定义图标。
    /// </summary>
    /// <remarks>
    /// <b>清单以归档为准</b>，不是以数据目录的现状为准——恢复之前缓存往往已经被清空，
    /// 按现状枚举会一个图标都找不到。这也是阶段 3 复核时真实踩到的坑。
    /// </remarks>
    private static IEnumerable<string> RestoreCandidates(string sourceDirectory)
    {
        foreach (var relativePath in AppPaths.BackupDocumentRelativePaths())
        {
            if (File.Exists(Path.Combine(sourceDirectory, relativePath)))
            {
                yield return relativePath;
            }
        }

        foreach (var relativePath in CustomIconsInArchive(sourceDirectory))
        {
            yield return relativePath;
        }
    }

    /// <summary>
    /// 归档里带着的自定义图标。
    /// </summary>
    /// <remarks>
    /// 只认 <c>icons/custom-*</c>，与备份时写进去的那一类一致。
    /// 归档里其它来历的文件一律不理——那不是我们的东西。
    /// </remarks>
    private static IReadOnlyList<string> CustomIconsInArchive(string sourceDirectory)
    {
        var directory = Path.Combine(sourceDirectory, AppPaths.IconCacheSubdirectory);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.GetFiles(directory, "custom-*")
            .Select(file => Path.Combine(AppPaths.IconCacheSubdirectory, Path.GetFileName(file)))];
    }

    /// <summary>
    /// 复制一个文件，并按 <paramref name="relativePath"/> 在目标目录下还原子目录。
    /// </summary>
    /// <remarks>
    /// 自定义图标住在 <c>icons/</c> 子目录里，归档里也必须保持这一层，
    /// 否则恢复时回不到原位。旧版用扁平的名字复制，所以这一层丢了。
    /// </remarks>
    private static void CopyPreservingRelativePath(string source, string destinationDirectory, string relativePath)
    {
        var target = Path.Combine(destinationDirectory, relativePath);
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(source, target, overwrite: true);
    }
}