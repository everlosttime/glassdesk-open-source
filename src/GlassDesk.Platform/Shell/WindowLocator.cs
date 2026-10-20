using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>一个候选的顶层窗口。</summary>
/// <param name="Handle">窗口句柄。</param>
/// <param name="Title">标题，可能为空。</param>
/// <param name="ProcessId">所属进程 id。</param>
/// <param name="IsMinimized">当前处于最小化。</param>
public sealed record WindowCandidateInfo(nint Handle, string Title, uint ProcessId, bool IsMinimized);

/// <summary>一个候选顶层窗口连同它两侧的身份信息。</summary>
/// <param name="ImagePath">窗口归属进程的映像完整路径；读不到为 null。</param>
/// <param name="WindowAumid">窗口上显式设置的 AppUserModelID；没设置为 null。</param>
/// <param name="ProcessAumid">归属进程的 AppUserModelID；普通 Win32 进程为 null。</param>
public sealed record WindowIdentityInfo(
    nint Handle,
    string Title,
    uint ProcessId,
    bool IsMinimized,
    string? ImagePath,
    string? WindowAumid,
    string? ProcessAumid)
{
    /// <summary>
    /// 这个窗口的「有效身份」。
    /// </summary>
    /// <remarks>
    /// 窗口上显式设置的身份优先——它比进程的身份更具体。最典型的例子是
    /// ApplicationFrameHost 托管的 UWP 窗口：宿主进程自己没有应用身份，
    /// 而窗口上带的是宿主应用真正的身份。两边都有但不一致的情形
    /// 按「不可信」处理，调用方不得再用宿主进程路径强行匹配，
    /// 规则见 <c>RunningAppTracker</c>。
    /// </remarks>
    public string? EffectiveAumid => WindowAumid ?? ProcessAumid;
}

