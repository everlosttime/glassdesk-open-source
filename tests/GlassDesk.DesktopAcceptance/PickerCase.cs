using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 最小验证一：在<strong>原生</strong>文件选择器里选中一条带中文与空格的路径，
/// 核对产品收到的目标字符串与它逐字符相同。
/// </summary>
/// <remarks>
/// 上一轮卡住的地方不是产品，而是驱动：Computer Use 只能枚举无任务栏的宿主窗口，
/// 拿不到那个原生子对话框，于是把对话框的坐标套进了程序坞的小矩形里，
/// 点在窗口外面（<c>point (469, -379) is outside window bounds</c>）。
/// 这里的做法完全不同：<b>按身份找窗口</b>——进程 + 窗口类名 <c>#32770</c>，
/// 再按控件类型与自动化标识找编辑框和按钮，坐标只从控件的实际矩形里取。
///
/// 夹具是仓库里既有的 <c>GlassDesk.UiAcceptance --picker</c>：它驱动产品的
/// <c>RelocateFromDialog</c>（真实的 <c>IFileOpenDialog</c>），并在自己进程内
/// 断言「条目指向替换目标、图标恢复、点击能启动」。本驱动做两件它做不到的事：
/// 用真实鼠标键盘完成选择，以及<strong>另起一个视角</strong>读落盘的
/// <c>apps.json</c>，核对产品收到的字符串。
/// </remarks>
internal static class PickerCase
{
    internal const string CaseName = "原生文件选择器选中中文空格路径后条目重新生效";

    private const string Requirement = "R-07 路径失效可重新定位；D-08 点击启动";

    private const string Driver = "真实桌面输入：UIA 定位原生对话框 + SendInput 输入与点击";

    private const string Expected =
        "在原生文件选择器里选中带中文与空格的路径后，产品记录的目标字符串与该路径逐字符相同，"
        + "条目不再失效，图标恢复，点击能启动目标。";

    internal static CaseRecord Run(DriverContext context, int round, int rounds)
    {
        var name = $"{CaseName}（第 {round}/{rounds} 轮）";
        var evidence = new List<string>();
        string? screenshot = null;

        CaseRecord Conclude(CaseStatus status, string actual) =>
            new(name, Requirement, Driver, Expected, actual, status, [.. evidence], screenshot);

        var work = context.NewWorkDirectory($"原生选择器-第{round}轮");
        var fixtureReport = Path.Combine(work, "ui-acceptance.json");

        using var fixture = ProcessLease.Launch(
            context.FixturePath,
            ["--setup-only", "--picker", "--report", fixtureReport],
            workingDirectory: work,
            captureOutput: true);

        evidence.Add($"夹具命令：{fixture.CommandLine}");
        context.Report.Note($"最小验证一（第 {round} 轮）：夹具已启动 pid {fixture.ProcessId}");

        // ---- 1. 夹具的替换目标：它自己打印出来，驱动不做任何猜测 ----

        var replacement = fixture.WaitForOutputLine("PICKER_REPLACEMENT=", 90000);
        if (replacement is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                "夹具 90 秒内没有打印 PICKER_REPLACEMENT=…，说明它没有走到「打开文件选择器」这一步。"
                + "这是夹具没跑起来，不是选择器的问题。夹具输出末尾："
                + Environment.NewLine + Tail(fixture.Output()));
        }

        evidence.Add($"要选中的路径（含中文与空格）：{replacement}");

        if (!File.Exists(replacement))
        {
            return Conclude(CaseStatus.Failed, $"夹具声明的替换目标不存在：{replacement}");
        }

        // ---- 2. 生产程序坞窗口的属性：测试期间不得被改动 ----

        var dockElement = Uia.WaitForTopWindow(fixture.ProcessId, null, "程序坞", 30000);
        if (dockElement is null)
        {
            return Conclude(
                CaseStatus.Blocked,
                $"等不到夹具的程序坞窗口（pid {fixture.ProcessId}）。本进程看到的该进程顶层窗口："
                + Environment.NewLine + DescribeWindows(fixture.ProcessId));
        }

