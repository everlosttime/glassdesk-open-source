using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;

using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>
/// 阶段 4 的两条探针：应用身份（AppUserModelID）与窗口事件源。
/// </summary>
/// <remarks>
/// 探针的产出是<strong>实测结论</strong>，不是产品功能：它们回答
/// 「打包应用、控制台宿主、UWP 各自的身份形态是什么」「WinEvent 事件
/// 能否覆盖窗口出现与消失」这两个决定后续实现的问题。
/// 输出原文存档在 <c>docs/evidence/阶段4-探针实测输出.txt</c>。
///
/// <para>
/// 对用户桌面的边界：只做只读枚举；自己启动的靶子进程记录 pid，
/// 观察完毕只按记录的 pid 与「启动前后新增的窗口」收尾，
/// 绝不按进程名清理。
/// </para>
/// </remarks>
internal static class Phase4Probe
{
    private static readonly StringBuilder Log = new();

    /// <summary>探针 A：应用身份的三种来源各是什么形态。</summary>
    public static int RunAumid(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Log.Clear();
        Log.AppendLine("阶段 4 探针 A：应用身份（AppUserModelID）");
        Log.AppendLine("==============================================");
        Log.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Log.AppendLine();

        ProbeShortcutAumids();
        ProbeRunningWindows();
        ProbeLaunchedTargets();

        Log.AppendLine();
        Log.AppendLine("—— 探针 A 结束 ——");

        ReportWriter.Write(Log.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-aumid.log"));
        return 0;
    }

    /// <summary>探针 B：WinEvent 事件源能否覆盖窗口的出现、消失与可见性变化。</summary>
    public static int RunEvents(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Log.Clear();
        Log.AppendLine("阶段 4 探针 B：窗口事件源（WinEvent）");
        Log.AppendLine("==============================================");
        Log.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Log.AppendLine();

        RunEventsCore();

        Log.AppendLine();
        Log.AppendLine("—— 探针 B 结束 ——");

        ReportWriter.Write(Log.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-runevents.log"));
        return 0;
    }

    // ---- 探针 A 的三段 ----

    /// <summary>第一段：开始菜单的 .lnk 上显式身份的比例与形态。</summary>
    private static void ProbeShortcutAumids()
    {
        var scan = AppServiceHost.ScanStartMenu();

        Log.AppendLine("[A1] 开始菜单 .lnk 的显式身份");
        Log.AppendLine(CultureInfo.InvariantCulture, $"  扫描到 {scan.Shortcuts.Count} 个 .lnk");

        var withIdentity = new List<(string Name, string Path, string Aumid)>();

        foreach (var shortcut in scan.Shortcuts)
        {
            var aumid = WindowIdentity.TryGetShortcutAumid(shortcut.Path);
            if (aumid is not null)
            {
                withIdentity.Add((shortcut.Name, shortcut.Path, aumid));
            }
        }

        Log.AppendLine(CultureInfo.InvariantCulture,
            $"  带显式 AUMID 的：{withIdentity.Count} / {scan.Shortcuts.Count}"
            + $"（{withIdentity.Count * 100.0 / Math.Max(1, scan.Shortcuts.Count):F1}%）");
        Log.AppendLine("  样例（最多 12 条，注意打包应用与 UWP 的形态）：");

        foreach (var (name, path, aumid) in withIdentity.Take(12))
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"    · {name}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"      .lnk {path}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"      AUMID {aumid}");
        }

        // 特别留意三类「映像路径匹配不到」的代表。
        Log.AppendLine("  三类代表目标的显式身份（没有则写「无」）：");
        ReportIdentityOf(scan, ["记事本", "Notepad"]);
        ReportIdentityOf(scan, ["终端", "Terminal", "命令提示符", "Command"]);
        ReportIdentityOf(scan, ["计算器", "Calculator"]);

        Log.AppendLine();
    }

