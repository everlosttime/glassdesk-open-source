using GlassDesk.Core.Contracts;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>
/// 阶段 3 那套应用服务的一个入口。
/// </summary>
/// <remarks>
/// 存在的意义是让宿主与两个界面不必各自把存储、图标缓存、导入与启动拼一遍——
/// 拼错的方式有很多种（用不同的 <see cref="AppLibraryStore"/> 实例、
/// 忘了先 <c>EnsureCreated</c>、把缓存目录指到别处），而它们都不会报错，
/// 只会表现为「导入的东西重启后不见了」。
///
/// <para>
/// 它<strong>不</strong>持有任何窗口、不引用 WPF。Dock 与启动台都通过它做事，
/// 于是「界面不直接碰系统」这条结构规则在阶段 3 之后仍然成立。
/// </para>
/// </remarks>
public sealed class AppServiceHost : IDisposable, IImportObserver
{
    private readonly GlassDeskDataStore _data;
    private readonly IImportObserver? _externalObserver;

    public AppServiceHost(GlassDeskDataStore data, IImportObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
        _externalObserver = observer;

        // 图标缓存目录必须先存在，否则第一次提取图标会写失败，
        // 而失败被设计成「安静跳过」，于是表现为「所有条目都没有图标」。
        data.Paths.EnsureCreated();

        Icons = new IconCache(data.Paths);
        Import = new ImportService(data.Library, Icons, this, data.Layouts,
            () => Math.Clamp(data.Settings.Current.Launcher.Rows, 2, 10)
                * Math.Clamp(data.Settings.Current.Launcher.Columns, 2, 12));
        Launch = new LaunchService(data.Library);
        Maintenance = new LibraryMaintenance(data.Library, Icons, this);
        Running = new RunningAppTracker(data.Library);
        Search = new SearchService(() => data.Library.Items, () => data.Layouts.Launcher);
        Editor = new AppEntryEditor(data.Library, data.Paths, OnLibraryChanged);
    }

    public IconCache Icons { get; }

    public ImportService Import { get; }

    public LaunchService Launch { get; }

    public LibraryMaintenance Maintenance { get; }

    public SearchService Search { get; }

    public AppEntryEditor Editor { get; }

    public event EventHandler? LibraryChanged;

    public void OnLibraryChanged()
    {
        _externalObserver?.OnLibraryChanged();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 运行状态跟踪（D-07）。调用 <see cref="RunningAppTracker.Start"/> 之后
    /// 才开始监听窗口事件；在那之前也可以按需调 <see cref="RunningAppTracker.Reconcile"/>。
    /// </summary>
    public RunningAppTracker Running { get; }

    /// <summary>Dock 与启动台各自的布局（固定顺序等）。</summary>
    public LayoutStore Layouts => _data.Layouts;

    /// <summary>
    /// 这次会话共用的那一份应用库。
    /// </summary>
    /// <remarks>
    /// 所有命令都从这一个实例读，而不是各加载各的。各加载各的会出现
    /// 「导入之后立刻列出却看不到新条目」——因为那个列表读的是导入之前
    /// 加载的那一份快照。这类问题不会报错，只是数据看起来不对。
    /// </remarks>
    public AppLibraryStore Library => _data.Library;

    public AppPaths Paths => _data.Paths;

    /// <summary>
    /// 按 <see cref="IImportService"/> 这个稳定形状暴露导入能力。
    /// </summary>
    /// <remarks>
    /// 界面拿这个而不是 <see cref="ImportService"/>，这样界面代码依赖的是契约，
    /// 不是具体实现；实现将来怎么换都不影响它。
    /// </remarks>
    public IImportService ImportContract => Import;

    /// <summary>按 <see cref="ILaunchService"/> 这个稳定形状暴露启动能力。</summary>
    public ILaunchService LaunchContract => Launch;

    /// <summary>扫描「开始」菜单，并报告这次看到的规模。</summary>
    public static StartMenuScanResult ScanStartMenu() => StartMenuScanner.Scan();

    /// <summary>
    /// 把扫描结果里选中的条目导入应用库。
    /// </summary>
    /// <remarks>
    /// 名称用快捷方式的外壳显示名，而不是解析出来的目标文件名：用户在「开始」菜单上
    /// 看到的是「画图」，目标却叫 <c>mspaint.exe</c>。外壳显示名也会保留本地化文字。
    /// </remarks>
    public ImportResult ImportStartMenuShortcuts(IEnumerable<StartMenuShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);

        var requests = shortcuts
            .Select(shortcut => new ImportRequest
            {
                Source = shortcut.Path,
                DisplayName = shortcut.Name,
            })
            .ToList();

        return Import.ImportItems(requests);
    }

    public void Dispose()
    {
        // 目前没有需要显式释放的非托管资源：COM 对象都在各自的调用里成对释放了，
        // 图标句柄也一样。运行状态跟踪的事件线程在这里收尾。
        Running.Dispose();
    }
}
