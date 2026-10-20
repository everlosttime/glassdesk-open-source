using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>前台窗口的铺满判定结果。</summary>
public enum FullscreenVerdict
{
    /// <summary>无法判定（没有前台窗口，或取几何信息失败）。</summary>
    Unknown = 0,

    /// <summary>前台窗口没有铺满它所在的显示器。</summary>
    NotFullscreen = 1,

    /// <summary>前台窗口铺满了它所在的显示器。</summary>
    Fullscreen = 2,

    /// <summary>
    /// 前台窗口属于「不该被当成全屏应用」的一类：桌面、任务栏、
    /// 本产品自己的窗口、已最小化或不可见的窗口。
    /// </summary>
    Excluded = 3,
}

/// <summary>A nullable verdict preserves the prior foreign window during session switches and own menus.</summary>
public readonly record struct PerformanceFullscreenObservation(bool? IsFullscreen, bool BelongsToCurrentProcess);

/// <summary>
/// 外壳报告的用户通知状态，即 <c>SHQueryUserNotificationState</c> 的返回码。
/// </summary>
/// <remarks>
/// 取值与 Win32 的 <c>QUNS_*</c> 常量一一对应，这是有意的：报告里要能直接
/// 和系统文档对上号，而不是多一层自造的名字。
/// </remarks>
public enum UserNotificationState
{
    /// <summary>本次调用失败，或系统返回了本产品不认识的值。</summary>
    Unknown = 0,

    /// <summary>用户不在（已锁屏或正在屏保）。</summary>
    NotPresent = 1,

    /// <summary>正在运行全屏应用，或已启用演示设置。</summary>
    Busy = 2,

    /// <summary>正在运行独占模式的 Direct3D 全屏应用。</summary>
    RunningD3dFullScreen = 3,

    /// <summary>已启用演示设置（不一定是全屏）。</summary>
    PresentationMode = 4,

    /// <summary>正常状态：可以打扰用户。</summary>
    AcceptsNotifications = 5,

    /// <summary>安静时间（新建用户账户后的第一个小时）。</summary>
    QuietTime = 6,

    /// <summary>正在全屏运行应用商店（UWP）应用。</summary>
    App = 7,
}

/// <summary>一次全屏判定的可选设置。</summary>
public sealed record FullscreenOptions
{
    /// <summary>默认设置：排除本进程自己的窗口。</summary>
    public static FullscreenOptions Default { get; } = new();

    /// <summary>
    /// 是否把本进程的窗口排除在外。
    /// </summary>
    /// <remarks>
    /// 产品运行时必须为 <c>true</c>：Dock、启动台、设置窗口一旦被当成「全屏应用」，
    /// 抑制逻辑就会把自己锁死——用户唤出 Dock，Dock 自己的窗口成了前台窗口，
    /// 于是判定为「有全屏应用」，从此再也不响应唤出。
    /// 只有实测工具需要打开它，因为实测要在自己的进程里造全屏窗口。
    /// </remarks>
    public bool ExcludeCurrentProcess { get; init; } = true;

    /// <summary>
    /// 是否把外壳状态（路径二）也并入廉价查询的判定。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 <c>false</c>：触发路径上的高频判断只走窗口几何，因为它稳定、便宜（实测中位 4.5 微秒），
    /// 而外壳调用实测中位 200 微秒上下、最坏 2.5 毫秒，且会随状态波动。
    /// </para>
    /// <para>
    /// 打开它只在一种情况下有价值：独占全屏（D3D）的游戏。那种窗口未必是普通的前台窗口，
    /// 窗口几何可能看不到，而外壳有一条专门的状态（<c>QUNS_RUNNING_D3D_FULL_SCREEN</c>）。
    /// 代价是引入路径二的不可靠面——实测它看不到副屏上的全屏窗口，也见过它报 Busy
    /// 而前台并不是全屏窗口；这两种情况都会让抑制偏严。
    /// </para>
    /// <para>
    /// 注意它只影响 <see cref="FullscreenDetector.IsFullscreenActive"/>（以及
    /// <see cref="FullscreenWatcher"/> 的判定）；<see cref="FullscreenDetector.Probe"/> 无论如何
    /// 都会把两条路径都测出来并分别报告。
    /// </para>
    /// </remarks>
    public bool IncludeShellState { get; init; }
}

