using System.Security.Cryptography;
using System.Text;
using GlassDesk.Core.Imaging;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>一次图标落盘的结果。</summary>
/// <param name="Path">缓存文件的绝对路径。</param>
/// <param name="Width">图标宽。</param>
/// <param name="Height">图标高。</param>
/// <param name="CreatedFile">这次新建了文件；false 表示复用了已经存在的那一份。</param>
public sealed record CachedIcon(string Path, int Width, int Height, bool CreatedFile);

/// <summary>清空缓存目录的结果。</summary>
/// <param name="Removed">删掉的可再生缓存文件数。</param>
/// <param name="LeftAlone">不属于缓存、因此留下的文件数。</param>
public sealed record IconCacheClearResult(int Removed, int LeftAlone)
{
    /// <summary>目录里除了我们自己的缓存之外还有别的东西，值得让用户知道。</summary>
    public bool LeftOthers => LeftAlone > 0;
}

/// <summary>
/// 把提取出来的图标写成 PNG 存进缓存目录。
/// </summary>
/// <remarks>
/// 缓存文件名由<strong>图标来源</strong>的哈希决定，不是由条目 id 决定。
/// 于是同一份来源只落一个文件：共享同一个目标的几十个「开始」菜单快捷方式
/// 只写一次，桌面上的同一个程序被导入两次也只写一次。
/// 用条目 id 命名则会产生几十份逐字节相同的 PNG。
///
/// <para>
/// 存成 PNG 而不是原始 BGRA：文件小一个数量级，而且是可以直接打开查看的标准格式，
/// 排查「图标为什么不对」时不用另外写工具。
/// </para>
/// </remarks>
public sealed class IconCache
{
    // 变更图标处理方式时更新此版本，避免继续复用旧尺寸的 PNG 缓存。
    private const string CacheKeyVersion = "installer-resource-v3";
    private readonly AppPaths _paths;

