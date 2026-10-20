using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>
/// 阶段 4 的无界面验收。体例与 <see cref="Phase3Acceptance"/> 相同：
/// 真实跑一遍、逐项断言、写报告，只在隔离的临时数据目录里操作，
/// <strong>绝不动用户真正的应用库</strong>。
/// </summary>
/// <remarks>
/// 断言随批次累积（每完成一个批次就补上该批次的检查）：
/// <list type="bullet">
/// <item><b>批次 1</b>：显式身份的存取、窗口归属匹配规则、
/// 运行状态的初始枚举与调和、优先激活的经典路径。</item>
/// </list>
///
/// <para>
/// 靶子纪律：自动化只启动自己记录了 pid 的靶子进程，收尾只按记录的 pid；
/// 用户桌面已有靶子窗口时跳过并标注「未验证」，绝不按进程名清理。
/// </para>
/// </remarks>
internal static class Phase4Acceptance
{
    private const string ClassicTarget = @"C:\Windows\System32\charmap.exe";

    /// <summary>打包版记事本的身份（阶段 4 探针实测的形态）。</summary>
    private const string NotepadAumid = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";

    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, GlassDesk.Platform.Storage.AcceptanceWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(realPaths);
        ArgumentNullException.ThrowIfNull(arguments);

        var report = new StringBuilder();
        var checks = new List<(string Label, CheckStatus Status, string Detail)>();

        var paths = new AppPaths(workspace.DataRoot);
        paths.EnsureCreated();

        report.AppendLine("GlassDesk 阶段 4 验收");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"验收数据目录：{paths.RootDirectory}（隔离的临时目录，跑完保留供核对）");
        report.AppendLine(CultureInfo.InvariantCulture, $"用户真正的数据目录：{realPaths.RootDirectory}——全程未被改动");
        report.AppendLine();

        var data = new GlassDeskDataStore(paths);
        data.LoadAll();
        using var host = new AppServiceHost(data);
        host.Running.Start();

        Section(report, "一、显式身份随导入存进条目");
        CheckImportedIdentity(host, paths, checks, report);

        Section(report, "二、窗口归属匹配规则（对伪造快照的纯函数断言）");
        CheckMatchingRules(checks, report);

        Section(report, "三、运行状态：初始枚举与出现/消失调和");
        var runningTargetAvailable = CheckRunningTracker(host, checks, report);

        Section(report, "四、优先激活：第一次启动、第二次激活（D-09）");
        if (runningTargetAvailable)
        {
            CheckActivateOrLaunch(host, checks, report);
        }
        else
        {
            Skip(checks, report, "用户桌面已有字符映射表窗口，激活检查跳过（避免动用户的窗口）");
        }

        Section(report, "五、默认值（D-21/D-22）：写入配置文件再读回来核对");
        CheckSettingsDefaults(paths, checks, report);

        Section(report, "六、摆放：四种位置 × 每块显示器实机落位（D-15/D-23）");
        CheckPhysicalPlacement(checks, report);

        Section(report, "七、放大峰值可调（D-17）：设置文件驱动几何");
        CheckMagnificationFromSettings(paths, checks, report);

        Section(report, "八、固定、排序与移出（D-02/D-03/R-08 的服务层）");

        Section(report, "十、500 条库与超长固定行的正确性");
        CheckScale(host, paths, checks, report);
        CheckPinnedAndReorder(host, paths, checks, report);

        Section(report, "九、内置入口（D-14：回收站经外壳打开；拼错的 shell: 目标被拒绝）");
        CheckRecycleBinEntry(checks, report);

        // ---- 汇总 ----
        report.AppendLine();
        report.AppendLine("检查汇总");
        report.AppendLine(new string('-', 46));

        foreach (var (label, status, detail) in checks)
        {
            var mark = status switch
            {
                CheckStatus.Passed => "✓",
                CheckStatus.Skipped => "○",
                _ => "✗",
            };

            report.AppendLine(CultureInfo.InvariantCulture, $"{mark} {label}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    {detail}");
        }

        var failed = checks.Count(check => check.Status == CheckStatus.Failed);
        var skipped = checks.Count(check => check.Status == CheckStatus.Skipped);
        var passed = checks.Count(check => check.Status == CheckStatus.Passed);

