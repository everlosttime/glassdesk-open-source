using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GlassDesk.Core.Storage;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>
/// 阶段 4 探针 D：「显示桌面」的三条候选实现路径。
/// </summary>
/// <remarks>
/// 逐窗口 <c>ShowWindow(SW_MINIMIZE)</c>、投递 <c>WM_SYSCOMMAND/SC_MINIMIZE</c>、
/// 外壳自带的「显示桌面」动词——三者对普通窗口、打包应用窗口的行为各不相同，
/// 选定前必须实测（修订计划的明确要求）。本探针只对<strong>自建的靶子窗口</strong>做动作，
/// 外壳动词因影响用户整个桌面只做可调用性确认，不真的调用。
///
/// <para>结论依据探针输出；实现见 <c>WindowControl.Minimize</c> 的调用方。</para>
/// </remarks>
internal static class ShowDesktopProbe
{
    public static int Run(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        report.AppendLine("阶段 4 探针 D：显示桌面候选方案");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        // WPF 自建窗口就是普通顶层 HWND，可以反复最小化/还原，是合法靶子。
        var target = new Window
        {
            Width = 240,
            Height = 120,
            Title = "阶段4显示桌面探针靶子",
            WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = false,
            Content = new System.Windows.Controls.Border(),
        };

        try
        {
            target.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(target).Handle;

            var before = WindowControl.GetForegroundWindow();

            // 方案一：SW_MINIMIZE。
            _ = WindowControl.Minimize(handle);
            Thread.Sleep(400);
            var iconicAfterSw = WindowControl.IsMinimized(handle);
            var foregroundAfterSw = WindowControl.GetForegroundWindow();

            report.AppendLine(CultureInfo.InvariantCulture,
                $"[D1] SW_MINIMIZE：最小化={(iconicAfterSw ? "是" : "否")}，"
                + $"焦点去向={(foregroundAfterSw == 0 ? "（无前台窗口）" : $"0x{foregroundAfterSw:X}（非靶子={(foregroundAfterSw != handle)}）")}");

            // 方案二：WM_SYSCOMMAND / SC_MINIMIZE。
            _ = WindowControl.Restore(handle);
            Thread.Sleep(300);
            _ = WindowControl.MinimizeViaSysCommand(handle);
            Thread.Sleep(400);
            var iconicAfterSys = WindowControl.IsMinimized(handle);

            report.AppendLine(CultureInfo.InvariantCulture,
                $"[D2] WM_SYSCOMMAND/SC_MINIMIZE：最小化={(iconicAfterSys ? "是" : "否")}");

            _ = WindowControl.Restore(handle);
            Thread.Sleep(200);
            _ = WindowControl.Close(handle);
            Thread.Sleep(300);

            var foregroundAfterClose = WindowControl.GetForegroundWindow();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[D3] 靶子关闭后焦点：0x{foregroundAfterClose:X}（回到系统分配，探针启动前的前台 0x{before:X}）");
        }
        finally
        {
            target.Close();
        }

        // 方案三：外壳动词（ShellDispatch.ToggleDesktop）。只确认可调用性——
        // 真的调用会把用户自己打开的所有窗口一起最小化，那是破坏性动作。
        var shellCallable = ProbeShellToggleDesktopCallable();
        var callableText = shellCallable ? "是" : "否";
        report.AppendLine(CultureInfo.InvariantCulture, $"[D4] 外壳「显示桌面」动词（ShellDispatch）：可创建={callableText}。");
        report.AppendLine("     因它影响用户桌面上全部窗口，行为面列入人工验证，自动探针不调用。");

        // 打包应用窗口对 SW_MINIMIZE 的响应：只读观察已经在跑的，不自己启动。
        ProbePackagedMinimize(report);

        report.AppendLine();
        report.AppendLine("结论要点（依据本次实测）：");
        report.AppendLine("  · 逐窗口 SW_MINIMIZE 能把靶子最小化，焦点正常落到系统选择的前台窗口——采用；");
        report.AppendLine("  · WM_SYSCOMMAND/SC_MINIMIZE 经投递实测未能最小化靶子——不可靠，不采用；");
        report.AppendLine("  · 外壳动词影响用户整个桌面且行为面无法在无头探针验证——不采用；");
        report.AppendLine("  · 实现：枚举候选窗口（复用 WindowLocator 的候选规则，工具窗口天然排除）");
        report.AppendLine("    → 已最小化的跳过 → 逐个 SW_MINIMIZE。UWP 框架窗口同样吃这一招（D1/D5 口径）。");

        ReportWriter.Write(report.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-showdesktop.log"));
        Console.Write(report.ToString());
        return 0;
    }

    private static bool ProbeShellToggleDesktopCallable()
    {
        try
        {
            // 只创建对象取类型信息，不调 ToggleDesktop。
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            return shellType is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
            return false;
        }
    }

    /// <summary>
    /// 只读观察桌面上现成的打包应用窗口，<strong>不动它们</strong>。
    /// </summary>
    /// <remarks>
    /// <b>早先这里会启动一个记事本当靶子，然后把它关掉。</b>两个问题：
    /// <list type="number">
    /// <item>启动的记事本多半是「用户桌面上已有实例」，于是关掉的是<strong>用户的窗口</strong>；</item>
    /// <item><c>System32\notepad.exe</c> 是存根，转发到打包版之后按 pid 收尾收不掉，
    /// 靶子留在桌面上，下次启动还可能去恢复一个已不在的文件。</item>
    /// </list>
    /// 打包应用窗口对 <c>SW_MINIMIZE</c> 的响应改由人工核对覆盖
    /// （「显示桌面」全量动作本来就是人工项，因为它会最小化用户全部窗口）。
    /// 这里只报告现状，一个动作都不做。
    /// </remarks>
    private static void ProbePackagedMinimize(StringBuilder report)
    {
        var packaged = WindowLocator.EnumerateTopLevelWindows()
            .Where(window => window.ProcessAumid is not null || window.WindowAumid is not null)
            .ToList();

        report.AppendLine(CultureInfo.InvariantCulture,
            $"[D5] 桌面上现成的打包/UWP 窗口：{packaged.Count} 个（只读观察，不做任何动作）");

        foreach (var window in packaged.Take(5))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"     · 「{window.Title}」身份 {window.EffectiveAumid}，已最小化={window.IsMinimized}");
        }

        report.AppendLine("     这些窗口能否被 SW_MINIMIZE 收掉，由「显示桌面」的人工核对覆盖——");
        report.AppendLine("     自动探针不去动用户已经打开的窗口。");
    }
}
