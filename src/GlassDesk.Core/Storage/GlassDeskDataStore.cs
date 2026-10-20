using GlassDesk.Core.Models;

namespace GlassDesk.Core.Storage;

/// <summary>加载全部文档的结果，措辞面向用户。</summary>
public sealed class DataLoadReport
{
    public LoadStatus LibraryStatus { get; internal set; }
    /// <summary>值得展示的消息。为空表示所有文件都顺利加载。</summary>
    public List<string> Notices { get; } = [];

    /// <summary>损坏文件的保留位置（如果有）。</summary>
    public List<string> PreservedFiles { get; } = [];

    public bool HasNotices => Notices.Count > 0;
}

/// <summary>
/// 产品的数据层：设置、共用应用库、两份布局，以及
/// 覆盖上述全部内容的备份与还原。
/// </summary>
/// <remarks>
/// 这个类型以下的一切都以文件为对象；它以上的一切（Dock、启动台、设置中心）
/// 只与这些存储交互而不是直接操作路径。正是这一点让计划书中
/// 「注册表、进程与窗口句柄调用不散落在界面事件处理器中」
/// 这条规则得以强制落实。
/// </remarks>
public sealed class GlassDeskDataStore
{
    public GlassDeskDataStore(AppPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Paths = paths;
        Settings = new SettingsStore(paths.SettingsFile, timeProvider);
        Library = new AppLibraryStore(paths.AppLibraryFile, timeProvider);
        Layouts = new LayoutStore(paths.DockLayoutFile, paths.LauncherLayoutFile, timeProvider);
        Backup = new BackupService(paths, timeProvider);
    }

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public AppLibraryStore Library { get; }

    public LayoutStore Layouts { get; }

    public BackupService Backup { get; }

    /// <summary>
    /// 创建数据目录并加载全部内容。
    /// </summary>
    /// <remarks>
    /// 面对损坏或缺失的文件绝不抛出异常：各存储回退到自己的备份或默认值，
    /// 并通过返回的报告说明发生了什么。
    /// 损坏的设置文件绝不能阻止应用启动。
    /// </remarks>
    public DataLoadReport LoadAll()
    {
        Paths.EnsureCreated();

        var report = new DataLoadReport();

        Describe(report, Settings.Load(), "设置");
        var libraryOutcome = Library.Load();
        report.LibraryStatus = libraryOutcome.Status;
        Describe(report, libraryOutcome, "应用库");
        Describe(report, Layouts.LoadDock(), "Dock 布局");
        Describe(report, Layouts.LoadLauncher(), "启动台布局");

        // 异常条目不会拦住启动，但必须让用户知道文件里有东西坏了。
        if (Library.AnomalousEntryCount > 0)
        {
            report.Notices.Add(
                $"应用库中有 {Library.AnomalousEntryCount} 条记录无法使用（可能是空条目或缺字段），已跳过；记录仍保留在文件中。");
        }

        // 自愈改了用户数据，必须可见。典型起因是数据目录被整体搬过、
        // 或备份恢复到了另一个目录：引用还指着旧位置。
        if (Library.RepairedIconReferenceCount > 0)
        {
            report.Notices.Add(
                $"有 {Library.RepairedIconReferenceCount} 条记录的自定义图标引用指向旧的数据目录，已改回当前目录。");
        }

        return report;
    }

    /// <summary>
    /// 从应用库删除一条记录并清除所有布局对它的引用，
    /// 然后持久化全部三份受影响的文档。
    /// </summary>
    /// <remarks>
    /// 这是「从应用库删除」动作。它刻意不是 Dock 移除命令所调用的目标——
    /// 后者只取消固定，必须让记录保持完好。
    /// </remarks>
    public bool DeleteFromLibrary(string? itemId)
    {
        if (!Library.Remove(itemId))
        {
            return false;
        }

        Layouts.PurgeItem(itemId);

        Library.Save();
        Layouts.SaveDock();
        Layouts.SaveLauncher();
        return true;
    }

    private static void Describe<T>(DataLoadReport report, LoadOutcome<T> outcome, string label)
        where T : class
    {
        if (outcome.PreservedFile is not null)
        {
            report.PreservedFiles.Add(outcome.PreservedFile);
        }

        switch (outcome.Status)
        {
            case LoadStatus.Loaded:
            case LoadStatus.FirstRun:
                break;

            case LoadStatus.RecoveredFromBackup:
                report.Notices.Add($"{label}文件损坏，已从备份恢复。");
                break;

            case LoadStatus.Unrecoverable:
                report.Notices.Add($"{label}文件与其备份均无法读取，已使用默认值。");
                break;

            default:
                report.Notices.Add($"{label}文件状态未知。");
                break;
        }

        if (outcome.PreservedFile is not null)
        {
            report.Notices.Add($"损坏的{label}文件已保留，便于排查。");
        }
    }
}