report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"共 {checks.Count} 项检查：通过 {passed} 项，失败 {failed} 项，跳过 {skipped} 项。");
        report.AppendLine("○ 是「条件不具备、没跑」的项——它们既不算通过也不算失败，交付报告里按未验证列出。");
        report.AppendLine("打包应用与 UWP 的真实激活、多窗口选择列表的交互、运行状态点的界面呈现");
        report.AppendLine("不由本命令完整覆盖；另跑 build/build.ps1 -UiAcceptance，并核对阶段4-自动交互验收报告中的外部覆盖状态。");

        // 次序是有讲究的：打印 → 写日志 → 删目录。
        // 日志文件本身就在隔离目录里，先删目录的话，写日志这一步会把它重新建出来
        // （实测：报告说「已清理」，目录却还在）。
        Console.Write(report.ToString());
        AcceptanceEntryPoint.WriteText(workspace.GetOutput("report"), report.ToString());
        ReportWriter.Write(report.ToString(), null);

        return failed == 0 ? 0 : 1;
    }

    // ---- 批次 2 的各节 ----

    /// <summary>
    /// D-21/D-22：把默认设置真正写到磁盘上，再从文件读回来核对默认值。
    /// </summary>
    /// <remarks>
    /// 只读内存默认值不算数——验收方式写明「读取配置文件核对默认值」。
    /// 在验收自己的隔离目录里另起一份设置文件，与用户真正的设置无关。
    /// </remarks>
    private static void CheckSettingsDefaults(
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var settingsPaths = new AppPaths(Path.Combine(paths.RootDirectory, "默认值核对"));
        var settingsStore = new SettingsStore(settingsPaths.SettingsFile);
        settingsStore.Save();

        // 重新从磁盘加载，确保断言的是文件里的内容而不是内存里的对象。
        var reloaded = new SettingsStore(settingsPaths.SettingsFile);
        _ = reloaded.Load();
        var dock = reloaded.Current.Dock;
        var appearance = reloaded.Current.Appearance;

        Check(checks, "默认图标尺寸 48 DIP（D-21）", dock.IconSize == 48, $"{dock.IconSize}");
        Check(checks, "默认图标间距 8 DIP（D-21）", dock.IconSpacing == 8, $"{dock.IconSpacing}");
        Check(checks, "默认放大峰值 1.35×（D-21）", Math.Abs(dock.MagnificationPeak - 1.35) < 0.0001, $"{dock.MagnificationPeak}");
        Check(checks, "液态玻璃 Dock 圆角 40 DIP", appearance.DockCornerRadius == 40, $"{appearance.DockCornerRadius}");
        Check(checks, "默认离开延迟约 300 ms（D-19）",
            dock.AutoHideLeaveDelayMs is >= 250 and <= 350, $"{dock.AutoHideLeaveDelayMs} ms");

        report.AppendLine(CultureInfo.InvariantCulture, $"核对文件：{settingsPaths.SettingsFile}");
        report.AppendLine();
    }

    /// <summary>
    /// 实机摆放：对每块显示器 × 四种位置，按 <see cref="DockPlacementCalculator"/> 的
    /// 计算结果摆一个真实窗口，读回窗口矩形断言一致且不出工作区。
    /// </summary>
    /// <remarks>
    /// 这一层验证的是「WPF 窗口 + <see cref="DisplayService.MoveWindowToPixelRect"/>
    /// 在真实多屏混合 DPI 环境下按计算结果就位」；计算本身的几何性质由
    /// 离线单测锁住（<c>DockPlacementCalculatorTests</c>），两层合起来才是 D-15。
    /// </remarks>
    private static void CheckPhysicalPlacement(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var displays = DisplayService.Enumerate();

        if (displays.Count == 0)
        {
            Check(checks, "至少枚举到一块显示器", false, "枚举结果为空");
            return;
        }

        Check(checks, "显示器枚举可用", true, $"枚举到 {displays.Count} 块");

        if (displays.Count < 2)
        {
            Skip(checks, report, "副屏不存在，多屏摆放只测了主屏");
        }

        // 一个朴素的真实窗口即可：验证的是摆放链路，不是玻璃外观。
        var probeWindow = new System.Windows.Window
        {
            Width = 900,
            Height = 90,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Transparent },
        };

        try
        {
            probeWindow.Show();

            var handle = new System.Windows.Interop.WindowInteropHelper(probeWindow).Handle;

            foreach (var display in displays)
            {
                foreach (DockPlacement placement in Enum.GetValues<DockPlacement>())
                {
                    // 期望矩形：DIP 尺寸按这块屏自己的 DPI 换算。
                    var widthPixels = Math.Max(1, display.ToPixels(probeWindow.ActualWidth));
                    var heightPixels = Math.Max(1, display.ToPixels(probeWindow.ActualHeight));

                    var expected = DockPlacementCalculator.Calculate(
                        display.WorkArea, placement, 0, 0, widthPixels, heightPixels, display.Dpi);

                    _ = DisplayService.MoveWindowToPixelRect(handle, expected);
                    var actual = DisplayService.GetWindowPixelBounds(handle);

                    var ok = actual is not null
                        && Math.Abs(actual.Value.X - expected.X) <= 1
                        && Math.Abs(actual.Value.Y - expected.Y) <= 1
                        && actual.Value.X >= display.WorkArea.X
                        && actual.Value.Y >= display.WorkArea.Y
                        && actual.Value.Right <= display.WorkArea.Right
                        && actual.Value.Bottom <= display.WorkArea.Bottom;

                    Check(checks, $"{display.DeviceName}{(display.IsPrimary ? "（主屏）" : string.Empty)} {placement} 按计算落位且不出工作区",
                        ok, $"期望 {expected}，实际 {actual?.ToString() ?? "读不到"}");
                }
            }
        }
        finally
        {
            probeWindow.Close();
        }

        report.AppendLine("每块显示器 × 四种位置逐一实机摆放并读回核对；副屏缺失时如实标注未验证。");
        report.AppendLine();
    }


    /// <summary>
    /// D-17：把一个非默认的放大峰值写进设置文件，读回来驱动几何计算，
    /// 断言峰值真实生效——「可调」的完整链路是 设置文件 → 读取 → 几何。
    /// </summary>
    private static void CheckMagnificationFromSettings(
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var settingsPaths = new AppPaths(Path.Combine(paths.RootDirectory, "放大峰值核对"));
        var store = new SettingsStore(settingsPaths.SettingsFile);
        _ = store.Load();
        store.Current.Dock.MagnificationPeak = 2.0;
        store.Save();

        var reloaded = new SettingsStore(settingsPaths.SettingsFile);
        _ = reloaded.Load();
        var peak = reloaded.Current.Dock.MagnificationPeak;

        Check(checks, "设置文件里的放大峰值能被原样读回", Math.Abs(peak - 2.0) < 0.0001, $"{peak}");

        var geometry = GlassDesk.Dock.DockMagnification.Compute(
            5, reloaded.Current.Dock.IconSize, reloaded.Current.Dock.IconSpacing, peak,
            GlassDesk.Dock.DockMagnification.SlotCenter(2, reloaded.Current.Dock.IconSize, reloaded.Current.Dock.IconSpacing, peak));

        Check(checks, "读回的峰值驱动几何：指针下方磁贴取峰值",
            Math.Abs(geometry[2].Scale - 2.0) < 0.0001, $"第 2 枚缩放 {geometry[2].Scale}");
    }

    /// <summary>
    /// 固定、排序与移出的服务层断言（D-02/D-03/R-08）。界面上的拖拽手势
    /// 最终都落到这一层：预览只改内存、提交恰好落盘一次、移出不动应用库。
    /// </summary>
    private static void CheckPinnedAndReorder(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        // 准备两条可用条目：charmap + 一个真实样本文件。
        var importResult = host.Import.ImportItems([
            new ImportRequest { Source = ClassicTarget },
            new ImportRequest { Source = @"C:\Windows\System32\drivers\etc\hosts" },
        ]);

        // charmap 可能在前面的激活/调和检查里已经导入过——「新增 + 重复」合并取条目，
        // 只要求两条都可用，不要求都是这次新增。
        var first = importResult.Added.Count > 0 ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0] : null;
        var second = importResult.Added.Count > 1 ? importResult.Added[1]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0] : null;

        if (first is null || second is null || first.Id == second.Id)
        {
            Check(checks, "排序检查的准备条目导入成功", false,
                $"新增 {importResult.Added.Count}，重复 {importResult.Duplicates.Count}，失败 {importResult.Failures.Count}");
            return;
        }

        var layout = host.Layouts;
        _ = layout.LoadDock();
        var saveCount = 0;

        // 排序状态机与 Dock 界面用的是同一套：保存委托正是 Dock 里的那条落盘路径。
        var model = new GlassDesk.Dock.DockReorderModel(order =>
        {
            layout.Dock.PinnedItemIds = [.. order];
            layout.SaveDock();
            saveCount++;
        });

        // 固定两条：预览若干次（先乱序）→ 期间布局文件不得被写。
        // 干净目录里文件此刻还不存在——预览期间它必须「连创建都没发生」，
        // 这比「内容不变」是更强的证据。
        var fileExistedBefore = File.Exists(layout.DockFilePath);
        var before = fileExistedBefore ? File.ReadAllBytes(layout.DockFilePath) : null;
        model.Begin([first.Id, second.Id]);
        model.PreviewMove(second.Id, 0);
        model.PreviewMove(second.Id, 1); // 放回去一次，制造无意义预览
        model.PreviewMove(second.Id, 0);

        var unchangedAfterPreview = fileExistedBefore
            ? before!.SequenceEqual(File.ReadAllBytes(layout.DockFilePath))
            : !File.Exists(layout.DockFilePath);

        Check(checks, "拖拽预览期间布局文件一个字节都没变（R-08）",
            unchangedAfterPreview,
            fileExistedBefore ? "预览 3 次后文件仍为原始内容" : "预览期间布局文件根本没有被创建");

        // 提交：恰好一次落盘，顺序为预览结果。
        var outcome = model.Commit();
        Check(checks, "提交恰好落盘一次", outcome == GlassDesk.Dock.ReorderOutcome.Committed && saveCount == 1,
            $"结果 {outcome}，保存次数 {saveCount}");

        // 「拖动后重启，顺序一致」：换一个进程视角重新加载布局。
        var reloaded = new LayoutStore(
            Path.Combine(paths.RootDirectory, "dock-layout.json"),
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloaded.LoadDock();
        Check(checks, "重新加载（模拟重启）后顺序一致（D-02）",
            reloaded.Dock.PinnedItemIds.Count == 2 && reloaded.Dock.PinnedItemIds[0] == second.Id,
            $"顺序：{string.Join(',', reloaded.Dock.PinnedItemIds)}");

        // Esc 取消：零落盘。
        saveCount = 0;
        model.Begin([second.Id, first.Id]);
        model.PreviewMove(first.Id, 0);
        _ = model.Cancel();
        Check(checks, "取消拖拽（Esc）零落盘（R-08）",
            saveCount == 0 && reloaded.Dock.PinnedItemIds[0] == second.Id, $"保存次数 {saveCount}");

        // 提交失败：顺序恢复原样。
        var failing = new GlassDesk.Dock.DockReorderModel(_ => throw new IOException("演练：磁盘拒绝"));
        failing.Begin([first.Id, second.Id]);
        failing.PreviewMove(first.Id, 1);
        var failureOutcome = failing.Commit();
        Check(checks, "提交失败时恢复原顺序（R-08）",
            failureOutcome == GlassDesk.Dock.ReorderOutcome.SaveFailed
            && failing.Order.SequenceEqual([first.Id, second.Id]),
            $"结果 {failureOutcome}");

        // 复核报告的验收标准：**5 个应用拖拽排序后，重启仍是正确顺序，
        // 布局文件仍只有 5 个应用 ID**（不能混进内置入口的 entry:*）。
        CheckFiveAppReorder(host, paths, checks, report);

        // D-03：移出取消固定——布局里没了，应用库里还在。
        _ = layout.UnpinFromDock(second.Id);
        layout.SaveDock();
        var reloadedAfterUnpin = new LayoutStore(
            Path.Combine(paths.RootDirectory, "dock-layout.json"),
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloadedAfterUnpin.LoadDock();

        var stillInLibrary = host.Library.Find(second.Id) is not null
            && !reloadedAfterUnpin.Dock.PinnedItemIds.Contains(second.Id);

        Check(checks, "移出后重启：布局没有它，启动台（应用库）仍有该项（D-03）",
            stillInLibrary,
            $"库中存在={host.Library.Find(second.Id) is not null}，固定数={reloadedAfterUnpin.Dock.PinnedItemIds.Count}");

        report.AppendLine(CultureInfo.InvariantCulture, $"布局文件：{layout.DockFilePath}");
        report.AppendLine();
    }

    /// <summary>
    /// D-14 的服务层断言：外壳认不认 <c>shell:RecycleBinFolder</c>、认不认拼错的。
    /// </summary>
    /// <remarks>
    /// <strong>只解析，不打开。</strong>早先这一节真的调 <c>ShellExecutor.Open</c>
    /// 打开回收站再关掉差集窗口，会在用户桌面上闪一个资源管理器窗口；
    /// 而拼错的 <c>shell:</c> 目标还有弹错误框的风险。解析检查（<c>SHParseDisplayName</c>）
    /// 零窗口零弹窗，断言的仍是同一件事：外壳接不接受这个目标。
    /// 「点一下入口磁贴真的能打开」属人工核对清单（第六节第 9 项旁）。
    /// </remarks>
    private static void CheckRecycleBinEntry(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var accepted = Platform.Shell.ShellTargets.CanResolve("shell:RecycleBinFolder");
        var rejected = Platform.Shell.ShellTargets.CanResolve("shell:GlassDeskNoSuchFolder");

        if (accepted is null || rejected is null)
        {
            Skip(checks, report, "外壳解析接口不可用，回收站目标的检查跳过");
            return;
        }

        Check(checks, "外壳接受 shell:RecycleBinFolder（回收站入口的链路）", accepted == true,
            "SHParseDisplayName 返回成功");
        Check(checks, "外壳拒绝拼错的 shell: 目标（存在性检查没有被放宽）", rejected == false,
            "SHParseDisplayName 返回失败");

        report.AppendLine("这一节只做解析检查，不开窗口、不弹框；真实打开由人工核对覆盖。");
        report.AppendLine();
    }

    /// <summary>
    /// 规模正确性（开工说明第五节）：500 条库 + 超长固定行下，
    /// 布局、排序与几何都不变形。流畅度属阶段 7 的性能验收，
    /// 这里只锁正确性。
    /// </summary>
    private static void CheckScale(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        // 库里已经有十几条真实/样本条目，再补到 500+。
        var existing = host.Library.Items.Count;
        var seedTarget = 500;
        var added = 0;

        for (var index = existing; index < seedTarget; index++)
        {
            var entry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.File,
                Target = @"C:\Windows\System32\notepad.exe",
                DisplayName = string.Create(CultureInfo.InvariantCulture, $"规模条目 {index:D4}"),
                Arguments = string.Create(CultureInfo.InvariantCulture, $"--scale {index}"),
                IconSource = @"C:\Windows\System32\notepad.exe",
            };

            if (host.Library.Add(entry) == AddEntryResult.Added)
            {
                added++;
            }
        }

        host.Library.Save();

        Check(checks, "应用库补足到 500 条以上", host.Library.Items.Count >= seedTarget,
            $"库共 {host.Library.Items.Count} 条（本次新增 {added}）");

        // 固定 200 条（远超一行能放下的量），覆盖溢出滚动的内容规模。
        var layout = host.Layouts;
        var pinned = new List<string>(layout.Dock.PinnedItemIds);
        foreach (var entry in host.Library.Items.Take(200))
        {
            if (entry is not null && entry.IsUsable && !pinned.Contains(entry.Id))
            {
                pinned.Add(entry.Id);
            }
        }

        layout.Dock.PinnedItemIds = pinned;
        layout.SaveDock();

        var reloaded = new LayoutStore(
            Path.Combine(paths.RootDirectory, "dock-layout.json"),
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloaded.LoadDock();

        Check(checks, "200 条固定行保存后重新加载顺序一致",
            reloaded.Dock.PinnedItemIds.Count == pinned.Count
            && pinned.SequenceEqual(reloaded.Dock.PinnedItemIds),
            $"固定 {pinned.Count} 项，重载 {reloaded.Dock.PinnedItemIds.Count} 项");

        // 200 枚磁贴的几何：总长度为正且命中不越界。
        var length = GlassDesk.Dock.DockMagnification.RequiredLength(
            pinned.Count, 48, 8, 1.35);
        var geometry = GlassDesk.Dock.DockMagnification.Compute(
            pinned.Count, 48, 8, 1.35, null);
        var hit = GlassDesk.Dock.DockMagnification.HitTest(
            geometry, 48, geometry[199].Center);

        Check(checks, "超长固定行的几何计算在 200 项下正确",
            length > 0 && hit == 199,
            $"总长 {length:F0} DIP，末位命中索引 {hit}");

        // 库重新加载（模拟重启）：条数一致。
        var reloadedLibrary = new AppLibraryStore(host.Paths.AppLibraryFile);
        reloadedLibrary.Load();
        Check(checks, "重启后应用库条数一致",
            reloadedLibrary.Items.Count == host.Library.Items.Count,
            $"重载 {reloadedLibrary.Items.Count} 条");

        report.AppendLine();
    }

    /// <summary>
    /// 复核报告的验收标准：5 个应用拖拽排序后——
    /// 重启仍是正确顺序，布局文件仍只有 5 个应用 ID（没有内置入口的 entry:*）。
    /// </summary>
    /// <remarks>
    /// 顺带验证旧布局的清理：往布局里**故意塞进**三个入口与一个陌生 id，
    /// 清洗后必须只剩库里的应用，且原应用顺序不变。
    /// </remarks>
    private static void CheckFiveAppReorder(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        const int AppCount = 5;
        var layout = host.Layouts;
        var dockFile = layout.DockFilePath;

        // 造出 5 个真实应用条目（库里已有的合成条目就是，取前 5 条）。
        var apps = host.Library.Items
            .Where(entry => entry is not null && entry.IsUsable)
            .Take(AppCount)
            .Select(entry => entry.Id)
            .ToList();

        if (apps.Count < AppCount)
        {
            Skip(checks, report, "库里不足 5 条可用条目，5 应用排序检查跳过");
            return;
        }

        // 排序走与界面同一条路：状态机 + DockLayoutPolicy 校验后落盘。
        var saveCount = 0;

        var model = new GlassDesk.Dock.DockReorderModel(order =>
        {
            var sanitized = GlassDesk.Dock.DockLayoutPolicy.Sanitize(
                order, id => host.Library.Find(id) is not null);

            layout.Dock.PinnedItemIds = sanitized;
            layout.SaveDock();
            saveCount++;
        });

        model.Begin(apps);
        model.PreviewMove(apps[4], 0);   // 把最后一个拖到最前
        model.PreviewMove(apps[0], 2);
        var outcome = model.Commit();

        var expected = new[] { apps[4], apps[1], apps[0], apps[2], apps[3] };

        Check(checks, "5 个应用拖拽排序提交成功", outcome == GlassDesk.Dock.ReorderOutcome.Committed && saveCount == 1,
            $"结果 {outcome}，保存次数 {saveCount}");

        // 「重启」= 换一个进程视角重新加载布局文件。
        var reloaded = new LayoutStore(
            dockFile,
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        _ = reloaded.LoadDock();
        var persisted = reloaded.Dock.PinnedItemIds;

        Check(checks, "重启后 5 个应用顺序正确（D-02）",
            persisted.Count == AppCount && persisted.SequenceEqual(expected),
            $"落盘顺序：{string.Join(',', persisted)}");

        Check(checks, "布局文件里只有 5 个应用 ID，没有内置入口（缺陷 1 的验收标准）",
            persisted.Count == AppCount && !persisted.Any(GlassDesk.Dock.DockLayoutPolicy.IsEntryTile),
            $"共 {persisted.Count} 项，其中 entry:* {persisted.Count(GlassDesk.Dock.DockLayoutPolicy.IsEntryTile)} 项");

        // 旧布局清理：故意写进三个入口 + 一个陌生 id，重跑清洗。
        var polluted = new List<string>(expected);
        polluted.Insert(1, "entry:launcher");
        polluted.Add("entry:showdesktop");
        polluted.Add("entry:recyclebin");
        polluted.Add("早就删掉的条目id");
        layout.Dock.PinnedItemIds = polluted;
        layout.SaveDock();

        var cleaned = GlassDesk.Dock.DockLayoutPolicy.Sanitize(
            layout.Dock.PinnedItemIds, id => host.Library.Find(id) is not null);

        var cleanedReload = new LayoutStore(
            dockFile,
            Path.Combine(paths.RootDirectory, "launcher-layout.json"));
        layout.Dock.PinnedItemIds = cleaned;
        layout.SaveDock();
        _ = cleanedReload.LoadDock();

        Check(checks, "旧布局里的三个入口与陌生 id 被清掉，应用顺序不变",
            cleanedReload.Dock.PinnedItemIds.SequenceEqual(expected),
            $"清理后：{string.Join(',', cleanedReload.Dock.PinnedItemIds)}");

        report.AppendLine(CultureInfo.InvariantCulture, $"布局文件：{dockFile}");
        report.AppendLine();
    }
    // ---- 批次 1 的各节 ----

    /// <summary>
    /// 写出带显式身份的样本快捷方式并导入，断言身份跟着进来。
    /// </summary>
    private static void CheckImportedIdentity(
        AppServiceHost host,
        AppPaths paths,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var samples = Path.Combine(paths.RootDirectory, "身份样本");
        _ = Directory.CreateDirectory(samples);

        var withIdentityPath = Path.Combine(samples, "带 身份 的记事本.lnk");
        var withIdentity = ShortcutWriter.Write(withIdentityPath, new ShortcutDefinition(
            TargetPath: @"C:\Windows\System32\notepad.exe",
            Description: "阶段 4 验收样本",
            Aumid: NotepadAumid));

        Check(checks, "带身份的样本快捷方式写出成功", withIdentity, withIdentityPath);

        if (!withIdentity)
        {
            return;
        }

        var resolved = ShortcutResolver.Resolve(withIdentityPath);
        Check(checks, "样本上的显式身份能被读回", resolved?.Aumid == NotepadAumid,
            $"读回：{resolved?.Aumid ?? "null"}");

        var importResult = host.Import.ImportItems([new ImportRequest { Source = withIdentityPath }]);
        Check(checks, "带身份的样本导入成功", importResult.Added.Count == 1,
            $"新增 {importResult.Added.Count}，失败 {importResult.Failures.Count}");

        var entry = importResult.Added.Count > 0 ? importResult.Added[0] : null;
        Check(checks, "条目上的身份与样本一致", entry?.AppUserModelId == NotepadAumid,
            $"条目身份：{entry?.AppUserModelId ?? "null"}");

        Check(checks, "会话内解析不再读文件也返回同一身份",
            WindowMatcher.ResolveAumid(entry!) == NotepadAumid,
            WindowMatcher.ResolveAumid(entry!) ?? "null");

        // 对照组：不带身份的普通快捷方式，身份保持 null——
        // 身份匹配是增强不是前提，普通 exe 走映像路径。
        var plainPath = Path.Combine(samples, "普通 的快捷方式.lnk");
        var plainWritten = ShortcutWriter.Write(plainPath, new ShortcutDefinition(
            TargetPath: @"C:\Windows\System32\charmap.exe",
            Description: "阶段 4 验收对照样本"));

        if (plainWritten)
        {
            var plainImport = host.Import.ImportItems([new ImportRequest { Source = plainPath }]);
            var plainEntry = plainImport.Added[0];
            Check(checks, "不带身份的快捷方式导入后身份为 null",
                plainEntry is not null && plainEntry.AppUserModelId is null,
                $"条目身份：{plainEntry?.AppUserModelId ?? "null"}");
        }
        else
        {
            Check(checks, "不带身份的快捷方式导入后身份为 null", false, "对照样本写出失败");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"样本目录：{samples}");
        report.AppendLine();
    }

    /// <summary>
    /// 匹配规则对伪造快照的断言。规则的实测依据见探针输出存档。
    /// </summary>
    private static void CheckMatchingRules(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        static WindowIdentityInfo Window(
            nint handle,
            string? imagePath,
            string? windowAumid,
            string? processAumid)
            => new(handle, $"窗口 {handle}", 1000, false, imagePath, windowAumid, processAumid);

        // 经典程序：条目无身份、目标 exe → 按映像路径。
        var classic = WindowMatcher.MatchWindows(
            new AppEntry { Id = "c", Kind = AppItemKind.Program, Target = ClassicTarget },
            [Window(1, @"C:\WINDOWS\SYSTEM32\CHARMAP.EXE", null, null)],
            aumid: null);
        Check(checks, "经典程序按映像路径匹配（大小写不敏感）",
            classic.Count == 1 && classic[0].Handle == 1, $"匹配 {classic.Count} 个");

        // 打包应用：条目身份 == 归属进程的包身份。
        var packaged = WindowMatcher.MatchWindows(
            new AppEntry { Id = "p", Kind = AppItemKind.Program, Target = @"C:\Windows\System32\notepad.exe", AppUserModelId = NotepadAumid },
            [Window(2, @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_...\Notepad.exe", null, NotepadAumid)],
            NotepadAumid);
        Check(checks, "打包应用按进程包身份匹配",
            packaged.Count == 1 && packaged[0].Handle == 2, $"匹配 {packaged.Count} 个");

        // UWP：条目身份 == 框架窗口上显式设置的身份。
        const string uwpIdentity = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
        var uwp = WindowMatcher.MatchWindows(
            new AppEntry { Id = "u", Kind = AppItemKind.Shortcut, Target = @"C:\某\设置.lnk", AppUserModelId = uwpIdentity },
            [Window(3, @"C:\Windows\System32\ApplicationFrameHost.exe", uwpIdentity, null)],
            uwpIdentity);
        Check(checks, "UWP 框架窗口按窗口显式身份匹配",
            uwp.Count == 1 && uwp[0].Handle == 3, $"匹配 {uwp.Count} 个");

        // 身份不一致：即使宿主进程路径相同也不算（两个 UWP 共用一个宿主）。
        var conflict = WindowMatcher.MatchWindows(
            new AppEntry { Id = "x", Kind = AppItemKind.Shortcut, Target = @"C:\某\应用A.lnk", AppUserModelId = "com.app.a" },
            [Window(4, @"C:\Windows\System32\ApplicationFrameHost.exe", "com.app.b", null)],
            "com.app.a");
        Check(checks, "身份不一致的窗口不匹配，也不回退到宿主进程路径",
            conflict.Count == 0, $"匹配 {conflict.Count} 个");

        // 脚本目标不做映像匹配（真身是 cmd.exe）。
        var script = WindowMatcher.MatchWindows(
            new AppEntry { Id = "s", Kind = AppItemKind.Program, Target = @"C:\tools\run.bat" },
            [Window(5, @"C:\tools\run.bat", null, null)],
            aumid: null);
        Check(checks, "脚本目标不做映像匹配（退化成老实启动）",
            script.Count == 0, $"匹配 {script.Count} 个");

        report.AppendLine("规则依据：阶段 4 探针 A 实测（docs/evidence/阶段4-探针实测输出.txt）。");
        report.AppendLine();
    }

    /// <summary>
    /// 运行状态跟踪：初始枚举必须看见已有窗口；自己启动的靶子出现与消失
    /// 都要被调和出来。返回「靶子启动是否执行」。
    /// </summary>
    private static bool CheckRunningTracker(
        AppServiceHost host,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var initial = host.Running.LastWindows.Count;
        Check(checks, "启动时已完成初始枚举（看见启动前已有的窗口）", initial > 0,
            $"初始窗口 {initial} 个");

        if (Platform.Shell.WindowLocator.FindWindowsForImage(ClassicTarget).Count > 0)
        {
            report.AppendLine("用户桌面已有字符映射表窗口；出现/消失调和检查跳过并标注为未验证。");
            report.AppendLine();
            Skip(checks, report, "运行集合的调和检查跳过（用户桌面已有靶子窗口）");
            return false;
        }

        var importResult = host.Import.ImportItems([new ImportRequest { Source = ClassicTarget }]);
        var entry = importResult.Added.Count > 0 ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0]
            : null;

        if (entry is null)
        {
            Check(checks, "靶子条目导入成功", false, "导入失败");
            return false;
        }

        Check(checks, "靶子启动前不在运行集合中", !host.Running.IsRunning(entry), "未运行");

        var outcome = ShellExecutor.Open(ClassicTarget, null, null);
        if (!outcome.Started || outcome.ProcessId == 0)
        {
            Check(checks, "靶子启动成功", false, $"外壳错误码 {outcome.ErrorCode}");
            return true;
        }

        try
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"靶子 charmap 进程 id：{outcome.ProcessId}");

            // 窗口创建是外部真实事件，轮询等待是这里合适的做法。
            var appeared = false;
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                if (host.Running.Reconcile() > 0 && host.Running.IsRunning(entry))
                {
                    appeared = true;
                    break;
                }

                Thread.Sleep(300);
            }

            Check(checks, "靶子窗口出现后进入运行集合（D-07 的服务层）", appeared,
                appeared ? "调和后 IsRunning = true" : "10 秒内未观察到窗口");

            var matching = host.Running.FindWindows(entry);
            Check(checks, "匹配到的窗口确实是靶子的",
                matching.Count == 0 || matching.All(w => w.ImagePath != null
                    && w.ImagePath.EndsWith("charmap.exe", StringComparison.OrdinalIgnoreCase)),
                $"匹配 {matching.Count} 个");
        }
        finally
        {
            // 收尾只按记录的 pid。
            try
            {
                using var process = Process.GetProcessById((int)outcome.ProcessId);
                if (!process.HasExited)
                {
                    process.Kill();
                    _ = process.WaitForExit(3000);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // 靶子已自行退出。
            }
        }

        var gone = false;
        var goneDeadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < goneDeadline)
        {
            Thread.Sleep(300);

            _ = host.Running.Reconcile();

            if (!host.Running.IsRunning(entry))
            {
                gone = true;
                break;
            }
        }

        Check(checks, "靶子进程结束后退出运行集合（调和覆盖「消失」）", gone,
            gone ? "调和后 IsRunning = false" : "10 秒后仍显示运行中");
        report.AppendLine();
        return true;
    }

    /// <summary>
    /// D-09 的服务层集成断言：第一次点击启动，第二次点击激活。
    /// </summary>
    private static void CheckActivateOrLaunch(
        AppServiceHost host,
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report)
    {
        var importResult = host.Import.ImportItems([new ImportRequest { Source = ClassicTarget }]);
        var entry = importResult.Added.Count > 0 ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0]
            : null;

        if (entry is null)
        {
            Check(checks, "激活检查的靶子条目存在", false, "导入失败");
            return;
        }

        var first = host.Launch.ActivateOrLaunch(entry.Id);
        var spawnedProcessId = host.Launch.LastLaunchedProcessId;
        report.AppendLine(CultureInfo.InvariantCulture, $"第一次 ActivateOrLaunch → {first.Outcome}");

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var windowSeen = false;

            while (DateTime.UtcNow < deadline)
            {
                if (WindowLocator.FindWindowsForImage(ClassicTarget).Count > 0)
                {
                    windowSeen = true;
                    break;
                }

                Thread.Sleep(300);
            }

            Check(checks, "第一次请求启动了应用（Launched）", first.Outcome == ActivationOutcome.Launched, first.Outcome.ToString());
            Check(checks, "窗口随后真实出现", windowSeen, windowSeen ? "已出现" : "10 秒内未出现");

            var second = host.Launch.ActivateOrLaunch(entry.Id);
            report.AppendLine(CultureInfo.InvariantCulture, $"第二次 ActivateOrLaunch → {second.Outcome}");
            Check(checks, "第二次请求激活已有窗口而不是再起一个（D-09）",
                second.Outcome is ActivationOutcome.Activated or ActivationOutcome.MultipleWindowsFound,
                second.Outcome.ToString());
        }
        finally
        {
            Phase3Acceptance.KillRecordedProcess(spawnedProcessId);
        }

        report.AppendLine();
    }

    private static void Skip(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        StringBuilder report,
        string reason)
    {
        // 跳过是独立的一类结论，**不计入通过**：它说明这台机器缺这个条件，
        // 而不是验证了什么。交付报告里按未验证列出。
        checks.Add((reason, CheckStatus.Skipped, reason));
        report.AppendLine(CultureInfo.InvariantCulture, $"跳过（未验证）：{reason}");
    }

    private static void Section(StringBuilder report, string title)
    {
        report.AppendLine();
        report.AppendLine(title);
        report.AppendLine(new string('-', 46));
    }

    private static void Check(
        List<(string Label, CheckStatus Status, string Detail)> checks,
        string label,
        bool passed,
        string detail)
        => checks.Add((label, passed ? CheckStatus.Passed : CheckStatus.Failed, detail));
}
