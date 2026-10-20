using System.Text;

namespace GlassDesk.Core.Storage;

/// <summary>
/// 写入文件时，让读取方永远看不到写了一半的文档。
/// </summary>
/// <remarks>
/// 顺序是：写一个同目录的临时文件，把它一路刷到磁盘上，再用
/// <see cref="File.Replace(string, string, string, bool)"/> 换到目标位置，该调用
/// 同时会把原有内容降级为旁边的 <c>.bak</c>。因此崩溃之后剩下的
/// 要么是旧文档、要么是新文档——绝不会是截断的混合物——并且始终
/// 有一份最后已知可用的副本。
/// </remarks>
public static class AtomicFile
{
    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// 把 <paramref name="contents"/> 以不带 BOM 的 UTF-8 写入 <paramref name="path"/>。
    /// </summary>
    /// <exception cref="StorageException">文件无法替换。</exception>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + TempSuffix;
        var backupPath = path + BackupSuffix;

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                // 刷到物理设备上，这样突然断电不会留下一个
                // 存在但为空的临时文件。
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(tempPath);
            throw new StorageException(
                $"无法保存 {Path.GetFileName(path)}：{ex.Message}",
                path,
                ex);
        }
    }

    /// <summary>读取文件；文件不存在时返回 null。</summary>
    public static string? ReadAllTextOrNull(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(
                $"无法读取 {Path.GetFileName(path)}：{ex.Message}",
                path,
                ex);
        }
    }

    /// <summary>
    /// 把读不出来的文件挪到一旁而不是删掉，用户仍可自行挽救
    /// 或上报。返回新位置；挪不动则返回 null。
    /// </summary>
    public static string? PreserveCorrupt(string path, DateTimeOffset timestamp)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var stamp = timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(directory, $"{name}.corrupt-{stamp}{extension}");

        try
        {
            // 绝不覆盖更早的挽救副本；一秒内出现两次失败
            // 不太可能，但弄丢第一份比文件名古怪更糟。
            var candidate = target;
            var counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name}.corrupt-{stamp}-{counter}{extension}");
                counter++;
            }

            File.Move(path, candidate);
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只做清理；真正要紧的是原始失败，而它已经被上报了。
        }
    }
}