using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>
/// 打开应用库里的条目，并在它已经开着的时候优先把窗口提上来。
/// </summary>
/// <remarks>
/// 四类条目走同一条外壳路径，理由见 <see cref="ShellExecutor"/>。
///
/// <para>
/// <b>失效条目绝不静默失败。</b>目标不在了就返回「找不到目标」并附带可读的说明，
/// 而不是抛异常或什么也不做——用户按下图标之后必须能得到一个解释。
/// 同时也不会因为库里存在失效条目而拒绝启动别的条目。
/// </para>
/// </remarks>
public sealed class LaunchService : ILaunchService
{
    private readonly AppLibraryStore _library;

    public LaunchService(AppLibraryStore library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>最近一次成功启动得到的进程 id，供「已运行就激活」在同一次会话里复用。</summary>
    public uint LastLaunchedProcessId { get; private set; }

    public LaunchResult LaunchItem(string itemId)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return new LaunchResult
            {
                Succeeded = false,
                Failure = LaunchFailureReason.UnknownItem,
                Message = "应用库里没有这一条记录。",
            };
        }

        // 网址没有「文件在不在」这回事，直接交给外壳。
        if (entry.Kind == AppItemKind.Url)
        {
            return OpenWithShell(entry);
        }

        if (IsTargetGone(entry))
        {
            return new LaunchResult
            {
                Succeeded = false,
                Failure = LaunchFailureReason.TargetMissing,
                Message = $"目标已不在原处，请重新定位：{entry.Target}",
            };
        }

