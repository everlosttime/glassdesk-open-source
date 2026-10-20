using System.Diagnostics;
using System.IO;
using System.Text;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 阶段 4 的独立桌面测试驱动。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由只有一条：既有的两组测试都做不到「真实桌面输入」。单元测试与阶段验收
/// 在进程内驱动状态机，<c>GlassDesk.UiAcceptance</c> 驱动的是 WPF 路由事件；
/// 两者都碰不到<strong>原生子对话框</strong>（文件选择器）和<strong>跨窗口拖拽</strong>。
/// 上一轮用 Computer Use 去补这两块，结果是它把原生对话框的坐标套进了程序坞的小窗口
/// （<c>point (469, -379) is outside window bounds</c>），跨窗口拖拽则直接返回
/// <c>not target window</c>。
/// </para>
/// <para>
/// 这个驱动换了一条路：用 <b>UI Automation 按身份</b>找窗口与控件，
/// 用 <b>SendInput</b> 注入真实的鼠标键盘事件，并用<strong>可观察的结果</strong>
/// （前台句柄、条目落盘、对话框关闭、窗口形态）判断通过与否。
/// </para>
/// <para>
/// 退出码：要求运行的用例全部通过才是 0；有失败是 1；没有失败但有阻塞或未跑是 2。
/// 跳过、缺产物、锁屏都不会得到 0。
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly object ReportGate = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 4 && args[0] == "--owned-target")
            return OwnedTarget.Run(args[1], int.Parse(args[2]), args[3]);

        // 参数错误是「这次没法跑」，不是产品缺陷：打印帮助并以阻塞退出，
        // 不要让一个没写对的开关看起来像崩溃。
        DriverOptions options;

        try
        {
            options = DriverOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(DriverOptions.HelpText);
            return 0;
        }

        // 只读诊断不需要门禁：它们不注入任何输入，也不碰任何窗口。
        if (options.DumpWindows)
        {
            Console.WriteLine("Timestamp=" + DateTimeOffset.Now.ToString("O"));
            foreach (var file in new[] { typeof(Program).Assembly.Location, options.ProductPath, Path.ChangeExtension(options.ProductPath, ".dll") })
                if (File.Exists(file)) Console.WriteLine("SHA256 " + file + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
            if (options.ProbeSession && DesktopGate.BlockedReason(true) is { } reason)
            {
                Console.WriteLine("BLOCKED: " + reason);
                DumpWindows();
                return 2;
            }
            return DumpWindows();
        }

        if (options.DumpProcessTree is { } processId)
        {
            return DumpProcessTree(processId);
        }

        var report = new Report(options.ReportPath);
        var environment = Report.Snapshot(File.Exists(options.ProductPath) ? options.ProductPath : null, DriverDescription);
        report.SetEnvironment(environment);
        for (var round = 1; round <= options.Repeat; round++)
        {
            if (options.Wants("picker")) report.Plan($"{PickerCase.CaseName}（第 {round}/{options.Repeat} 轮）", "R-07；D-08", "原生选择器重新定位后目标恢复并能启动");
            if (options.Wants("explorer")) report.Plan($"{ExplorerDropCase.CaseName}（第 {round}/{options.Repeat} 轮）", "D-01；D-23；R-07", "资源管理器真实拖入、固定及重启持久化");
            foreach (var item in BatchCCases.Catalog.Where(c => options.Wants(c.Id)))
                report.Plan(BatchCCases.Name(item.Name, round, options.Repeat), item.Requirement, item.Name);
        }
        report.Note("请求范围：" + options.CaseFilter + "；all 才是完整桌面清单。单项通过不代表全量通过。");
        report.Write();

        using var watchdog = StartWatchdog(report, options.TimeoutMinutes);

        try
        {
            return RunGuarded(options, report);
        }
        catch (Exception ex)
        {
            lock (ReportGate)
            {
                report.Add(new CaseRecord(
                    "驱动自身运行",
                    "—",
                    DriverDescription,
                    "驱动完成全部用例并写出结论",
                    "驱动自身抛出未预期的异常，这不是产品结论：" + Environment.NewLine + ex,
                    CaseStatus.Failed,
                    [],
                    null));

                report.Write();
            }

            return 1;
        }
    }

    private const string DriverDescription = "真实桌面输入：UI Automation 定位控件 + SendInput 注入鼠标键盘";

    private static int RunGuarded(DriverOptions options, Report report)
    {
        // ---- 门禁：不满足就一个输入事件都不发 ----

        var blocked = DesktopGate.BlockedReason(options.OptedIn);

        if (blocked is not null)
        {
            lock (ReportGate)
            {
                report.Note("环境门禁未通过，未注入任何输入：" + blocked);
                report.Add(new CaseRecord(
                    "桌面驱动环境门禁",
                    "计划书：桌面验收只在测试虚拟机（独立桌面）运行",
                    "无（门禁未过，未注入输入）",
                    "存在一个只有测试在用的交互式桌面",
                    blocked,
                    CaseStatus.Blocked,
                    [],
                    null));

                report.Write();
            }

            return 2;
        }

        NativeInput.VerifyLayout();

        if (!File.Exists(options.ProductPath))
        {
            return Blocked(report, $"产品产物不存在：{options.ProductPath}。先跑 build.ps1 构建，或用 --product 指定路径。");
        }

        if (!File.Exists(options.FixturePath))
        {
            return Blocked(report, $"原生选择器夹具不存在：{options.FixturePath}。先跑 build.ps1 构建，或用 --fixture 指定路径。");
        }

        var workRoot = DriverContext.DefaultWorkRoot();
        Directory.CreateDirectory(workRoot);

        var context = new DriverContext(options.ProductPath, options.FixturePath, workRoot, report);

        report.Note($"工作目录：{workRoot}");
        report.Note($"产品：{options.ProductPath}");
        report.Note($"夹具：{options.FixturePath}");
        report.Note($"每个请求用例运行 {options.Repeat} 轮；范围 {options.CaseFilter}。");

        for (var round = 1; round <= options.Repeat; round++)
        {
            report.Note($"===== 第 {round}/{options.Repeat} 轮 =====");

            if (options.Wants("picker")) Add(report, () => PickerCase.Run(context, round, options.Repeat));
            if (options.Wants("explorer")) Add(report, () => ExplorerDropCase.Run(context, round, options.Repeat));
            foreach (var item in BatchCCases.Catalog.Where(c => options.Wants(c.Id)))
                Add(report, () => BatchCCases.Run(context, item.Id, round, options.Repeat));
        }

        lock (ReportGate)
        {
            report.Note($"任务完成，工作目录保留在 {workRoot}（证据与截图都在里面）。");
            report.Write();
            Console.WriteLine($"报告：{report.Path}");
            Console.WriteLine($"通过 {report.Cases.Count(c => c.Status == CaseStatus.Passed)}，"
                + $"失败 {report.Cases.Count(c => c.Status == CaseStatus.Failed)}，"
                + $"阻塞 {report.Cases.Count(c => c.Status == CaseStatus.Blocked)}。");

            return report.ExitCode();
        }
    }

    /// <summary>跑一条用例。驱动自身的异常也要留下痕迹，而不是让进程直接崩掉。</summary>
    private static void Add(Report report, Func<CaseRecord> run)
    {
        CaseRecord record;

        try
        {
            record = run();
        }
        catch (Exception ex)
        {
            record = new CaseRecord(
                "驱动执行用例时抛出异常",
                "—",
                DriverDescription,
                "用例跑完并给出结论",
                "驱动自身异常，这不是产品结论：" + Environment.NewLine + ex,
                CaseStatus.Failed,
                [],
                null);
        }

        lock (ReportGate)
        {
            report.Add(record);
            report.Write();
        }
    }

    private static int Blocked(Report report, string reason)
    {
        lock (ReportGate)
        {
            report.Note(reason);
            report.Add(new CaseRecord(
                "桌面驱动前置条件",
                "—",
                "无（未注入输入）",
                "产品与夹具产物都已构建",
                reason,
                CaseStatus.Blocked,
                [],
                null));

            report.Write();
        }

        return 2;
    }

    /// <summary>
    /// 总时限。跨进程的 UIA 调用一旦卡住，驱动自己没有恢复的办法，
    /// 因此由看门线程写出报告并以「阻塞」退出（退出码 2），而不是挂着不动。
    /// </summary>
    private static Timer StartWatchdog(Report report, int timeoutMinutes)
    {
        return new Timer(
            _ =>
            {
                lock (ReportGate)
                {
                    report.Note($"总时限 {timeoutMinutes} 分钟已到，写出当前报告并以退出码 2 结束。");
                    report.Add(new CaseRecord(
                        "桌面驱动总时限",
                        "—",
                        DriverDescription,
                        $"全部用例在 {timeoutMinutes} 分钟内跑完",
                        $"超过 {timeoutMinutes} 分钟仍未跑完，已中止。预先登记但未完成的用例保留为 NotRun。",
                        CaseStatus.Blocked,
                        [],
                        null));

                    report.Write();
                }

                Environment.Exit(2);
            },
            null,
            TimeSpan.FromMinutes(timeoutMinutes),
            System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>只读诊断：列出当前顶层窗口。首次在某台机器上跑时先用它核对窗口类名。</summary>
    private static int DumpWindows()
    {
        Console.WriteLine($"虚拟桌面 {Win32.VirtualScreen().Describe()}；系统 DPI {Win32.SystemDpi()}；本进程 DPI 感知 {Win32.DpiAwareness()}");
        Console.WriteLine($"输入桌面 {Win32.InputDesktopName() ?? "（读取失败）"}；本进程桌面 {Win32.CurrentThreadDesktopName() ?? "（读取失败）"}");
        Console.WriteLine($"窗口站可见：{Win32.WindowStationIsVisible()}");
        Console.WriteLine($"活动控制台会话 {Win32.ActiveConsoleSessionId()}；本进程会话 {Process.GetCurrentProcess().SessionId}");
        Console.WriteLine();

        foreach (var facts in Win32.TopLevelWindows().Select(Win32.Describe).Where(f => f is not null))
        {
            Console.WriteLine("  " + facts!.Describe());
        }

        return 0;
    }

    /// <summary>只读诊断：按进程号打印 UIA 子树。用于核对控件身份，尤其是文件对话框。</summary>
    private static int DumpProcessTree(int processId)
    {
        if (!Uia.Until(() => Uia.FindTopWindow(processId, null, null) is not null, 5000))
        {
            Console.WriteLine($"pid {processId} 没有顶层窗口。");
            return 2;
        }

        foreach (var window in FindAllWindows(processId))
        {
            Console.WriteLine(Uia.Describe(window));
            Console.WriteLine(Uia.Dump(window));
            Console.WriteLine();
        }

        return 0;
    }

    private static IEnumerable<System.Windows.Automation.AutomationElement> FindAllWindows(int processId)
    {
        var condition = new System.Windows.Automation.PropertyCondition(
            System.Windows.Automation.AutomationElement.ProcessIdProperty,
            processId);

        var found = System.Windows.Automation.AutomationElement.RootElement
            .FindAll(System.Windows.Automation.TreeScope.Children, condition);

        return found.Cast<System.Windows.Automation.AutomationElement>();
    }
}

/// <summary>命令行的全部开关。</summary>
internal sealed class DriverOptions
{
    internal string ReportPath { get; private init; } = Path.GetFullPath("phase4-desktop.json");

    internal string ProductPath { get; private init; } = string.Empty;

    internal string FixturePath { get; private init; } = string.Empty;

    internal int Repeat { get; private init; } = 1;

    internal string CaseFilter { get; private init; } = "all";
    internal bool Wants(string id) => CaseFilter == "all" || CaseFilter == id;

    internal int TimeoutMinutes { get; private init; } = 30;

    internal bool OptedIn { get; private init; }

    internal bool Help { get; private init; }

    internal bool DumpWindows { get; private init; }
    internal bool ProbeSession { get; private init; }

    internal int? DumpProcessTree { get; private init; }

    internal static DriverOptions Parse(string[] args)
    {
        var root = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains(
            Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        string DefaultUnder(string relative) => root is null
            ? string.Empty
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
                .Replace("$(Configuration)", configuration);

        var report = Path.GetFullPath("phase4-desktop.json");
        var product = DefaultUnder("src/GlassDesk.App/bin/$(Configuration)/net10.0-windows/GlassDesk.exe");
        var fixture = DefaultUnder("tests/GlassDesk.UiAcceptance/bin/$(Configuration)/net10.0-windows/GlassDesk.UiAcceptance.exe");
        var repeat = 1;
        var caseFilter = "all";
        var timeout = 30;
        var optedIn = string.Equals(
            Environment.GetEnvironmentVariable(DesktopGate.OptInEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        var help = false;
        var dumpWindows = false;
        var probeSession = false;
        int? dumpTree = null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];

            switch (token)
            {
                case "--case":
                    caseFilter = Next(args, ref index, token);
                    if (caseFilter is not ("all" or "picker" or "explorer") && !BatchCCases.Catalog.Any(c => c.Id == caseFilter))
                        throw new ArgumentException("Unknown case: " + caseFilter);
                    break;
                case "--report":
                    report = Next(args, ref index, token);
                    break;

                case "--product":
                    product = Next(args, ref index, token);
                    break;

                case "--fixture":
                    fixture = Next(args, ref index, token);
                    break;

                case "--repeat":
                    repeat = int.TryParse(Next(args, ref index, token), out var parsedRepeat) && parsedRepeat > 0
                        ? parsedRepeat
                        : throw new ArgumentException("--repeat 需要正整数。");

                    break;

                case "--timeout":
                    timeout = int.TryParse(Next(args, ref index, token), out var parsedTimeout) && parsedTimeout > 0
                        ? parsedTimeout
                        : throw new ArgumentException("--timeout 需要正整数（分钟）。");

                    break;

                case DesktopGate.OptInFlag:
                    optedIn = true;
                    break;

                case "--dump-windows":
                    dumpWindows = true;
                    break;

                case "--probe-session":
                    dumpWindows = true;
                    probeSession = true;
                    break;

                case "--dump-uia":
                    dumpTree = int.TryParse(Next(args, ref index, token), out var parsedPid)
                        ? parsedPid
                        : throw new ArgumentException("--dump-uia 需要进程号。");

                    break;

                case "--help":
                case "-h":
                    help = true;
                    break;

                default:
                    throw new ArgumentException($"无法识别的参数：{token}{Environment.NewLine}{HelpText}");
            }
        }

        return new DriverOptions
        {
            ReportPath = report,
            ProductPath = product,
            FixturePath = fixture,
            Repeat = repeat,
            CaseFilter = caseFilter,
            TimeoutMinutes = timeout,
            OptedIn = optedIn,
            Help = help,
            DumpWindows = dumpWindows,
            ProbeSession = probeSession,
            DumpProcessTree = dumpTree,
        };
    }

    internal const string HelpText =
        """
        GlassDesk 桌面验收驱动（阶段 4）

        用法：GlassDesk.DesktopAcceptance.exe [选项]

          --i-am-on-isolated-desktop  声明「我在只有测试在用的交互式桌面上」。
                                      没有这个声明（或环境变量 GLASSDESK_ISOLATED_DESKTOP=1）
                                      就一个输入事件都不发，直接记阻塞退出。
          --case <id>                 all（默认）或 picker/explorer/launch/reorder/focus/magnification/autohide/desktop/packaged/settings。
          --repeat <n>                每个请求用例连续运行的轮数，默认 1。
          --timeout <分钟>            总时限，默认 30 分钟。到点写出当前报告并以 2 退出。
          --report <文件>             报告路径，默认 phase4-desktop.json（.log 是同目录的文本版）。
          --product <路径>            产品产物，默认按仓库相对路径推导。
          --fixture <路径>            原生选择器夹具，默认按仓库相对路径推导。
          --dump-windows              只读诊断：列出顶层窗口并退出（不需要门禁）。
          --probe-session             只读列举窗口、时间和产物哈希；非活动交互桌面退出 2，不注入输入。
          --dump-uia <pid>            只读诊断：打印该进程的 UIA 子树并退出（不需要门禁）。
          --help                      显示本帮助。

        退出码：0 全部通过；1 有失败；2 有阻塞或未跑。
        """;

    private static string Next(string[] args, ref int index, string token)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{token} 后面缺少值。");
        }

        index++;
        return args[index];
    }

    /// <summary>从自己的产物目录往上找仓库根（认 GlassDesk.slnx），找不到就返回 null。</summary>
    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GlassDesk.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