/// <summary>
/// 找出属于某个程序的顶层窗口，并把它带到前台。
/// </summary>
/// <remarks>
/// 归属判定用「进程映像的完整路径」，不用进程名。进程名的粒度太粗——
/// 用户可能装了多个版本的同一个程序，也可能有别的程序碰巧同名。
/// 路径是唯一的。
///
/// <para>
/// 取映像路径用 <c>PROCESS_QUERY_LIMITED_INFORMATION</c>，
/// 它对<strong>已提权</strong>的进程同样有效；换成 <c>PROCESS_QUERY_INFORMATION</c>
/// 会直接吃拒绝访问，于是「以管理员身份运行的程序」永远匹配不到窗口。
/// </para>
/// </remarks>
public static class WindowLocator
{
    /// <summary>
    /// 枚举所有「有效身份」等于给定 AppUserModelID 的候选顶层窗口。
    /// </summary>
    /// <remarks>
    /// 有效的身份 = 窗口上显式设置的身份，读不到时退回归属进程的包身份
    /// （<see cref="WindowIdentityInfo.EffectiveAumid"/>）。阶段 4 探针实测：
    /// UWP 框架窗口（ApplicationFrameHost 宿主）靠窗口侧读出，打包应用
    /// 靠进程侧读出，两条路合起来才覆盖。找不到匹配时返回空列表——
    /// 调用方按「没有已运行窗口」处理，绝不回退到宿主进程的映像路径：
    /// 两个 UWP 应用共享同一个宿主进程，按宿主匹配会把 A 应用的窗口
    /// 错认成 B 应用的。
    /// </remarks>
    public static IReadOnlyList<WindowCandidateInfo> FindWindowsForAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return [];
        }

        var matches = new List<WindowCandidateInfo>();

        foreach (var window in EnumerateTopLevelWindows())
        {
            if (string.Equals(window.EffectiveAumid, aumid, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new WindowCandidateInfo(window.Handle, window.Title, window.ProcessId, window.IsMinimized));
            }
        }

        return matches;
    }

    /// <summary>
    /// 枚举所有与给定映像路径匹配的、可见且有标题的顶层窗口。
    /// </summary>
    /// <param name="imagePath">目标的完整路径。为 null 或空时返回空列表。</param>
    public static IReadOnlyList<WindowCandidateInfo> FindWindowsForImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return [];
        }

        var normalized = NormalizePath(imagePath);
        var matches = new List<WindowCandidateInfo>();

        // 同一个进程可能开多个窗口，映像路径问一次就够了。
        var processPaths = new Dictionary<uint, string?>();

        try
        {
            _ = User32.EnumWindows(
                (window, parameter) =>
                {
                    if (!IsCandidateWindow(window))
                    {
                        return 1;
                    }

                    _ = User32.GetWindowThreadProcessId(window, out var processId);
                    if (processId == 0)
                    {
                        return 1;
                    }

                    if (!processPaths.TryGetValue(processId, out var path))
                    {
                        path = TryReadImagePath(processId);
                        processPaths[processId] = path;
                    }

                    if (path is not null && string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new WindowCandidateInfo(
                            window,
                            User32.ReadWindowText(window),
                            processId,
                            User32.IsIconic(window)));
                    }

                    return 1;
                },
                0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        return matches;
    }

    /// <summary>
    /// 枚举所有值得当作「某个应用的窗口」的顶层窗口，并带上两侧身份。
    /// </summary>
    /// <remarks>
    /// 运行状态判定（D-07）的调和来源：事件只当触发信号，
    /// 真正的窗口集合以这里的枚举结果为准。身份读取有成本
    /// （每个窗口一次属性存储查询、每个进程一次句柄打开），
    /// 但候选窗口数量级通常在几十，调和又只由事件触发，可接受。
    /// </remarks>
    public static IReadOnlyList<WindowIdentityInfo> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowIdentityInfo>();
        var imagePaths = new Dictionary<uint, string?>();
        var processAumids = new Dictionary<uint, string?>();

        try
        {
            _ = User32.EnumWindows(
                (window, parameter) =>
                {
                    if (!IsCandidateWindow(window))
                    {
                        return 1;
                    }

                    _ = User32.GetWindowThreadProcessId(window, out var processId);
                    if (processId == 0)
                    {
                        return 1;
                    }

                    if (!imagePaths.TryGetValue(processId, out var imagePath))
                    {
                        imagePath = TryReadImagePath(processId);
                        imagePaths[processId] = imagePath;
                    }

                    if (!processAumids.TryGetValue(processId, out var processAumid))
                    {
                        processAumid = WindowIdentity.TryGetProcessAumid(processId);
                        processAumids[processId] = processAumid;
                    }

                    windows.Add(new WindowIdentityInfo(
                        window,
                        User32.ReadWindowText(window),
                        processId,
                        User32.IsIconic(window),
                        imagePath,
                        WindowIdentity.TryGetWindowAumid(window),
                        processAumid));

                    return 1;
                },
                0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        return windows;
    }

    /// <summary>
    /// 把窗口带到前台。
    /// </summary>
    /// <remarks>
    /// Windows 的<strong>前台锁定</strong>会拒绝来自「此刻未被用户操作」的进程的前台切换，
    /// 返回 <c>false</c> 而不是报错。用户点程序坞图标时我们的进程本来就拿到了前台权限，
    /// 所以正常路径直接成功；失败时按下面顺序降级：
    /// <list type="number">
    /// <item>最小化的先还原——对最小化窗口调用前台切换本来就不生效；</item>
    /// <item>把本线程接到前台窗口所属线程的输入队列上，再试一次；</item>
    /// <item>还不行就用 <c>BringWindowToTop</c>，至少让它在 z 序上往前。</item>
    /// </list>
    /// 三步都不成，就如实返回 <c>false</c>，由调用方降级成「已启动但未能前置」的提示，
    /// 而不是报错。
    /// </remarks>
    public static bool TryBringToForeground(nint window)
    {
        if (window == 0 || !User32.IsWindow(window))
        {
            return false;
        }

        try
        {
            if (User32.IsIconic(window))
            {
                _ = User32.ShowWindow(window, User32.SwRestore);
            }

            if (User32.SetForegroundWindow(window))
            {
                return true;
            }

            return TryWithAttachedInput(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryWithAttachedInput(nint window)
    {
        var foreground = User32.GetForegroundWindow();
        var ourThread = Kernel32.GetCurrentThreadId();
        var targetThread = foreground == 0 ? 0 : User32.GetWindowThreadProcessId(foreground, out _);

        if (targetThread == 0 || targetThread == ourThread)
        {
            return User32.BringWindowToTop(window);
        }

        if (!User32.AttachThreadInput(ourThread, targetThread, true))
        {
            return User32.BringWindowToTop(window);
        }

        try
        {
            _ = User32.BringWindowToTop(window);
            return User32.SetForegroundWindow(window);
        }
        finally
        {
            // 不成对解开会把两个线程的输入队列永久绑在一起——
            // 表现为键盘焦点行为错乱，而且很难查。
            _ = User32.AttachThreadInput(ourThread, targetThread, false);
        }
    }

    /// <summary>
    /// 一个窗口值得当作「这个程序的窗口」候选吗。
    /// </summary>
    /// <remarks>
    /// 三条都要满足：可见、有标题、不是工具窗口。最后一条尤其重要——
    /// 很多程序都留着一个隐藏的、无标题的消息窗口，把它们算进来会让
    /// 「只有一个窗口」变成「有多个窗口」，于是程序坞弹出选择列表。
    /// </remarks>
    private static bool IsCandidateWindow(nint window)
    {
        if (!User32.IsWindowVisible(window))
        {
            return false;
        }

        if (User32.GetWindow(window, User32.GwOwner) != 0)
        {
            return false;
        }

        if (User32.ReadWindowText(window).Length == 0)
        {
            return false;
        }

        // WS_EX_TOOLWINDOW：不属于任务栏的辅助窗口。
        const int GwlExStyle = -20;
        const int WsExToolWindow = 0x00000080;

        var extendedStyle = User32.GetWindowLong(window, GwlExStyle);
        return (extendedStyle & WsExToolWindow) == 0;
    }

    private static string? TryReadImagePath(uint processId)
    {
        var handle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            // 先用一个够大的缓冲区一次问完；长路径下 MAX_PATH 不够。
            var buffer = new char[1024];
            var size = (uint)buffer.Length;

            if (!Kernel32.QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return null;
            }

            return size == 0 ? null : NormalizePath(new string(buffer, 0, (int)size));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            _ = Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 折叠成可比较的形式。
    /// </summary>
    /// <remarks>
    /// 映像路径可能是 <c>\??\C:\...</c> 这种设备形式（取决于 32 位/64 位与查询方式），
    /// 也可能带 <c>\\?\</c> 前缀。不剥掉的话两边永远不相等。
    /// </remarks>
    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();

        foreach (var prefix in new[] { @"\??\", @"\\?\", @"\SystemRoot\" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[prefix.Length..];

                if (prefix == @"\SystemRoot\")
                {
                    trimmed = @"C:\Windows\" + trimmed;
                }

                break;
            }
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return expanded;
        }
    }
}