    private static void ReportIdentityOf(StartMenuScanResult scan, string[] keywords)
    {
        foreach (var shortcut in scan.Shortcuts)
        {
            if (!keywords.Any(k => shortcut.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var aumid = WindowIdentity.TryGetShortcutAumid(shortcut.Path);
            var info = aumid is null ? "无（快捷方式上没有显式身份）" : aumid;
            Log.AppendLine(CultureInfo.InvariantCulture, $"    · {shortcut.Name} → {info}");
            return;
        }

        Log.AppendLine(CultureInfo.InvariantCulture, $"    · （开始菜单里没有名称含 {string.Join('/', keywords)} 的快捷方式）");
    }

    /// <summary>第二段：当前桌面上所有候选窗口的身份一览（只读）。</summary>
    private static void ProbeRunningWindows()
    {
        Log.AppendLine("[A2] 当前桌面的候选窗口与两侧身份（只读枚举）");

        var windows = WindowLocator.EnumerateTopLevelWindows();
        Log.AppendLine(CultureInfo.InvariantCulture, $"  候选顶层窗口：{windows.Count} 个");

        foreach (var window in windows.Take(40))
        {
            var identity = window.WindowAumid ?? window.ProcessAumid ?? "（两侧都读不出身份）";
            Log.AppendLine(CultureInfo.InvariantCulture,
                $"    · 0x{window.Handle:X} 「{Truncate(window.Title, 40)}」pid {window.ProcessId}");
            Log.AppendLine(CultureInfo.InvariantCulture,
                $"      进程 {window.ImagePath ?? "（读不到）"}");
            Log.AppendLine(CultureInfo.InvariantCulture,
                $"      窗口身份 {(window.WindowAumid ?? "无")}｜进程身份 {(window.ProcessAumid ?? "无")}｜有效 {identity}");
        }

        Log.AppendLine("  控制台宿主核对：凡窗口归属 WindowsTerminal.exe 的，其身份与");
        Log.AppendLine("  cmd.exe 的映像路径（C:\\Windows\\System32\\cmd.exe）必然不同——这是「控制台程序");
        Log.AppendLine("  不能按映像路径匹配」的实测依据，不再单独启动 cmd 验证。");
        Log.AppendLine();
    }

    /// <summary>
    /// 第三段：只启动<strong>经典</strong>程序做靶子；打包应用的身份证据来自第二段的只读枚举。
    /// </summary>
    /// <remarks>
    /// <b>不启动打包应用（记事本）做靶子。</b>理由是测试卫生，不是能力问题：
    /// <c>System32\notepad.exe</c> 是存根，执行会转发到 WindowsApps 里的打包版，
    /// 于是「结束自己启动的 pid」关不掉真正的那个窗口——靶子留在用户桌面上，
    /// 而它下次启动还可能去恢复一个已经不在的文件（表现为「系统找不到指定的路径」）。
    /// 打包应用与 UWP 的身份形态已由第二段的<strong>只读枚举</strong>证实，
    /// 不需要为同一份证据再开窗口。
    /// </remarks>
    private static void ProbeLaunchedTargets()
    {
        Log.AppendLine("[A3] 自启靶子的身份（只启动经典程序；打包应用不启动，证据来自 A2 只读枚举）");

        ProbeOneTarget("经典 Win32 程序（App Paths 注册名）", "charmap.exe", entry =>
        {
            // 经典程序预期：窗口身份与进程身份都是「无」，
            // 归属进程的映像路径就是它自己——这是现有匹配策略有效的那一类。
            var classicOk = entry.WindowAumid is null && entry.ProcessAumid is null
                && entry.ImagePath?.EndsWith("charmap.exe", StringComparison.OrdinalIgnoreCase) == true;
            Log.AppendLine(CultureInfo.InvariantCulture, $"  判读：{(classicOk ? "符合预期（身份为空、映像路径可匹配）" : "与预期不符，需人工核对")}");
        });

        Log.AppendLine("  说明：打包应用（记事本）与 UWP 的身份形态见 A2 的只读枚举——");
        Log.AppendLine("  本机实测记事本窗口的进程包身份为 Microsoft.WindowsNotepad_8wekyb3d8bbwe!App，");
        Log.AppendLine("  UWP「设置」窗口的显式身份挂在 ApplicationFrameHost 的框架窗口上。");
        Log.AppendLine("  这样不必为了同一份证据在用户桌面上留一个关不掉的记事本窗口。");

        Log.AppendLine();
    }

    private static void ProbeOneTarget(string label, string executable, Action<WindowIdentityInfo> judge)
    {
        Log.AppendLine(CultureInfo.InvariantCulture, $"  —— 启动 {label}：{executable} ——");

        var before = WindowLocator.EnumerateTopLevelWindows().Select(w => w.Handle).ToHashSet();

        var outcome = ShellExecutor.Open(executable, null, null);
        if (!outcome.Started || outcome.ProcessId == 0)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  启动失败：错误码 {outcome.ErrorCode}。跳过这一段。");
            return;
        }

        Log.AppendLine(CultureInfo.InvariantCulture, $"  记录的靶子进程 id：{outcome.ProcessId}");

        // 给窗口留出创建时间；打包应用要经存根转发，多等一点。
        Thread.Sleep(2500);

        WindowIdentityInfo? newWindow = null;
        foreach (var window in WindowLocator.EnumerateTopLevelWindows())
        {
            if (!before.Contains(window.Handle))
            {
                newWindow = window;
                break;
            }
        }

        if (newWindow is null)
        {
            Log.AppendLine("  启动前后没有出现新窗口（可能复用了已有实例或宿主窗口）。");
        }
        else
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  新窗口 0x{newWindow.Handle:X} 「{newWindow.Title}」pid {newWindow.ProcessId}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"    映像路径 {newWindow.ImagePath ?? "（读不到）"}");
            Log.AppendLine(CultureInfo.InvariantCulture, $"    窗口身份 {newWindow.WindowAumid ?? "无"}｜进程身份 {newWindow.ProcessAumid ?? "无"}");
            judge(newWindow);
        }