        return OpenWithShell(entry);
    }

    /// <summary>
    /// 优先把已有的窗口提到前面；没有窗口才启动。
    /// </summary>
    /// <remarks>
    /// 窗口匹配分两条路，规则见 <see cref="WindowMatcher"/>：带显式身份的条目
    /// （打包应用与 UWP 的快捷方式）按身份匹配；普通可执行程序按进程映像路径。
    /// 都不适用时老实启动——退化行为见 <see cref="WindowMatcher"/> 的说明。
    /// </remarks>
    public ActivationResult ActivateOrLaunch(string itemId)
        => ActivateOrLaunch(itemId, minimizeIfForeground: false);

    /// <summary>Dock 图标可以把唯一匹配的前台窗口最小化；后台或已最小化窗口仍然前置。</summary>
    public ActivationResult ActivateOrLaunch(string itemId, bool minimizeIfForeground)
    {
        var entry = _library.Find(itemId);

        if (entry is null)
        {
            return new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = "应用库里没有这一条记录。",
            };
        }

        if (entry.Kind == AppItemKind.Url)
        {
            var plain = LaunchItem(itemId);

            return plain.Succeeded
                ? new ActivationResult { Outcome = ActivationOutcome.Launched }
                : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = plain.Message };
        }

        if (IsTargetGone(entry))
        {
            return new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = $"目标已不在原处，请重新定位：{entry.Target}",
            };
        }

        var canMatch = !string.IsNullOrWhiteSpace(WindowMatcher.ResolveAumid(entry))
            || WindowMatcher.CanMatchByImage(entry);

        if (!canMatch)
        {
            var plain = LaunchItem(itemId);

            return plain.Succeeded
                ? new ActivationResult { Outcome = ActivationOutcome.Launched }
                : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = plain.Message };
        }

        var windows = WindowMatcher.FindWindows(entry);

        switch (windows.Count)
        {
            case 1:
                if (minimizeIfForeground
                    && windows[0].Handle == WindowControl.GetForegroundWindow()
                    && !WindowControl.IsMinimized(windows[0].Handle))
                {
                    _ = WindowControl.Minimize(windows[0].Handle);
                    return WindowControl.IsMinimized(windows[0].Handle)
                        ? new ActivationResult { Outcome = ActivationOutcome.Minimized }
                        : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = "未能最小化该窗口，请重试。" };
                }

                return Raise(new WindowCandidateInfo(windows[0].Handle, windows[0].Title, windows[0].ProcessId, windows[0].IsMinimized));

            case > 1:
                return new ActivationResult
                {
                    Outcome = ActivationOutcome.MultipleWindowsFound,
                    Candidates = windows
                        .Select(window => new WindowCandidate { Handle = window.Handle, Title = window.Title })
                        .ToList(),
                };

            default:
                var launched = OpenWithShell(entry);

                return launched.Succeeded
                    ? new ActivationResult { Outcome = ActivationOutcome.Launched }
                    : new ActivationResult { Outcome = ActivationOutcome.Failed, Message = launched.Message };
        }
    }

    /// <summary>
    /// 把一组候选窗口里的某一个提到前面。供多窗口选择列表使用。
    /// </summary>
    /// <remarks>
    /// 三种结局分得清楚，因为它们对用户意味着不同的事：
    /// 窗口已经关掉（列表是几秒前列的）、系统拒绝前置（可让用户从任务栏切）、成功。
    /// 合成一句「失败了」会让用户不知道该做什么。
    /// </remarks>
    public static ActivationResult ActivateWindow(nint handle)
    {
        if (handle == 0)
        {
            return new ActivationResult { Outcome = ActivationOutcome.Failed, Message = "没有指定窗口。" };
        }

        if (!WindowControl.IsAlive(handle))
        {
            return new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = "那个窗口已经关掉了，请重新点一次图标。",
            };
        }

        return WindowLocator.TryBringToForeground(handle)
            ? new ActivationResult { Outcome = ActivationOutcome.Activated }
            : new ActivationResult
            {
                Outcome = ActivationOutcome.Failed,
                Message = "系统拒绝了把该窗口切到前台，请从任务栏切换。",
            };
    }

    /// <summary>目标现在还在不在。</summary>
    public static bool IsTargetGone(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Kind == AppItemKind.Url || string.IsNullOrWhiteSpace(entry.Target))
        {
            return false;
        }

        try
        {
            return !File.Exists(entry.Target) && !Directory.Exists(entry.Target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到不等于不存在——可能是权限问题。按「存在」处理，
            // 让启动去撞真实的错误，而不是在这里先拦下来给出错误的结论。
            return false;
        }
    }

    private static ActivationResult Raise(WindowCandidateInfo window)
    {
        if (WindowLocator.TryBringToForeground(window.Handle))
        {
            return new ActivationResult { Outcome = ActivationOutcome.Activated };
        }

        return new ActivationResult
        {
            Outcome = ActivationOutcome.Failed,
            Message = "找到了正在运行的窗口，但系统拒绝了把它切到前台，请从任务栏切换。",
        };
    }

    private LaunchResult OpenWithShell(AppEntry entry)
    {
        var outcome = ShellExecutor.Open(entry.Target, entry.Arguments, entry.WorkingDirectory);

        if (outcome.Started)
        {
            LastLaunchedProcessId = outcome.ProcessId;
            return new LaunchResult { Succeeded = true };
        }

        return new LaunchResult
        {
            Succeeded = false,
            Failure = Classify(outcome),
            Message = Describe(outcome, entry),
        };
    }

    private static LaunchFailureReason Classify(ShellLaunchOutcome outcome) => outcome switch
    {
        { IsFileNotFound: true } => LaunchFailureReason.TargetMissing,
        { IsAccessDenied: true } => LaunchFailureReason.AccessDenied,
        { IsNoAssociation: true } => LaunchFailureReason.Unsupported,
        _ => LaunchFailureReason.ShellRejected,
    };

private static string Describe(ShellLaunchOutcome outcome, AppEntry entry)
    {
        var explanation = outcome switch
        {
            { IsFileNotFound: true } => $"找不到目标：{entry.Target}",
            { IsAccessDenied: true } => "系统拒绝了这次打开，目标可能需要管理员权限，或者用户取消了提权提示。",
            { IsNoAssociation: true } => $"系统不知道该怎么打开这一类文件：{entry.Target}",
            _ => "外壳没有完成这次打开。",
        };

        // 原始错误码一并给出：它能被搜索到具体含义，而「外壳拒绝了」不能。
        // 排查这类问题时它是唯一有用的线索。
        return $"{explanation}（外壳错误码 {outcome.ErrorCode}）";
    }
}
