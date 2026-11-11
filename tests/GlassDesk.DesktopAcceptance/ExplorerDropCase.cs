using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 最小验证二：从<strong>真实资源管理器</strong>把一个带中文与空格的文件
/// 拖进真实程序坞，核对应用库新增并固定，且重启后仍在。
/// </summary>
/// <remarks>
/// 与既有的 <c>GlassDesk.UiAcceptance --drag</c> 的区别在于<strong>源</strong>：
/// 那个用一个自绘的 WPF 窗口当拖拽源，只证明 OLE 通路在跨进程时能把
/// <c>CF_HDROP</c> 送到；这里用的是用户日常真正会用的那个源——资源管理器窗口里的
/// 文件条目，由真实的按下、移动、松开序列驱动。上一轮 Computer Use 的
/// <c>drag</c> 明确拒绝「终点属于另一窗口」，返回 <c>not target window</c>，
/// 那不是产品拖放失败；本驱动把整段手势发在同一串系统输入里，因此能跨窗口。
///
/// 落盘与重启这三步刻意分开核对：拖拽期间不读文件（避免把「预览」当成「提交」），
/// 松手后读 <c>apps.json</c> 与 <c>dock-layout.json</c>，重启后再用
/// 一个全新进程的 <c>--list-library</c> 从磁盘读一遍。
/// </remarks>
internal static class ExplorerDropCase
{
    internal const string CaseName = "资源管理器拖入真实程序坞并固定（中文空格路径）";

    private const string Requirement = "D-01 导入（拖拽入口）；D-23 程序坞窗口形态；R-07 重载后条目仍在";

    private const string Driver = "真实桌面输入：UIA 定位资源管理器条目与程序坞窗口 + SendInput 拖拽";

    private const string Expected =
        "从资源管理器把一个带中文与空格的文件拖到程序坞上松手后，应用库出现该条目并固定到程序坞；"
        + "重启产品后条目仍在且仍固定；期间程序坞窗口保持 WS_EX_NOACTIVATE、不出现在任务栏，尺寸不因测试而改变。";

    internal static CaseRecord Run(DriverContext context, int round, int rounds)
    {
        var name = $"{CaseName}（第 {round}/{rounds} 轮）";
        var evidence = new List<string>();
        string? screenshot = null;

        CaseRecord Conclude(CaseStatus status, string actual) =>
            new(name, Requirement, Driver, Expected, actual, status, [.. evidence], screenshot);

        var work = context.NewWorkDirectory($"资源管理器拖入-第{round}轮");
        var folder = Path.Combine(work, "资源管理器 拖入 中文 空格");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "拖入条目 中文 空格.txt");
        File.WriteAllText(file, "glassdesk desktop driver drop sample");
        evidence.Add($"被拖入的文件：{file}");

        // ---- 1. 前置：真实应用库必须是干净的 ----

        var libraryRoot = ProductLibrary.DefaultRoot;
        var foreign = ProductLibrary.ForeignEntries(libraryRoot, DriverContext.WorkRootFamily);