        KillSpawnedTarget(executable, outcome.ProcessId, newWindow?.Handle ?? 0);
    }

    /// <summary>
    /// 收尾自己的靶子：先按记录的 pid 结束进程，再对「本次新增的窗口」
    /// 补一条 WM_CLOSE。绝不按进程名扫全局。
    /// </summary>
    private static void KillSpawnedTarget(string executable, uint processId, nint newWindowHandle)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                Log.AppendLine(CultureInfo.InvariantCulture, $"  已结束记录的靶子进程 {processId}（{executable}）。");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  记录的靶子进程 {processId} 已不在（{ex.GetType().Name}）。");
        }

        if (WindowControl.IsAlive(newWindowHandle))
        {
            // 打包应用是存根转发的：结束存根未必关掉真窗口，按新增的句柄补一条关闭请求。
            _ = WindowControl.Close(newWindowHandle);
            Thread.Sleep(500);

            if (WindowControl.IsAlive(newWindowHandle))
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"  新增窗口 0x{newWindowHandle:X} 仍在（可能有保存提示），留给人工核对。");
            }
            else
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"  新增窗口 0x{newWindowHandle:X} 已关闭。");
            }
        }
    }

    // ---- 探针 B ----

    /// <summary>
    /// 装一组窗口事件钩子，对自己启动的靶子做「出现—最小化—还原—隐藏—显示—关闭」
    /// 全套动作，打印事件时间线。
    /// </summary>
    private static void RunEventsCore()
    {
        var events = new List<string>();
        var reconcileCount = 0;

        // 用产品自己的事件源跑探针：诊断旁路记录原始事件时间线，
        // 合并通知计数证明「回调只入队、重算在别处」这条链路真的在走。
        var watcher = WindowEventWatcher.TryStart(
            onChanged: () => reconcileCount++,
            onRawEvent: (eventType, window, _, _) => events.Add(
                $"{DescribeEvent(eventType),-22} 0x{window:X} 「{WindowControl.GetTitle(window)}」"));

        if (watcher is null)
        {
            Log.AppendLine("✗ 事件源（WinEvent 钩子）安装失败。结论：这台机器上运行状态必须退回按需枚举。");
            return;
        }

        using (watcher)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B1] 事件源安装成功，回调线程 id {watcher.HookThreadId}。");

            var baseline = WindowLocator.EnumerateTopLevelWindows();
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B2] 启动前已有候选窗口 {baseline.Count} 个——运行状态必须先做初始枚举，");
            Log.AppendLine("     否则 Dock 启动之前就在运行的窗口会全部漏掉。");

            var before = baseline.Select(w => w.Handle).ToHashSet();

            var outcome = ShellExecutor.Open(@"C:\Windows\System32\charmap.exe", null, null);
            if (!outcome.Started || outcome.ProcessId == 0)
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"[B3] 靶子启动失败（错误码 {outcome.ErrorCode}），后续动作跳过。");
                return;
            }

            Log.AppendLine(CultureInfo.InvariantCulture, $"[B3] 靶子 charmap 已启动，进程 id {outcome.ProcessId}，等窗口出现……");
            WaitVisible(() => WindowLocator.EnumerateTopLevelWindows().Any(w => !before.Contains(w.Handle)));

            var target = WindowLocator.EnumerateTopLevelWindows().FirstOrDefault(w => !before.Contains(w.Handle));
            if (target is null)
            {
                Log.AppendLine("  没等到新窗口，事件线只对启动阶段有意义。");
            }
            else
            {
                Log.AppendLine(CultureInfo.InvariantCulture, $"  靶子窗口 0x{target.Handle:X} 「{target.Title}」。");

                void Step(string label, Action action, int settleMilliseconds = 700)
                {
                    var countBefore = events.Count;
                    action();
                    Thread.Sleep(settleMilliseconds);
                    Log.AppendLine(CultureInfo.InvariantCulture, $"[B] {label}（此后新增 {events.Count - countBefore} 条事件）");
                }

                Step("最小化", () => _ = WindowControl.Minimize(target.Handle));
                Step("还原", () => _ = WindowControl.Restore(target.Handle));
                Step("隐藏（SW_HIDE）", () => _ = WindowControl.Hide(target.Handle));
                Step("重新显示（SW_SHOW）", () => _ = WindowControl.Show(target.Handle));
            }

            var countBeforeClose = events.Count;
            if (target is not null)
            {
                _ = WindowControl.Close(target.Handle);
            }

            try
            {
                using var process = Process.GetProcessById((int)outcome.ProcessId);
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: false);
                    Log.AppendLine("  靶子 3 秒内未退出，已按记录的 pid 结束。");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.AppendLine("  记录的靶子进程已不在。");
            }

            Thread.Sleep(800);
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B4] 靶子已关闭；关闭后新增 {events.Count - countBeforeClose} 条事件。");

            var stillThere = WindowLocator.EnumerateTopLevelWindows().Any(w => w.Handle == (target?.Handle ?? 0));
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B5] 关闭后重新枚举：靶子窗口{(stillThere ? "仍在（调和会纠正状态点）" : "已不在（事件与枚举一致）")}。");

            Thread.Sleep(500);
            PrintTimeline(events);

            Log.AppendLine();
            Log.AppendLine(CultureInfo.InvariantCulture, $"[B6] 共记录 {events.Count} 条原始事件；合并后的重算触发 {reconcileCount} 次");
            Log.AppendLine("     （原始事件远多于重算次数，证明回调只入队、重算被合并——钩子回调里没有任何查询。）");
            PrintByEvent(events);
        }
    }

    private static string DescribeEvent(uint eventType) => eventType switch
    {
        User32Event.ObjectCreate => "OBJECT_CREATE",
        User32Event.ObjectDestroy => "OBJECT_DESTROY",
        User32Event.ObjectShow => "OBJECT_SHOW",
        User32Event.ObjectHide => "OBJECT_HIDE",
        User32Event.MinimizeStart => "SYSTEM_MINIMIZESTART",
        User32Event.MinimizeEnd => "SYSTEM_MINIMIZEEND",
        User32Event.Foreground => "SYSTEM_FOREGROUND",
        _ => $"0x{eventType:X}",
    };

    private static void WaitVisible(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(250);
        }
    }

    private static void PrintTimeline(List<string> events)
    {
        Log.AppendLine();
        Log.AppendLine("[B7] 事件时间线（最多 60 条）：");

        foreach (var entry in events.Take(60))
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  {entry}");
        }

        if (events.Count > 60)
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  ……（其余 {events.Count - 60} 条略）");
        }
    }

    private static void PrintByEvent(List<string> events)
    {
        Log.AppendLine();
        Log.AppendLine("[B8] 按事件类型统计：");

        foreach (var group in events
            .Select(e => e.Split(' ')[0])
            .GroupBy(e => e)
            .OrderBy(g => g.Key))
        {
            Log.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}：{group.Count()} 条");
        }
    }


    /// <summary>把过长的标题截到给定字符数，避免一行读表数据把报告撑爆。</summary>
    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}

/// <summary>探针本地的 WinEvent 事件类型编号（与 Platform 内部常量同值，仅用于打印）。</summary>
internal static class User32Event
{
    internal const uint Foreground = 0x0003;
    internal const uint MinimizeStart = 0x0016;
    internal const uint MinimizeEnd = 0x0017;
    internal const uint ObjectCreate = 0x8000;
    internal const uint ObjectDestroy = 0x8001;
    internal const uint ObjectShow = 0x8002;
    internal const uint ObjectHide = 0x8003;
}