/// <summary>前台窗口的身份信息，仅供诊断展示。</summary>
public sealed record ForegroundWindowInfo(
    nint Handle,
    string ClassName,
    string Title,
    uint ProcessId,
    string ProcessName,
    bool IsMaximized)
{
    public override string ToString() =>
        $"句柄 0x{Handle:X}  类名 {ClassName}  进程 {ProcessName}（{ProcessId}）{(IsMaximized ? "  已最大化" : string.Empty)}{(string.IsNullOrEmpty(Title) ? string.Empty : $"  标题「{Title}」")}";
}

/// <summary>路径一的判定结果：前台窗口矩形与所在显示器边界的比较。</summary>
/// <param name="WindowRect"><c>GetWindowRect</c> 的矩形，物理像素。含最大化窗口的不可见调整边框。</param>
/// <param name="VisibleFrame">DWM 报告的可见边框，物理像素。取值失败时为 null。</param>
public sealed record WindowPathResult(
    FullscreenVerdict Verdict,
    string Reason,
    PixelRect? WindowRect,
    PixelRect? VisibleFrame,
    PixelRect? MonitorBounds,
    string MonitorDeviceName,
    TimeSpan Duration)
{
    /// <summary>这条路径的结论；被排除或无法判定时为 null（即「没有结论」）。</summary>
    public bool? IsFullscreen => Verdict switch
    {
        FullscreenVerdict.Fullscreen => true,
        FullscreenVerdict.NotFullscreen => false,
        _ => null,
    };
}

/// <summary>路径二的判定结果：外壳的用户通知状态。</summary>
public sealed record ShellPathResult(
    UserNotificationState State,
    bool IsFullscreen,
    string? Error,
    TimeSpan Duration)
{
    public override string ToString() =>
        Error is null ? $"{State}（{(IsFullscreen ? "全屏" : "非全屏")}）" : $"{State}——{Error}";
}

/// <summary>
/// 两条路径的一次完整探测结果。
/// </summary>
/// <remarks>
/// 两条路径的结论**可能不一致**，而且不一致本身是有信息量的：
/// 例如无边框最大化窗口、独占全屏的游戏、被本产品排除的自身窗口。
/// 所以这里把两条路径的结果原样并列，而不是先合并成一个布尔值。
/// </remarks>
public sealed record FullscreenSnapshot(
    ForegroundWindowInfo Foreground,
    WindowPathResult WindowPath,
    ShellPathResult ShellPath)
{
    /// <summary>
    /// 触发方式（Dock 边缘唤出、热角、鼠标侧键）此刻是否应当被抑制。
    /// </summary>
    /// <remarks>
    /// 两条路径取「或」：任一条认为有全屏应用就抑制。这是刻意的不对称取舍——
    /// 误抑制只是让 Dock 在某个窗口铺满时晚一点出现，用户再动一下鼠标就好；
    /// 误放行则会在看视频、玩游戏时弹出面板并抢走焦点，代价大得多。
    /// </remarks>
    public bool ShouldSuppressTriggers =>
        WindowPath.Verdict == FullscreenVerdict.Fullscreen || ShellPath.IsFullscreen;

    /// <summary>
    /// 两条路径的结论是否一致。
    /// </summary>
    /// <remarks>
    /// 窗口路径被排除（桌面、任务栏、本产品自己的窗口）或无法判定时它没有结论，
    /// 此时不计为「不一致」——但报告里仍然会写出被排除的原因。
    /// </remarks>
    public bool PathsAgree => WindowPath.IsFullscreen is null || WindowPath.IsFullscreen == ShellPath.IsFullscreen;

    /// <summary>一行式摘要，供诊断报告使用。</summary>
    public string Describe() =>
        $"路径一 {DescribeVerdict(WindowPath.Verdict)}，路径二 {ShellPath.State}，"
        + (PathsAgree ? "两路一致" : "两路不一致");

    private static string DescribeVerdict(FullscreenVerdict verdict) => verdict switch
    {
        FullscreenVerdict.Fullscreen => "全屏",
        FullscreenVerdict.NotFullscreen => "非全屏",
        FullscreenVerdict.Excluded => "已排除（无结论）",
        _ => "无法判定",
    };
}