        var dockHandle = Uia.Handle(dockElement);
        var dockFacts = Win32.Describe(dockHandle);
        evidence.Add($"夹具的程序坞窗口：{dockFacts?.Describe() ?? "（读不到窗口属性）"}");

        if (dockFacts is null)
        {
            return Conclude(CaseStatus.Blocked, "读不到夹具程序坞窗口的属性，无法核对它有没有被测试夹具改过。");
        }

        if (!dockFacts.HasNoActivate || dockFacts.IsOnTaskbar)
        {
            return Conclude(
                CaseStatus.Failed,
                "夹具的程序坞窗口不符合生产形态（要求 WS_EX_NOACTIVATE 且不出现在任务栏）："
                + dockFacts.Describe());
        }

        // ---- 3. 原生文件选择器：按进程与窗口类找，不套任何坐标 ----

        var dialog = Uia.WaitForDialog(fixture.ProcessId, 45000);
        if (dialog is null)
        {
            screenshot ??= TryShot(work, round, "没等到选择器");
            return Conclude(
                CaseStatus.Failed,
                "产品的重新定位流程没有在 45 秒内创建原生文件选择器（窗口类 #32770）。"
                + "该进程当前的顶层窗口：" + Environment.NewLine + DescribeWindows(fixture.ProcessId));
        }

        var treeFile = Path.Combine(work, $"选择器控件树-第{round}轮.txt");
        File.WriteAllText(treeFile, Uia.Dump(dialog));
        evidence.Add($"选择器控件树（首次在目标机上跑时用它核对控件身份）：{treeFile}");

        screenshot = TryShot(work, round, "选择器已出现");

        // 轮到对话框的控件时也要等：窗口已经可见但内部还没排好版的那一小段里，
// 编辑框的矩形是空的，按「空矩形 = 找不到」会得出错误结论。
        var editReady = Uia.Until(() => PickFileNameEdit(dialog) is not null, 15000, 300);
        var edit = editReady ? PickFileNameEdit(dialog) : null;

        if (edit is null)
        {
            return Conclude(
                CaseStatus.Failed,
                "在选择器里找不到可用的文件名编辑框。控件树已存档：" + treeFile);
        }

        evidence.Add($"文件名编辑框：{Uia.Describe(edit)}");

        var editClick = Uia.Click(edit, "文件名编辑框");
        evidence.Add(editClick.Detail);

        var usedKeyboard = false;

        if (!editClick.Clicked)
        {
            // 通知横幅这类弹窗不会自己消失，重力点击在这里永远点不中。改用键盘焦点：UIA 的
            // SetFocus 不依赖那一点上有什么窗口，之后的输入仍然是真实击键。
            if (!Uia.TryFocus(edit))
            {
                return Conclude(CaseStatus.Blocked, "没能点中文件名编辑框，键盘焦点也没能落到它上面：" + editClick.Detail);
            }

            evidence.Add("点击被弹窗挡住，改用键盘焦点 + 真实击键（SetFocus 后 HasKeyboardFocus 为真）。");
            usedKeyboard = true;
        }

        NativeInput.SelectAll();
        Thread.Sleep(120);
        NativeInput.TypeText(replacement);
        Thread.Sleep(250);

        var typed = Uia.ValueOf(edit);
        evidence.Add($"键入后编辑框里的文本：{Quote(typed)}");

        if (typed is not null && !string.Equals(typed, replacement, StringComparison.Ordinal))
        {
            screenshot = TryShot(work, round, "键入后不一致");
            return Conclude(
                CaseStatus.Failed,
                $"编辑框里的文本与要选的路径不一致：编辑框 {Quote(typed)}（长度 {typed.Length}），"
                + $"期望 {Quote(replacement)}（长度 {replacement.Length}）。");
        }

        var openReady = Uia.Until(() => PickOpenButton(dialog) is not null, 10000, 300);
        var open = openReady ? PickOpenButton(dialog) : null;

