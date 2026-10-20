namespace GlassDesk.Core.Storage;

/// <summary>
/// 本产品在自身用户数据目录下读写的全部位置。
/// </summary>
/// <remarks>
/// 这个文件夹名只属于我们。这里没有任何路径指向参考产品的存储
/// （<c>%APPDATA%\MacDock</c> 或启动台的 Electron 用户数据），因此安装
/// GlassDesk 不会干扰已有的 MacDock 或 Ym Launcher 安装。
/// 这种隔离是计划书的硬性要求，
/// 也是基线中一次真实事故的起因：
/// 当时的开发版本改写了已安装启动器的自启动项。
///
/// 布局文件与应用库刻意分开：布局损坏不应
/// 让用户丢掉自己的应用列表。
/// </remarks>
public sealed class AppPaths
{
    /// <summary>漫游应用数据文件夹下的目录名。</summary>
    public const string ProductFolderName = "GlassDesk";

    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>产品数据目录的绝对路径。</summary>
    public string RootDirectory { get; }

    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public string AppLibraryFile => Path.Combine(RootDirectory, "apps.json");

    public string DockLayoutFile => Path.Combine(RootDirectory, "dock-layout.json");

    public string LauncherLayoutFile => Path.Combine(RootDirectory, "launcher-layout.json");

    /// <summary>提取出的图标文件与用户自备的图标文件。</summary>
    public string IconCacheDirectory => Path.Combine(RootDirectory, IconCacheSubdirectory);

    /// <summary>
    /// 图标缓存目录相对 <see cref="RootDirectory"/> 的位置。
    /// </summary>
    /// <remarks>
    /// 备份归档沿用同一层子目录，恢复时才能回到原位。集中成一个常量，
    /// 免得归档层自己拼一个写法不一致的路径出来。
    /// </remarks>
    public const string IconCacheSubdirectory = "icons";

    /// <summary>本产品托管的自定义图标文件名的公共前缀。</summary>
    /// <remarks>
    /// 由 <c>IconCache.ImportCustomIcon</c> 写盘时使用；
    /// 判断「这个路径是不是我们托管的」也靠它，两处必须一致。
    /// </remarks>
    public const string ManagedCustomIconPrefix = "custom-";

    /// <summary>
    /// 判断一个路径是不是本产品托管的自定义图标（由 IconCache 复制进 icons/ 的那份）。
    /// </summary>
    /// <remarks>
    /// 只看文件名前缀。条目上存的路径<strong>永远</strong>是我们自己复制出来的那份
    /// （用户选的原始文件从不直接引用），所以前缀一致就足以判定。
    /// 指向别处的路径不属于我们托管，改写它就是破坏用户数据。
    /// </remarks>
    public static bool IsManagedCustomIconPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Path.GetFileName(path).StartsWith(ManagedCustomIconPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>某个图标文件名在指定数据目录下的托管位置。</summary>
    public static string ManagedCustomIconPath(string dataRoot, string fileName)
        => Path.Combine(dataRoot, IconCacheSubdirectory, fileName);

    /// <summary>用户从设置中心主动创建的备份。</summary>
    public string BackupDirectory => Path.Combine(RootDirectory, "backup");

    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>
    /// 进行实验性系统改动（目前只有任务栏拦截）之前写入的状态，
    /// 以便崩溃后能在下次启动时撤销。
    /// </summary>
    public string RecoveryDirectory => Path.Combine(RootDirectory, "recovery");

    /// <summary>当前用户的数据目录：<c>%APPDATA%\GlassDesk</c>。</summary>
    public static AppPaths ForCurrentUser()
    {
        // Acceptance data is portable and never points at the installed profile.
        return new AppPaths(Path.Combine(AppContext.BaseDirectory, "Data"));
    }

    /// <summary>创建目录树。可以重复调用。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }

    /// <summary>
    /// 构成完整备份的<strong>可解析文档</strong>，以相对数据目录的路径表示。
    /// </summary>
    /// <remarks>
    /// 同一个相对路径既用作归档内的位置，也用作恢复时的目标位置。
    /// 这四份是名字固定的文档，恢复前能逐份校验；自定义图标是另一类
    /// （<see cref="CustomIconRelativePaths"/>），是字节文件，没有文档结构可校验。
    /// </remarks>
    public static IReadOnlyList<string> BackupDocumentRelativePaths() =>
    [
        "settings.json",
        "apps.json",
        "dock-layout.json",
        "launcher-layout.json",
    ];

    /// <summary>
    /// 数据目录里<strong>现有</strong>的自定义图标，以相对 <see cref="RootDirectory"/> 的路径表示。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>自定义图标必须进备份。</b>它们不是可再生缓存，是从用户选的文件复制进来的，
    /// 源文件可能早已不存在；丢了它们，恢复之后那些条目就真的没有图标了，
    /// 而备份当时还会报「成功」。
    /// </para>
    /// <para>
    /// 刻意<strong>只</strong>带 <c>custom-*</c> 这一类。icons/ 下按图标来源哈希命名的
    /// PNG 可以随时按来源重新提取，不算用户数据；目录里的其它文件更不是我们的，
    /// 不该被顺带拷走。
    /// </para>
    /// <para>
    /// 这个列表描述的是<strong>现在磁盘上有什么</strong>，所以只对备份与恢复前快照有意义。
    /// 恢复时该以<strong>归档里有什么</strong>为准，见 <see cref="BackupService"/>——
    /// 恢复之前缓存往往已经被清空，按现状枚举会一个图标都找不到。
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> CustomIconRelativePaths()
    {
        var iconsDirectory = IconCacheDirectory;

        if (!Directory.Exists(iconsDirectory))
        {
            return [];
        }

        // 名字要与 IconCache.ImportCustomIcon 写盘时用的一致。
        return [.. Directory.GetFiles(iconsDirectory, "custom-*")
            .Select(file => Path.GetRelativePath(RootDirectory, file))];
    }
}