/// <summary>
/// 判断前台是否有一个铺满整屏的应用在运行。
/// </summary>
/// <remarks>
/// <para>
/// Dock 的边缘唤出、热角、鼠标侧键都必须在全屏应用前台时被抑制，
/// 否则用户看视频或玩游戏时会被自己的桌面美化工具打断。这个类型就是那条抑制条件的唯一落点。
/// </para>
/// <para>
/// 检测走两条互相独立的路径，两条的结果会**如实并列报告**，不假装它们等价：
/// </para>
/// <list type="number">
/// <item>
/// 前台窗口的矩形与所在显示器的边界比较。全部是廉价的 Win32 查询（大约几十微秒），
/// 因此它就是「当前是否全屏」这个高频问题的答案。
/// 关键细节是用 DWM 的**可见边框**而不是 <c>GetWindowRect</c> 参与判定，
/// 见 <see cref="DwmApi.DwmwaExtendedFrameBounds"/> 的说明。
/// </item>
/// <item>
/// <c>SHQueryUserNotificationState</c>，也就是 Windows 决定「要不要弹通知」时用的那套判断。
/// 它更接近系统自身的语义（独占全屏的游戏会被它识别），代价是一次跨进程的外壳调用。
/// </item>
/// </list>
/// <para>
/// 这里**没有任何定时器**：判定要么由调用方按需查询，要么由
/// <see cref="FullscreenWatcher"/> 通过 <c>SetWinEventHook</c> 在事件到达时触发。
/// </para>
/// </remarks>
public static class FullscreenDetector
{
    /// <summary>
    /// 铺满判定允许的像素误差。
    /// </summary>
    /// <remarks>
    /// 允许 1 像素是因为窗口边框与显示器边界的取整方向可能不同（缩放比例不是整数时尤其如此），
    /// 而 1 像素在画面上看不出来。再放宽就会开始把「几乎铺满」的窗口算成全屏了。
    /// </remarks>
    private const int CoverageTolerance = 1;

