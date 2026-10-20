using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>
/// 「这条记录的已运行窗口是哪些」的唯一判定者。
/// </summary>
/// <remarks>
/// 匹配策略按阶段 4 探针实测结论定（证据：<c>docs/evidence/阶段4-探针实测输出.txt</c>）：
///
/// <list type="number">
/// <item>条目带显式身份（导入时从 .lnk 读出）→ 按窗口的<strong>有效身份</strong>匹配；
/// 这覆盖打包应用（窗口身份来自归属进程的包身份）与 UWP（窗口身份挂在
/// ApplicationFrameHost 的框架窗口上）。有效身份不同的窗口一律不算——
/// 两个 UWP 应用共享同一个宿主进程，凭宿主进程路径强行匹配会把
/// A 应用的窗口错认成 B 应用的。</item>
/// <item>条目不带身份、目标是可执行文件 → 按进程映像路径匹配（阶段 3 已验证的路径）。</item>
/// <item>两者都不适用 → 视为「没有已运行窗口」，点击老实启动。
/// 退化行为如实写进交付报告：单实例应用自己会把已有窗口调到前面，
/// 多实例程序（如经控制台宿主打开的 cmd）会多开一个。</item>
/// </list>
///
/// <para>
/// 匹配本身是对一个<strong>窗口快照</strong>的纯函数（<see cref="MatchWindows"/>），
/// 因此可以在没有窗口的环境下单测；活查询只是「枚举 + 快照匹配」的组合。
/// </para>
/// </remarks>
public static class WindowMatcher
{
    /// <summary>
    /// 在给定快照里找出这条记录此刻已运行的窗口。
    /// </summary>
    /// <param name="entry">应用库记录，不得为 null。</param>
    /// <param name="snapshot">候选窗口快照，通常来自一次
    /// <see cref="WindowLocator.EnumerateTopLevelWindows"/>。</param>
    /// <param name="aumid">
    /// 条目的有效身份；为 null 时按映像路径匹配。调用方可用
    /// <see cref="ResolveAumid"/> 解析。
    /// </param>
    public static IReadOnlyList<WindowIdentityInfo> MatchWindows(
        AppEntry entry,
        IReadOnlyList<WindowIdentityInfo> snapshot,
        string? aumid)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.IsNullOrWhiteSpace(aumid))
        {
            return MatchByIdentity(snapshot, aumid);
        }

        if (CanMatchByImage(entry))
        {
            var target = NormalizePath(entry.Target);
            return snapshot
                .Where(window => string.Equals(window.ImagePath, target, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return [];
    }

    /// <summary>活查询：枚举当前窗口再匹配。单次点击的场景用。</summary>
    /// <param name="entry">应用库记录。</param>
    /// <param name="identityCache">
    /// 条目身份的会话内缓存；目标仍是 .lnk 的条目按需读一次显式身份。
    /// 传 null 则每次现读。
    /// </param>
    public static IReadOnlyList<WindowIdentityInfo> FindWindows(
        AppEntry entry,
        IDictionary<string, string?>? identityCache = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return MatchWindows(entry, WindowLocator.EnumerateTopLevelWindows(), ResolveAumid(entry, identityCache));
    }

    /// <summary>
    /// 解析条目的有效身份：优先用导入时存下的，目标还是 .lnk 时按需现读。
    /// </summary>
    public static string? ResolveAumid(AppEntry entry, IDictionary<string, string?>? identityCache = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.IsNullOrWhiteSpace(entry.AppUserModelId))
        {
            return entry.AppUserModelId;
        }

        if (!entry.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (identityCache is not null && identityCache.TryGetValue(entry.Id, out var cached))
        {
            return cached;
        }

        var resolved = WindowIdentity.TryGetShortcutAumid(entry.Target);

        if (identityCache is not null)
        {
            identityCache[entry.Id] = resolved;
        }

        return resolved;
    }

    /// <summary>
    /// 按身份筛窗口。窗口有效身份与条目身份<strong>不一致</strong>时不算匹配，
    /// 也不回退到映像路径——这正是「两侧都有身份但不一致，不得凭宿主进程
    /// 路径强行匹配」这条规则的落点。
    /// </summary>
    private static List<WindowIdentityInfo> MatchByIdentity(
        IReadOnlyList<WindowIdentityInfo> windows,
        string aumid)
    {
        var matches = new List<WindowIdentityInfo>();

        foreach (var window in windows)
        {
            if (string.Equals(window.EffectiveAumid, aumid, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(window);
            }

            // 到这里的窗口身份是空或不同。身份不同的窗口即使映像路径碰巧等于
            // 目标也不算——身份是应用自己声明的，声明对不上就不可信。
            // 身份为空的窗口不在此列：经典 Win32 程序两侧都读不出身份，
            // 它们的归属仍由映像路径判定（MatchWindows 的第二条路径）。
        }

        return matches;
    }

    /// <summary>
    /// 这条记录能不能按进程映像匹配窗口。
    /// </summary>
    /// <remarks>
    /// 只在目标是可执行文件时才匹配。<c>.bat</c> 之类的目标本身是脚本，
    /// 真正跑起来的是 cmd.exe，拿它的路径去比对永远匹配不上，
    /// 于是每次点击都会再起一个——与其做这种无效匹配，不如老实地每次都启动。
    /// </remarks>
    public static bool CanMatchByImage(AppEntry entry)
        => entry.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>折叠成可比较的形式。与 <see cref="WindowLocator"/> 的规则一致。</summary>
    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }
}