        if (open is null)
        {
            return Conclude(CaseStatus.Failed, "在选择器里找不到「打开」按钮。控件树已存档：" + treeFile);
        }

        evidence.Add($"「打开」按钮：{Uia.Describe(open)}");

        var openClick = Uia.Click(open, "打开按钮");
        evidence.Add(openClick.Detail);

        if (openClick.Clicked)
        {
            // 鼠标路径成立。
        }
        else if (usedKeyboard)
        {
            // 已经走键盘路径：文件名框上的回车就是「打开」。
            evidence.Add("「打开」按钮同样被挡，改用文件名框上的回车提交（与点击「打开」等效）。");
            NativeInput.PressEnter();
        }
        else
        {
            return Conclude(CaseStatus.Blocked, "没能点中「打开」按钮：" + openClick.Detail);
        }

        var dialogClosed = Uia.Until(
            () => Uia.FindDialog(fixture.ProcessId) is null,
            20000);

        if (!dialogClosed)
        {
            screenshot = TryShot(work, round, "对话框没关闭");
            return Conclude(
                CaseStatus.Failed,
                "点了「打开」之后 20 秒内选择器没有关闭——选择没有被接受。截图：" + (screenshot ?? "（拍不到）"));
        }

        evidence.Add("点击「打开」后选择器已关闭（可观察结果）。");

        // ---- 4. 夹具自己的断言 + 独立读落盘数据核对「产品收到了什么」 ----

        if (!fixture.WaitForExit(120000))
        {
            screenshot ??= TryShot(work, round, "夹具未退出");
            return Conclude(
                CaseStatus.Failed,
                "选择器关闭后夹具 120 秒内没有退出。截图：" + (screenshot ?? "（拍不到）"));
        }

        var verdict = ReadFixtureVerdict(fixtureReport);
        evidence.Add($"夹具报告：{fixtureReport}");

        if (!verdict.Ok)
        {
            screenshot ??= TryShot(work, round, "夹具断言失败");
            return Conclude(CaseStatus.Failed, "夹具内的断言没有通过：" + verdict.Detail);
        }

        var dataDirectory = ReadFixtureDataDirectory(fixtureReport);
        if (dataDirectory is null)
        {
            return Conclude(CaseStatus.Failed, "读不出夹具报告里的数据目录，无法独立核对落盘结果。");
        }

        var entries = ProductLibrary.ReadEntries(dataDirectory);
        evidence.Add($"夹具的数据目录：{ProductLibrary.Describe(dataDirectory)}");

        var exact = entries.FirstOrDefault(entry => string.Equals(entry.Target, replacement, StringComparison.Ordinal));

        if (exact is null)
        {
            var nearMiss = entries.FirstOrDefault(entry =>
                entry.Target.Contains(Path.GetFileName(replacement), StringComparison.OrdinalIgnoreCase));

            return Conclude(
                CaseStatus.Failed,
                "产品落盘的目标字符串与要选的路径不逐字符相同。"
                + $"期望 {Quote(replacement)}；库里最接近的一条是 {Quote(nearMiss?.Target)}"
                + $"（库中 {entries.Count} 条）:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, entries.Select(entry => "  · " + Quote(entry.Target))));
        }

        if (exact.IsTargetMissing)
        {
            return Conclude(CaseStatus.Failed, "重新定位后条目仍然被标记为失效：" + Quote(exact.Target));
        }

        return Conclude(
            CaseStatus.Passed,
            $"原生选择器接受了键入的路径，产品落盘的目标与它逐字符相同（{Quote(exact.Target)}），"
            + "条目不再失效，夹具内「图标恢复 + 点击启动」的断言同时通过。");
    }