    /// <summary>
    /// 外壳自己的窗口类名：桌面、任务栏。它们铺满屏幕是常态，不构成全屏应用。
    /// </summary>
    private static readonly string[] ShellWindowClasses =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    ];

    /// <summary>
    /// 完整探测一次：两条路径都走，各自带上耗时。
    /// </summary>
    /// <remarks>
    /// 给诊断和「需要解释为什么」的调用方使用。触发路径上的高频查询请用
    /// <see cref="IsFullscreenActive"/>，它不碰外壳。
    /// </remarks>
    public static FullscreenSnapshot Probe(FullscreenOptions? options = null)
    {
        var effective = options ?? FullscreenOptions.Default;
        var handle = User32.GetForegroundWindow();

        var windowWatch = Stopwatch.StartNew();
        var (verdict, reason, geometry) = EvaluateWindowPath(handle, effective);
        windowWatch.Stop();

        var shellWatch = Stopwatch.StartNew();
        var (state, error) = QueryShellStateCore();
        shellWatch.Stop();

        var foreground = new ForegroundWindowInfo(
            handle,
            User32.ReadClassName(handle),
            User32.ReadWindowText(handle),
            GetProcessId(handle),
            ReadProcessName(handle),
            (User32.GetWindowLong(handle, User32.GwlStyle) & User32.WsMaximize) != 0);

        return new FullscreenSnapshot(
            foreground,
            new WindowPathResult(
                verdict,
                reason,
                geometry.WindowRect,
                geometry.VisibleFrame,
                geometry.MonitorBounds,
                geometry.MonitorDeviceName,
                windowWatch.Elapsed),
            new ShellPathResult(state, IsFullscreenState(state), error, shellWatch.Elapsed));
    }

    /// <summary>
    /// 廉价查询：前台窗口此刻是否铺满整屏。
    /// </summary>
    /// <remarks>
    /// 默认只走窗口几何（不调用外壳、不分配字符串），适合放在每次唤出判断上。
    /// 需要把外壳状态也算进来时打开 <see cref="FullscreenOptions.IncludeShellState"/>，
    /// 代价与理由见那里。
    /// </remarks>
    public static bool IsFullscreenActive(FullscreenOptions? options = null)
    {
        var effective = options ?? FullscreenOptions.Default;

        if (GetForegroundVerdict(effective) == FullscreenVerdict.Fullscreen)
        {
            return true;
        }

        // 几何不认为全屏时，才轮到可选的第二条路径——顺序是有意的：
        // 常见情况（没有全屏应用）只需付出几何那几微秒。
        return effective.IncludeShellState && IsFullscreenState(QueryShellStateCore().State);
    }

    /// <summary>Read one foreground handle consistently. Only the specific exclusive-D3D shell state augments geometry.</summary>
    public static PerformanceFullscreenObservation ReadPerformanceObservation()
    {
        var handle = User32.GetForegroundWindow();
        if (handle == 0) return new(null, false);
        if (GetProcessId(handle) == (uint)Environment.ProcessId) return new(null, true);
        if (IsExcluded(handle, FullscreenOptions.Default, out _)) return new(false, false);
        if (!TryGetGeometry(handle, out var geometry, out _)) return new(null, false);
        var coversMonitor = Covers(geometry.VisibleFrame ?? geometry.WindowRect, geometry.MonitorBounds);
        return new(coversMonitor || QueryShellStateCore().State == UserNotificationState.RunningD3dFullScreen, false);
    }

    /// <summary>
    /// 廉价查询的完整形态：给出前台窗口的判定结论（含「已排除」这一档）。
    /// </summary>
    public static FullscreenVerdict GetForegroundVerdict(FullscreenOptions? options = null)
    {
        var effective = options ?? FullscreenOptions.Default;
        var handle = User32.GetForegroundWindow();

        if (handle == 0)
        {
            return FullscreenVerdict.Unknown;
        }

        if (IsExcluded(handle, effective, out _))
        {
            return FullscreenVerdict.Excluded;
        }

        if (!TryGetGeometry(handle, out var geometry, out _))
        {
            return FullscreenVerdict.Unknown;
        }

        // 可见边框取不到时退回 GetWindowRect：宁可稍微激进一点，
        // 也不要因为一个查询失败就放行触发。
        var frame = geometry.VisibleFrame ?? geometry.WindowRect;

        return Covers(frame, geometry.MonitorBounds)
            ? FullscreenVerdict.Fullscreen
            : FullscreenVerdict.NotFullscreen;
    }

    /// <summary>询问外壳当前的用户通知状态。</summary>
    public static UserNotificationState QueryShellState() => QueryShellStateCore().State;

    /// <summary>
    /// 某个外壳状态是否意味着「有全屏应用」。
    /// </summary>
    /// <remarks>
    /// <c>PresentationMode</c> 不并进来：它表示开了演示设置，并不代表有窗口铺满屏幕；
    /// <c>NotPresent</c> / <c>QuietTime</c> 是「别打扰」，也不是全屏。
    /// 这三者属于「不要打扰」而不是「全屏」，调用方若需要更宽的抑制条件，
    /// 可以自行判断 <see cref="UserNotificationState"/> 的原始值。
    /// </remarks>
    public static bool IsFullscreenState(UserNotificationState state) =>
        state is UserNotificationState.Busy
            or UserNotificationState.RunningD3dFullScreen
            or UserNotificationState.App;

    /// <summary>
    /// 任务栏此刻是否处于自动隐藏。
    /// </summary>
    /// <remarks>
    /// 这个信息用于解释一种**无法区分**的情形：任务栏自动隐藏时，最大化窗口的可见边框
    /// 也覆盖整块显示器，与真正的全屏在几何上完全一致，任何基于矩形的判定都不可能分开它们。
    /// 查询失败时返回 false。
    /// </remarks>
    public static bool IsTaskbarAutoHidden()
    {
        try
        {
            var data = new Shell32.AppBarData
            {
                Size = (uint)Marshal.SizeOf<Shell32.AppBarData>(),
            };

            return (Shell32.SHAppBarMessage(Shell32.AbmGetState, ref data) & Shell32.AbsAutoHide) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>路径一的完整形态：判定 + 解释 + 几何数据。</summary>
    private static (FullscreenVerdict Verdict, string Reason, WindowGeometry Geometry) EvaluateWindowPath(nint handle, FullscreenOptions options)
    {
        if (handle == 0)
        {
            return (FullscreenVerdict.Unknown, "当前没有前台窗口（会话正在切换，或已锁屏）。", default);
        }

        if (IsExcluded(handle, options, out var exclusion))
        {
            return (FullscreenVerdict.Excluded, exclusion, default);
        }

        if (!TryGetGeometry(handle, out var geometry, out var failure))
        {
            return (FullscreenVerdict.Unknown, failure, default);
        }

        var visible = geometry.VisibleFrame ?? geometry.WindowRect;

        if (Covers(visible, geometry.MonitorBounds))
        {
            var note = geometry.IsMaximized
                ? "（该窗口带最大化状态；任务栏自动隐藏时，最大化窗口与全屏在几何上无法区分）"
                : string.Empty;
            return (
                FullscreenVerdict.Fullscreen,
                $"可见边框 {visible} 覆盖了 {geometry.MonitorDeviceName} 的全部 {geometry.MonitorBounds}{note}",
                geometry);
        }

        if (Covers(geometry.WindowRect, geometry.MonitorBounds))
        {
            // 这就是只看 GetWindowRect 会踩的坑，值得单独解释一句：
            // 最大化窗口的 GetWindowRect 含被推到屏幕外的不可见调整边框。
            return (
                FullscreenVerdict.NotFullscreen,
                $"GetWindowRect {geometry.WindowRect} 在数值上覆盖了整屏，但可见边框 {visible} 停在 "
                    + $"{geometry.MonitorDeviceName} 的工作区之内——这是最大化窗口的不可见调整边框，不是全屏",
                geometry);
        }

        return (
            FullscreenVerdict.NotFullscreen,
            $"可见边框 {visible} 未覆盖 {geometry.MonitorDeviceName} 的全部 {geometry.MonitorBounds}",
            geometry);
    }

    /// <summary>
    /// 判断前台窗口是否属于「不该被当成全屏应用」的一类。
    /// </summary>
    /// <remarks>
    /// 顺序有讲究：先做最便宜的句柄与类名比较，最后才查 DWM。
    /// 这条路径会被高频调用，每一次多余的系统调用都要说得出理由。
    /// </remarks>
    private static bool IsExcluded(nint handle, FullscreenOptions options, out string reason)
    {
        if (handle == User32.GetShellWindow() || handle == User32.GetDesktopWindow())
        {
            reason = "前台窗口是桌面（Progman/WorkerW），桌面铺满屏幕是常态，不算全屏应用。";
            return true;
        }

        if (IsShellWindowClass(handle))
        {
            reason = $"前台窗口是外壳的任务栏（类名 {User32.ReadClassName(handle)}），不算全屏应用。";
            return true;
        }

        if (options.ExcludeCurrentProcess)
        {
            var processId = GetProcessId(handle);
            if (processId != 0 && processId == (uint)Environment.ProcessId)
            {
                reason = "前台窗口属于本产品自己，不算全屏应用（否则 Dock 会把自己的窗口当成全屏而不再唤出）。";
                return true;
            }
        }

        if (User32.IsIconic(handle))
        {
            reason = "前台窗口已最小化，不可能铺满屏幕。";
            return true;
        }

        if (!User32.IsWindowVisible(handle))
        {
            reason = "前台窗口不可见。";
            return true;
        }

        if (DwmApi.IsCloaked(handle))
        {
            reason = "前台窗口已被 DWM 隐藏（cloaked），通常是挂起的 UWP 应用留下的空壳。";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>窗口类名是否属于外壳的桌面/任务栏。不分配字符串。</summary>
    private static bool IsShellWindowClass(nint handle)
    {
        Span<char> buffer = stackalloc char[64];
        var length = User32.GetClassName(handle, buffer);

        if (length <= 0)
        {
            return false;
        }

        var name = buffer[..length];

        foreach (var shellClass in ShellWindowClasses)
        {
            if (name.Equals(shellClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>取窗口矩形、可见边框、所在显示器边界与设备名。</summary>
    private static bool TryGetGeometry(nint handle, out WindowGeometry geometry, out string failure)
    {
        geometry = default;
        failure = string.Empty;

        if (!Display32.GetWindowRect(handle, out var windowRect))
        {
            failure = "GetWindowRect 失败，取不到前台窗口的矩形。";
            return false;
        }

        var monitor = Display32.MonitorFromWindow(handle, Display32.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            failure = "MonitorFromWindow 失败，无法确定前台窗口在哪块显示器上。";
            return false;
        }

        var info = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };

        if (!Display32.GetMonitorInfo(monitor, ref info))
        {
            failure = "GetMonitorInfo 失败，取不到显示器边界。";
            return false;
        }

        PixelRect? visibleFrame = DwmApi.TryGetExtendedFrameBounds(handle, out var extended)
            ? ToPixelRect(extended)
            : null;

        geometry = new WindowGeometry(
            ToPixelRect(windowRect),
            visibleFrame,
            ToPixelRect(info.Monitor),
            info.DeviceName,
            (User32.GetWindowLong(handle, User32.GwlStyle) & User32.WsMaximize) != 0);

        return true;
    }

    /// <summary>窗口矩形是否覆盖了整块显示器。</summary>
    private static bool Covers(PixelRect window, PixelRect monitor) =>
        window.X <= monitor.X + CoverageTolerance
        && window.Y <= monitor.Y + CoverageTolerance
        && window.Right >= monitor.Right - CoverageTolerance
        && window.Bottom >= monitor.Bottom - CoverageTolerance;

    private static (UserNotificationState State, string? Error) QueryShellStateCore()
    {
        try
        {
            var result = Shell32.SHQueryUserNotificationState(out var raw);

            if (result < 0)
            {
                return (UserNotificationState.Unknown, $"SHQueryUserNotificationState 返回失败 HRESULT 0x{result:X8}");
            }

            var state = (UserNotificationState)raw;

            return IsKnownState(state)
                ? (state, null)
                : (UserNotificationState.Unknown, $"SHQueryUserNotificationState 返回了未知状态 {raw}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (UserNotificationState.Unknown, "本机的 shell32.dll 没有 SHQueryUserNotificationState。");
        }
    }

    private static bool IsKnownState(UserNotificationState state) => state switch
    {
        UserNotificationState.NotPresent or
        UserNotificationState.Busy or
        UserNotificationState.RunningD3dFullScreen or
        UserNotificationState.PresentationMode or
        UserNotificationState.AcceptsNotifications or
        UserNotificationState.QuietTime or
        UserNotificationState.App => true,
        _ => false,
    };

    private static uint GetProcessId(nint handle) =>
        User32.GetWindowThreadProcessId(handle, out var processId) == 0 ? 0 : processId;

    /// <summary>取前台窗口所属进程的名字，仅用于诊断报告。取不到时返回「?」。</summary>
    private static string ReadProcessName(nint handle)
    {
        var processId = GetProcessId(handle);

        if (processId == 0)
        {
            return "?";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程可能刚好退出，或权限不足。诊断报告不值得为此中断。
            return "?";
        }
    }

    private static PixelRect ToPixelRect(Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    /// <summary>路径一用到的几何数据。物理像素。</summary>
    private readonly record struct WindowGeometry(
        PixelRect WindowRect,
        PixelRect? VisibleFrame,
        PixelRect MonitorBounds,
        string MonitorDeviceName,
        bool IsMaximized);
}