        if (foreign.Count > 0)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"这台机器的 GlassDesk 应用库里有 {foreign.Count} 条不是本驱动创建的记录，"
                + "拖入验证会往库里写东西，因此拒绝在真实数据上运行："
                + Environment.NewLine
                + string.Join(Environment.NewLine, foreign.Take(5).Select(entry => "  · " + entry.Target))
                + Environment.NewLine
                + "请在独立测试账户或虚拟机里跑这一轮。");
        }

        var existing = DesktopGate.ExistingInstanceReason();
        if (existing is not null)
        {
            return Conclude(CaseStatus.Blocked, existing);
        }

        // ---- 2. 启动真实产品，读它的程序坞窗口 ----

        using var product = ProcessLease.Launch(context.ProductPath, []);
        evidence.Add($"产品命令：{product.CommandLine}");
        context.Report.Note($"最小验证二（第 {round} 轮）：产品已启动 pid {product.ProcessId}");

        var dock = Uia.WaitForTopWindow(product.ProcessId, null, "程序坞", 60000);
        if (dock is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"60 秒内没等到产品的程序坞窗口（pid {product.ProcessId}）。该进程的顶层窗口："
                + Environment.NewLine + DescribeWindows(product.ProcessId));
        }

        var before = DescribeDock(dock);
        evidence.Add("拖入前：" + before);
        screenshot = TryShot(work, round, "拖入前");

        // ---- 3. 打开真实资源管理器，按身份找到那个文件条目 ----

        var leaf = Path.GetFileName(folder);

        // 不用 UseShellExecute：它在 .NET 上不支持 ArgumentList，而路径里有空格，
        // 靠字符串拼接去拼引号迟早出错。explorer.exe 会把窗口交给已经在跑的 shell 进程，
        // 启动出来的这个进程随即退出，所以不纳入进程租约——收尾靠按唯一文件夹名关闭窗口。
        _ = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            ArgumentList = { folder },
        });

        var explorerWindow = Uia.WaitForWindow(
            "CabinetWClass",
            leaf,
            60000,
            accept: window => IsExplorerProcess(window));

        if (explorerWindow is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"60 秒内没等到标题含「{leaf}」的资源管理器窗口（类 CabinetWClass）。"
                + "枚举到的顶层窗口：" + Environment.NewLine + DescribeWindows(null));
        }

        evidence.Add($"资源管理器窗口：{Uia.Describe(explorerWindow)}");

        // 窗口出现不等于内容已经填好：资源管理器是异步填充列表的，
        // 立刻找一次会得出「驱动定位不到条目」这个错误结论。因此轮询等它出现。
        var item = Uia.Until(() => FindFileItem(explorerWindow, file) is not null, 30000, 400)
            ? FindFileItem(explorerWindow, file)
            : null;

        if (item is null)
        {
            screenshot = TryShot(work, round, "找不到文件条目");
            return Conclude(
                CaseStatus.Blocked,
                $"资源管理器窗口里等了 30 秒也没出现名为「{Path.GetFileName(file)}」的条目。"
                + "驱动没能定位到拖拽源，产品没有被验证到。该窗口的条目控件树已存档："
                + Environment.NewLine + DumpItems(work, round, explorerWindow));
        }

        evidence.Add($"拖拽源条目：{Uia.Describe(item)}");

        var sourceRect = Uia.RectOf(item);
        var drop = DropPoint(dock);

        if (sourceRect.IsEmpty || !Win32.VirtualScreen().Contains(new ScreenPoint(sourceRect.Left + 1, sourceRect.Top + 1)))
        {
            return Conclude(
                CaseStatus.Blocked,
                $"拖拽源条目不在可见桌面内（矩形 {sourceRect.Describe()}，虚拟桌面 {Win32.VirtualScreen().Describe()}）。");
        }

        var start = new ScreenPoint(sourceRect.Left + (sourceRect.Width / 2), sourceRect.Top + (sourceRect.Height / 2));
        evidence.Add($"拖拽起点 {start.X},{start.Y}（资源管理器条目中心）；终点 {drop.Point.X},{drop.Point.Y}（{drop.How}）");

        // 按下之前先确认光标下面确实是资源管理器。第 1 轮实测失败过：起点没做这个检查时，
        // 按下与移动发给了别的窗口（例如还没让出前台的程序坞），拖动整段作废且看不出来。
        var explorerProcess = Uia.ProcessIdOf(explorerWindow);
        var sourceReady = false;
        WindowFacts? sourceOwner = null;

        for (var attempt = 0; attempt < 6 && !sourceReady; attempt++)
        {
            NativeInput.MoveTo(start);
            Thread.Sleep(250);

            var top = Win32.WindowAt(start);
            var root = top == 0 ? 0 : Win32.RootAncestor(top);
            sourceOwner = Win32.Describe(root);
            sourceReady = root != 0 && Win32.ProcessOf(root) == explorerProcess;

            if (!sourceReady)
            {
                Thread.Sleep(700);
            }
        }

        if (!sourceReady)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"拖拽起点 {start.X},{start.Y} 上方的窗口不属于资源管理器（pid {explorerProcess}），"
                + $"实际是 {sourceOwner?.Describe() ?? "（没有窗口）"}。拖动没有开始，产品没有被验证到。");
        }

        // ---- 4. 一次真实的跨窗口拖拽 ----

        NativeInput.DragPath(NativeInput.Interpolate(start, drop.Point, 24));

        // ---- 5. 等的仍然是可观察结果：条目落盘 ----

        var entriesAfterDrop = Array.Empty<LibraryEntry>();
        var landed = Uia.Until(
            () =>
            {
                try
                {
                    entriesAfterDrop = [.. ProductLibrary.ReadEntries(libraryRoot)];
                    return entriesAfterDrop.Any(entry => string.Equals(entry.Target, file, StringComparison.Ordinal));
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    // 产品可能正在原子替换文件，下一轮再读。
                    return false;
                }
            },
            40000,
            300);

        screenshot = TryShot(work, round, "拖入后");

        var status = StatusTextOf(dock);
        evidence.Add($"松手后程序坞上的状态文字：{status ?? "（没有文字）"}");
        evidence.Add("拖入后：" + DescribeDock(dock));
        evidence.Add(ProductLibrary.Describe(libraryRoot));

        if (!landed)
        {
            return Conclude(
                CaseStatus.Failed,
                $"松开鼠标后 40 秒内应用库里没有出现「{file}」。"
                + $"库现有 {entriesAfterDrop.Length} 条，程序坞状态文字是 {status ?? "（空）"}。"
                + "截图：" + (screenshot ?? "（拍不到）"));
        }

        var entry = entriesAfterDrop.Single(candidate => string.Equals(candidate.Target, file, StringComparison.Ordinal));

        // ---- 6. 固定到程序坞（拖入的语义就是「加入并固定」） ----

        var pinned = ProductLibrary.ReadPinned(libraryRoot);
        if (!pinned.Contains(entry.Id))
        {
            return Conclude(
                CaseStatus.Failed,
                $"条目已进入应用库（id {entry.Id}）但没有固定到程序坞；dock-layout.json 里的固定项："
                + string.Join("、", pinned));
        }

        evidence.Add($"固定项 id：{entry.Id}");

        var afterDropFacts = DescribeDock(dock);

        // ---- 7. 重启核对：新进程从磁盘读一遍 ----

        evidence.Add(product.Stop("拖入已落盘，准备重启核对持久化"));

        var listing = RunListLibrary(context);
        evidence.Add($"重启后 --list-library 的输出（末尾）：{Environment.NewLine}{Tail(listing)}");

        if (!listing.Contains(file, StringComparison.Ordinal))
        {
            return Conclude(
                CaseStatus.Failed,
                "重启后的新进程从磁盘列不出这条记录，说明固定只存在内存里。--list-library 输出末尾："
                + Environment.NewLine + Tail(listing));
        }

        using var relaunched = ProcessLease.Launch(context.ProductPath, []);
        var dockAgain = Uia.WaitForTopWindow(relaunched.ProcessId, null, "程序坞", 60000);

        if (dockAgain is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                "重启后等不到程序坞窗口。该进程的顶层窗口："
                + Environment.NewLine + DescribeWindows(relaunched.ProcessId));
        }

        var afterReload = DescribeDock(dockAgain);
        evidence.Add("重启后：" + afterReload);

        // 窗口形态不得因为测试而改变：三次取样都必须带生产要求的扩展样式。
        var shapes = new[] { before, afterDropFacts, afterReload };

        foreach (var shape in shapes)
        {
            if (!shape.Facts.HasNoActivate || shape.Facts.IsOnTaskbar)
            {
                return Conclude(
                    CaseStatus.Failed,
                    "程序坞窗口的形态与生产要求不符（要求 WS_EX_NOACTIVATE、不出现在任务栏）："
                    + shape.Facts.Describe());
            }
        }

        // 尺寸只对比「拖入后」与「重启后」这两次：程序坞按内容自适应大小，
        // 新磁贴进来时合法地变宽是产品设计，不是测试改动。内容相同的两次必须一模一样。
        if (afterDropFacts.Facts.Rect.Describe() != afterReload.Facts.Rect.Describe())
        {
            return Conclude(
                CaseStatus.Failed,
                "内容相同的两次启动（拖入后 / 重启后）程序坞尺寸却不一致："
                + $"{afterDropFacts.Facts.Rect.Describe()} → {afterReload.Facts.Rect.Describe()}。"
                + "说明重启后的窗口形态与拖入时不同。");
        }

        if (afterReload.Tiles < afterDropFacts.Tiles)
        {
            return Conclude(
                CaseStatus.Failed,
                $"重启后程序坞上的应用磁贴少了：重启前 {afterDropFacts.Tiles} 个，重启后 {afterReload.Tiles} 个。");
        }

        if (afterDropFacts.Tiles <= before.Tiles && before.Tiles == 0)
        {
            return Conclude(
                CaseStatus.Blocked,
                "程序坞窗口的控件树里看不到任何应用磁贴图标（拖入前后都是 0 个），"
                + "界面侧证据取不到。这属于驱动定位手段不足，不能算产品通过，也不能算失败。"
                + "窗口控件树已存档：" + DumpWindow(work, round, dockAgain));
        }

        // ---- 8. 收尾：只关本次自己打开的资源管理器窗口 ----

        var explorerFacts = Win32.Describe(Uia.Handle(explorerWindow));
        if (explorerFacts is not null && explorerFacts.Title.Contains(leaf, StringComparison.Ordinal))
        {
            _ = Win32.RequestClose(explorerFacts.Handle);
            evidence.Add($"已请求关闭本次打开的资源管理器窗口（标题含「{leaf}」）。");
        }
        else
        {
            evidence.Add("资源管理器窗口标题已不含本次唯一文件夹名，未去动它。");
        }

        evidence.Add(relaunched.Stop("收尾"));

        return Conclude(
            CaseStatus.Passed,
            $"真实拖拽落盘：应用库新增 1 条（{entry.DisplayName}），已固定到程序坞；"
            + $"重启后新进程仍能列出该条，程序坞窗口形态与尺寸前后一致（磁贴 {before.Tiles} → "
            + $"{afterDropFacts.Tiles} → {afterReload.Tiles}）。");
    }

    /// <summary>
    /// 重启后用一个全新进程从磁盘读应用库。
    /// </summary>
    /// <remarks>
    /// 先停掉界面进程，免得单实例转发把这个进程直接打发回去、什么都不打印。
    /// 取两路证据：进程的标准输出（宿主是 WinExe，报告靠附着父控制台写出），
    /// 以及产品自己日志目录里的 <c>appservices.log</c>——两者任一提到该条目即算读到，
    /// 免得因为控制台附着的细节把一次真实的持久化判成失败。
    /// </remarks>
    private static string RunListLibrary(DriverContext context)
    {
        using var lister = ProcessLease.Launch(context.ProductPath, ["--list-library"], captureOutput: true);
        _ = lister.WaitForExit(60000);

        var text = string.Join(Environment.NewLine, lister.Output());
        var logFile = Path.Combine(ProductLibrary.DefaultRoot, "logs", "appservices.log");

        if (File.Exists(logFile))
        {
            try
            {
                text += Environment.NewLine + File.ReadAllText(logFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text += Environment.NewLine + $"（读不到 {logFile}：{ex.Message}）";
            }
        }

        return text;
    }

    private static AutomationElement? FindFileItem(AutomationElement window, string filePath)
    {
        var full = Path.GetFileName(filePath);
        var withoutExtension = Path.GetFileNameWithoutExtension(filePath);

        var items = Uia.Descendants(window, ControlType.ListItem)
            .Concat(Uia.Descendants(window, ControlType.DataItem))
            .Where(element => !Uia.IsOffscreen(element))
            .Where(element => !Uia.RectOf(element).IsEmpty)
            .ToList();

        return items.FirstOrDefault(element => Matches(Uia.Name(element)));

        bool Matches(string itemName) =>
            itemName.Equals(full, StringComparison.OrdinalIgnoreCase)
            || itemName.Equals(withoutExtension, StringComparison.OrdinalIgnoreCase)
            //「隐藏已知扩展名」时条目名就是去掉扩展名的那个，用前缀兜一下。
            || itemName.StartsWith(withoutExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 选一个落在程序坞窗口里、且不会被磁贴条截走的落点。
    /// </summary>
    /// <remarks>
    /// 磁贴条只处理「内部排序」那一种数据格式（见 <c>DockTileStrip.OnDragOver</c>），
    /// 文件列表到达时它不置 <c>Handled</c>，事件会冒泡到窗口的
    /// <c>OnExternalDrop</c>。所以落点只要在窗口内即可；优先选状态文字，
    /// 因为那一定是空白区域，落点最不容易被下一次布局变化影响。
    /// </remarks>
    private static (ScreenPoint Point, string How) DropPoint(AutomationElement dock)
    {
        // 首选拖入结果的状态文字：它一定在磁贴行下方，落点不会随下一次布局变化而改变，
        // 而且「固定 N 项」这句话本身就是稍后要核对的那条可观察结果。
        var status = Uia.Descendants(dock, ControlType.Text)
            .FirstOrDefault(element =>
                (Uia.Name(element).Contains('固') || Uia.Name(element).Contains('项'))
                && !Uia.RectOf(element).IsEmpty);

        if (status is not null)
        {
            var rect = Uia.RectOf(status);
            if (!rect.IsEmpty)
            {
                return (new ScreenPoint(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2)), "程序坞状态文字区（窗口内）");
            }
        }

        // 退路仍是窗口内：磁贴条只处理内部排序那一种格式，文件列表到达时它不置 Handled，
        // 事件会冒泡到窗口的 OnExternalDrop，所以窗口内任意一点都能作为落点。
        var window = Uia.RectOf(dock);
        if (!window.IsEmpty)
        {
            return (new ScreenPoint(window.Left + (window.Width / 2), window.Bottom - 8), "程序坞窗口下缘内侧");
        }

        return (new ScreenPoint(Win32.VirtualScreen().Left + 100, Win32.VirtualScreen().Bottom - 40), "虚拟桌面左下角（退路）");
    }

    private sealed record DockShape(WindowFacts Facts, int Tiles, int Texts, string Status);

    private static DockShape DescribeDock(AutomationElement dock)
    {
        var facts = Win32.Describe(Uia.Handle(dock))
            ?? new WindowFacts(0, "?", "?", 0, 0, default, false, 0);

        var images = Uia.Descendants(dock, ControlType.Image);
        var texts = Uia.Descendants(dock, ControlType.Text);

        return new DockShape(facts, images.Count, texts.Count, StatusTextOf(dock) ?? string.Empty);
    }

    private static string? StatusTextOf(AutomationElement dock)
    {
        var texts = Uia.Descendants(dock, ControlType.Text)
            .Select(Uia.Name)
            .Where(text => text.Contains('固') || text.Contains('项') || text.Contains("失败"))
            .ToList();

        return texts.Count == 0 ? null : string.Join(" / ", texts);
    }

    private static bool IsExplorerProcess(AutomationElement window)
    {
        var processId = 0;

        try
        {
            processId = window.Current.ProcessId;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>把资源管理器窗口里的条目列出来存档：定位失败时靠它核对控件身份。</summary>
    private static string DumpItems(string work, int round, AutomationElement window)
    {
        var path = Path.Combine(work, $"资源管理器条目-第{round}轮.txt");
        File.WriteAllText(path, Uia.Dump(window));

        var names = Uia.Descendants(window, ControlType.ListItem)
            .Select(element => "  · 「" + Uia.Name(element) + "」")
            .Take(20)
            .ToList();

        return path + Environment.NewLine + (names.Count == 0 ? "（一个条目控件都没有）" : string.Join(Environment.NewLine, names));
    }

    private static string DumpWindow(string work, int round, AutomationElement window)
    {
        var path = Path.Combine(work, $"程序坞控件树-第{round}轮.txt");
        File.WriteAllText(path, Uia.Dump(window));
        return path;
    }

    private static string TryShot(string work, int round, string label)
    {
        var path = Path.Combine(work, $"{label}-第{round}轮.png");
        return ScreenShot.TryCapture(path) ?? "（截图不可用）";
    }

    private static string DescribeWindows(int? processId)
    {
        var lines = Win32.TopLevelWindows()
            .Select(Win32.Describe)
            .Where(facts => facts is not null && (processId is null || facts.ProcessId == processId))
            .Select(facts => "  · " + facts!.Describe())
            .Take(40)
            .ToList();

        return lines.Count == 0 ? "  （没有可见顶层窗口）" : string.Join(Environment.NewLine, lines);
    }

    private static string Tail(string text)
    {
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "（没有输出）" : string.Join(Environment.NewLine, lines.TakeLast(15));
    }
}