    /// <summary>
    /// 挑出文件名编辑框。
    /// </summary>
    /// <remarks>
    /// 按证据强弱排序，而不是按位置猜：Vista 之后的文件对话框里，文件名组合框内的
    /// 编辑框自动化标识是 <c>1148</c>；经典对话框是 <c>1001</c>；
    /// 再退一步才看名字里有没有「文件名 / File name」。
    /// 三条都不中时取最靠下的一个可用编辑框：文件对话框里文件名框本来就在底部。
    /// 真实的控件树在第一次运行时会存档，选错了可以直接对着它改。
    /// </remarks>
    private static AutomationElement? PickFileNameEdit(AutomationElement dialog)
    {
        var candidates = Uia.Descendants(dialog, ControlType.Edit)
            .Where(Uia.IsEnabled)
            .Where(element => !Uia.IsOffscreen(element))
            .Where(element => !Uia.RectOf(element).IsEmpty)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(Score)
            .ThenByDescending(element => Uia.RectOf(element).Top)
            .First();

        int Score(AutomationElement element)
        {
            var id = Uia.AutomationId(element);

            if (id == "1148")
            {
                return 100;
            }

            if (id == "1001")
            {
                return 90;
            }

            if (HasFileLabel(Uia.Name(element)))
            {
                return 80;
            }

            var parent = Uia.Parent(element);
            if (parent is not null && HasFileLabel(Uia.Name(parent)))
            {
                return 70;
            }

            return 10;
        }

        static bool HasFileLabel(string text) =>
            text.Contains("文件名", StringComparison.OrdinalIgnoreCase)
            || text.Contains("File name", StringComparison.OrdinalIgnoreCase);
    }

    private static AutomationElement? PickOpenButton(AutomationElement dialog)
    {
        var buttons = Uia.Descendants(dialog, ControlType.Button)
            .Where(Uia.IsEnabled)
            .Where(element => !Uia.IsOffscreen(element))
            .ToList();

        return buttons.FirstOrDefault(element => Uia.AutomationId(element) == "1")
            ?? buttons.FirstOrDefault(element =>
                Uia.Name(element).StartsWith("打开", StringComparison.OrdinalIgnoreCase)
                || Uia.Name(element).StartsWith("Open", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读夹具报告里那条 picker 用例的结论。</summary>
    private static (bool Ok, string Detail) ReadFixtureVerdict(string path)
    {
        if (!File.Exists(path))
        {
            return (false, "夹具没有写出报告文件：" + path);
        }

        var document = JsonNode.Parse(File.ReadAllText(path));

        if (document?["checks"] is not JsonArray checks)
        {
            return (false, "夹具报告是超时形态（没有逐项结果）：" + (document?["detail"]?.GetValue<string>() ?? "无说明"));
        }

        var picker = checks.FirstOrDefault(node =>
            (node?["name"]?.GetValue<string>() ?? string.Empty).StartsWith("native picker", StringComparison.OrdinalIgnoreCase));

        if (picker is null)
        {
            return (false, "夹具报告里没有 picker 用例——它没有跑到那一步。");
        }

        var pass = picker["pass"]?.GetValue<bool>() ?? false;
        var detail = picker["detail"]?.GetValue<string>() ?? string.Empty;

        return (pass, pass ? "夹具内断言通过" : detail);
    }

    private static string? ReadFixtureDataDirectory(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonNode.Parse(File.ReadAllText(path));
        var root = document?["dataDirectory"]?.GetValue<string>();

        return root is null ? null : Path.Combine(root, "data");
    }

    private static string TryShot(string work, int round, string label)
    {
        var path = Path.Combine(work, $"{label}-第{round}轮.png");
        return ScreenShot.TryCapture(path) ?? "（截图不可用）";
    }

    private static string DescribeWindows(int processId)
    {
        var lines = Win32.TopLevelWindows()
            .Select(Win32.Describe)
            .Where(facts => facts is not null && facts.ProcessId == processId)
            .Select(facts => "  · " + facts!.Describe())
            .ToList();

        return lines.Count == 0 ? "  （没有该进程的可见顶层窗口）" : string.Join(Environment.NewLine, lines);
    }

    private static string Quote(string? text) => text is null ? "（读不到）" : $"「{text}」";

    private static string Tail(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "（没有输出）" : string.Join(Environment.NewLine, lines.TakeLast(12));
}