    public IconCache(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// 目录里<strong>可再生</strong>缓存文件的数量，即 <see cref="Clear"/> 会删掉的那一类。
    /// </summary>
    /// <remarks>
    /// 刻意不含自定义图标：它们不是缓存。报告里「缓存目录现有 N 个」
    /// 指的是这个数，否则清空之后数字不归零，读报告的人会以为清理没生效。
    /// </remarks>
    public int FileCount => Directory.Exists(_paths.IconCacheDirectory)
        ? Directory.GetFiles(_paths.IconCacheDirectory).Count(IsRegenerableCacheFile)
        : 0;

    /// <summary>
    /// 按图标来源取（必要时先提取再写）缓存文件。
    /// </summary>
    /// <returns>取不到图标时返回 null——这只是界面少一张图，不是错误。</returns>
    public CachedIcon? EnsureFor(string? iconSource)
    {
        if (string.IsNullOrWhiteSpace(iconSource))
        {
            return null;
        }

        var target = PathFor(iconSource);

        // 已经缓存过就直接复用。判断「是否为空图」只对新建的文件做，
        // 否则每次调用都要把整个 PNG 读一遍。
        if (File.Exists(target))
        {
            var (width, height) = ReadPngSize(target);
            return width > 0 ? new CachedIcon(target, width, height, CreatedFile: false) : null;
        }

        var extracted = IconExtractor.Extract(iconSource);
        if (extracted is null || extracted.VisiblePixelCount == 0)
        {
            // 全透明的图不该被缓存：存下来之后每一条用到它的记录都会显示成空白，
            // 而正确的表现是「没有图标」，让界面自己决定画什么占位图。
            return null;
        }

        var normalized = IconCanvasNormalizer.Normalize(extracted.Bgra, extracted.Width, extracted.Height);

        try
        {
            WriteAtomically(target, PngWriter.EncodeBgra(normalized.Bgra, normalized.Width, normalized.Height));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return new CachedIcon(target, normalized.Width, normalized.Height, CreatedFile: true);
    }

    /// <summary>
    /// 把用户自选的图标文件复制进缓存目录。
    /// </summary>
    /// <remarks>
    /// 复制而不是只记下用户选的那个路径：用户可能事后把源文件删掉或搬走，
    /// 那时记着外部路径就等于图标丢了。复制进来之后，
    /// 数据目录本身就是完整的、可随备份一起搬走的。
    ///
    /// <para>
    /// 这里只校验文件头，不做完整解码——解码需要图像库，而产品不引第三方依赖。
    /// 认不出来的格式直接拒绝，而不是复制一个后面必然加载失败的
    /// 文件进来。
    /// </para>
    /// </remarks>
    /// <returns>复制成功时返回缓存文件的绝对路径；格式不认识或读不了时返回 null。</returns>
    public string? ImportCustomIcon(string sourcePath, string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var format = ImageFormatSniffer.Detect(sourcePath);
        if (format is null)
        {
            return null;
        }

        var extension = ImageFormatSniffer.FileExtensionFor(format.Value);

        // 名字里带上条目 id，这样同一个用户图标被两条记录选用时互不覆盖。
        // 前缀来自 AppPaths：判断「这个路径是不是我们托管的」用的是同一个常量，
        // 两处各写一份迟早会不一致。
        var target = Path.Combine(
            _paths.IconCacheDirectory,
            AppPaths.ManagedCustomIconPrefix + Sanitize(itemId) + extension);

        try
        {
            Directory.CreateDirectory(_paths.IconCacheDirectory);
            File.Copy(sourcePath, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // 换格式重选会留下旧扩展名的文件，清掉它，否则目录里堆的是看不到的垃圾。
        foreach (var stale in Directory.GetFiles(_paths.IconCacheDirectory, AppPaths.ManagedCustomIconPrefix + Sanitize(itemId) + ".*"))
        {
            if (!string.Equals(stale, target, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(stale);
            }
        }

        return target;
    }

    /// <summary>删掉某条记录用过的自定义图标文件。</summary>
    public void RemoveCustomIcon(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Directory.Exists(_paths.IconCacheDirectory))
        {
            return;
        }

        foreach (var stale in Directory.GetFiles(_paths.IconCacheDirectory, AppPaths.ManagedCustomIconPrefix + Sanitize(itemId) + ".*"))
        {
            TryDelete(stale);
        }
    }

    /// <summary>
    /// 清掉<strong>可再生</strong>的缓存文件。条目上的 <c>IconSource</c> 会保留，因此可以重新提取。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>先判断，再删。</b>旧版先 <see cref="File.Delete"/> 再用判断结果计数，
    /// 那个判断于是纯粹成了统计——目录里的每个文件都会被删掉，
    /// 包括用户自己放进来的、和本产品的自定义图标。
    /// </para>
    /// <para>
    /// <b>自定义图标（<c>custom-*</c>）刻意不清。</b>它不是可再生缓存：
    /// 是从用户选的文件复制进来的，源文件可能早已不存在，删了就找不回来。
    /// 要撤掉某条记录的自定义图标走 <see cref="RemoveCustomIcon"/>，那是按条目精确做的。
    /// </para>
    /// </remarks>
    public IconCacheClearResult Clear()
    {
        if (!Directory.Exists(_paths.IconCacheDirectory))
        {
            return new IconCacheClearResult(0, 0);
        }

        var removed = 0;
        var leftAlone = 0;

        foreach (var file in Directory.GetFiles(_paths.IconCacheDirectory))
        {
            if (!IsRegenerableCacheFile(file))
            {
                leftAlone++;
                continue;
            }

            if (TryDelete(file))
            {
                removed++;
            }
            else
            {
                // 删不掉的文件留着，不因为清理失败而中断。
                leftAlone++;
            }
        }

        return new IconCacheClearResult(removed, leftAlone);
    }

    /// <summary>
    /// 是不是由 <see cref="EnsureFor"/> 生成、可以按图标来源重新提取的缓存文件。
    /// </summary>
    /// <remarks>
    /// 只认「32 个十六进制位 + <c>.png</c>」这一种名字。要求逐位是十六进制，
    /// 而不是只比长度——避免把恰好 36 个字符的别的东西当成缓存。
    /// </remarks>
    private static bool IsRegenerableCacheFile(string path)
    {
        var name = Path.GetFileName(path);

        if (name.Length != 36 || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < 32; i++)
        {
            if (!Uri.IsHexDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 某个图标来源对应的缓存文件位置。
    /// </summary>
    /// <remarks>
    /// 公开出来是为了让「这个来源的图标在哪」在缓存之外也能被回答——
    /// 诊断与联系表都要在不触发提取的前提下问出路径。
    /// </remarks>
    public string PathFor(string iconSource)
    {
        // 序号里带上来源本身的哈希，这样「图标来源变了」会自然换一个新文件，
        // 不会拿旧的糊弄。
        var cacheIdentity = $"{CacheKeyVersion}\0{iconSource.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity));
        var name = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return Path.Combine(_paths.IconCacheDirectory, name + ".png");
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        // PNGS 签名 8 字节，IHDR 长度 4 字节，类型 4 字节，然后是两个大端 int。
        Span<byte> header = stackalloc byte[24];

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return (0, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }

        if (!header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return (0, 0);
        }

        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        return (width, height);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        }

        return builder.ToString();
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着。清理失败不该影响用户正在做的操作。
            return false;
        }
    }

    /// <summary>把条目上已经不再需要的自定义图标清掉，并挂上新的。</summary>
    public bool ApplyCustomIcon(AppEntry entry, string? customIconPath)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(customIconPath))
        {
            if (entry.CustomIconPath is not null)
            {
                entry.CustomIconPath = null;
                RemoveCustomIcon(entry.Id);
                return true;
            }

            return false;
        }

        var cached = ImportCustomIcon(customIconPath, entry.Id);
        if (cached is null)
        {
            return false;
        }

        entry.CustomIconPath = cached;
        return true;
    }
}
