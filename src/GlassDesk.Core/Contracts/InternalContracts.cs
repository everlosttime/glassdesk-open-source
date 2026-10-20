using GlassDesk.Core.Models;

namespace GlassDesk.Core.Contracts;

/// <summary>
/// UI 编写代码时所依赖的稳定内部接口面。
/// </summary>
/// <remarks>
/// 这些都是进程内接口；本产品不对外暴露任何网络服务。它们存在的意义，是让
/// 窗口代码去请求某个服务来做事，而不是就地做注册表、进程或
/// 窗口句柄的操作，这是计划书里的一条结构规则。
///
/// 备份与还原已经由
/// <see cref="Storage.BackupService"/> 真正实现。其余接口在这里按约定的形状声明，
/// 各自的成员上标明了实现阶段；阶段 1 骨架里还没有任何东西
/// 假装已经满足它们。
/// </remarks>
public static class ContractPhases
{
    public const string Import = "阶段 3";
    public const string Launch = "阶段 3";
    public const string Search = "阶段 5";
    public const string Layout = "阶段 4 / 5";
    public const string Appearance = "阶段 2 / 6";
}

/// <summary>用户要求带进应用库的一样东西。</summary>
public sealed class ImportRequest
{
    /// <summary>绝对路径；当 <see cref="IsUrl"/> 置位时则是一个 URL。</summary>
    public string Source { get; set; } = string.Empty;

    public bool IsUrl { get; set; }

    /// <summary>用户在导入时改了名，就用它覆盖推导出来的名称。</summary>
    public string? DisplayName { get; set; }
}

public enum ImportFailureReason
{
    NotFound = 0,
    UnsupportedType = 1,
    AccessDenied = 2,
    ShortcutUnreadable = 3,
    Other = 4,
}

public sealed class ImportFailure
{
    public string Source { get; init; } = string.Empty;

    public ImportFailureReason Reason { get; init; }

    /// <summary>可以放心显示在该条目旁边的消息。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>一次导入请求中新增、重复与失败的结果。</summary>
public sealed class ImportResult
{
    public IReadOnlyList<AppEntry> Added { get; init; } = [];

    public IReadOnlyList<AppEntry> Duplicates { get; init; } = [];

    public IReadOnlyList<ImportFailure> Failures { get; init; } = [];
}

public enum LaunchFailureReason
{
    UnknownItem = 0,
    TargetMissing = 1,
    AccessDenied = 2,
    ShellRejected = 3,

    /// <summary>目标的格式我们不知道该怎么打开。</summary>
    Unsupported = 4,
}

public sealed class LaunchResult
{
    public bool Succeeded { get; init; }

    public LaunchFailureReason? Failure { get; init; }

    /// <summary>给用户看的解释。绝不能是原始的异常消息。</summary>
    public string? Message { get; init; }
}

/// <summary>优先复用已有窗口、而不是再启动一个实例的结果。</summary>
public enum ActivationOutcome
{
    /// <summary>已有窗口被带到了前台。</summary>
    Activated = 0,

    /// <summary>没有匹配的窗口，于是启动了应用。</summary>
    Launched = 1,

    /// <summary>有多个窗口匹配，必须由用户来选择。</summary>
    MultipleWindowsFound = 2,

    Failed = 3,

    /// <summary>再次点击前台应用的 Dock 图标，窗口被最小化。</summary>
    Minimized = 4,
}

public sealed class ActivationResult
{
    public ActivationOutcome Outcome { get; init; }

    /// <summary>出现 <see cref="ActivationOutcome.MultipleWindowsFound"/> 时的候选窗口。</summary>
    public IReadOnlyList<WindowCandidate> Candidates { get; init; } = [];

    public string? Message { get; init; }
}

/// <summary>可能属于某个应用库条目的窗口。</summary>
public sealed class WindowCandidate
{
    public nint Handle { get; init; }

    public string Title { get; init; } = string.Empty;
}

public enum SearchMatchKind
{
    /// <summary>查询词与名称完全相同。</summary>
    ExactName = 0,

    /// <summary>名称以查询词开头。</summary>
    NamePrefix = 1,

    /// <summary>全拼匹配。</summary>
    Pinyin = 2,

    /// <summary>拼音首字母匹配。</summary>
    PinyinInitials = 3,

    /// <summary>名称中某处包含查询词。</summary>
    NameContains = 4,

    /// <summary>别名或目标路径匹配。</summary>
    Alias = 5,
}

/// <summary>一条已排序的搜索命中。排序由服务负责。</summary>
public sealed class SearchHit
{
    public AppEntry Item { get; init; } = null!;

    public SearchMatchKind MatchKind { get; init; }

    /// <summary>
    /// 相对分值。仅在一次结果集内排序时有意义；
    /// 分值的刻度不属于契约的一部分。
    /// </summary>
    public int Score { get; init; }
}

/// <summary>导入应用、快捷方式、文件夹、文件与 URL。</summary>
public interface IImportService
{
    /// <summary>导入绝不损坏现有应用库；失败按条目逐条报告。</summary>
    ImportResult ImportItems(IReadOnlyList<ImportRequest> requests);
}

/// <summary>打开应用库条目，并优先复用已有窗口。</summary>
public interface ILaunchService
{
    /// <summary>按其种类打开目标。</summary>
    LaunchResult LaunchItem(string itemId);

    /// <summary>把已有窗口提到前面；若没有则启动应用。</summary>
    ActivationResult ActivateOrLaunch(string itemId);
}

/// <summary>搜索共享应用库。完全在本地运行；无遥测，不联网。</summary>
public interface ISearchService
{
    IReadOnlyList<SearchHit> SearchApps(string query, int limit);
}

/// <summary>
/// 一次调用就应用完整的布局变更。调用方提交的是最终排布，
/// 而不是一串增量编辑，这样拖到一半被放弃的操作
/// 就不会留下存了一半的布局。
/// </summary>
public interface ILayoutService
{
    void UpdateLayout(DockLayout dock, LauncherLayout launcher);
}

/// <summary>把外观变更一次推送给每个已打开的窗口。</summary>
public interface IAppearanceService
{
    void ApplyAppearance(Models.Settings.AppearanceSettings appearance);
}
