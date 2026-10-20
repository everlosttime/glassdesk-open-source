using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassDesk.DesktopAcceptance;

/// <summary>用例结论。四个值都必须在报告里可能出现，缺一个就是把没跑过的当成跑过了。</summary>
internal enum CaseStatus
{
    /// <summary>预期与实际一致，且证据来自可观察的结果。</summary>
    Passed,

    /// <summary>跑到了，但产品行为与预期不符。</summary>
    Failed,

    /// <summary>环境不满足（锁屏、权限隔离、缺少产物、已有实例），本次没有验证到产品。</summary>
    Blocked,

    /// <summary>本次被要求运行但没有跑。</summary>
    NotRun,
}

/// <summary>
/// 一条用例记录。字段按批次 D 的统一结果格式定：用例名、需求编号、驱动方式、
/// 预期、实际、结论、证据、截图，以及产品／系统／显示环境。
/// </summary>
internal sealed record CaseRecord(
    string Name,
    string Requirement,
    string Driver,
    string Expected,
    string Actual,
    CaseStatus Status,
    IReadOnlyList<string> Evidence,
    string? Screenshot);

/// <summary>驱动运行时的环境快照。刻意不叫 Environment：那会和 System.Environment 打架。</summary>
internal sealed record EnvironmentSnapshot(
    string ProductVersion,
    string ProductPath,
    string OperatingSystem,
    string Session,
    string Display,
    string Driver);

/// <summary>按批次 D 的格式写报告：机器可读的 JSON 与可直接贴进交付报告的文本。</summary>
internal sealed class Report
{
    private readonly List<CaseRecord> _cases = [];
    private readonly List<string> _log = [];

    internal Report(string path) => Path = System.IO.Path.GetFullPath(path);

    internal string Path { get; }

    internal EnvironmentSnapshot? EnvironmentInfo { get; private set; }

    internal IReadOnlyList<CaseRecord> Cases => _cases;

    internal void SetEnvironment(EnvironmentSnapshot snapshot) => EnvironmentInfo = snapshot;

    internal void Plan(string name, string requirement, string expected)
    {
        if (_cases.Any(c => c.Name == name)) throw new InvalidOperationException("Duplicate case: " + name);
        _cases.Add(new CaseRecord(name, requirement, "尚未执行", expected,
            "本轮已登记，但尚未执行；门禁、异常或超时均不视为通过。", CaseStatus.NotRun, [], null));
    }

    internal void Note(string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        _log.Add(line);
        Console.WriteLine(line);
    }

    internal void Add(CaseRecord record)
    {
        var index = _cases.FindIndex(c => c.Name == record.Name && c.Status == CaseStatus.NotRun);
        if (index >= 0) _cases[index] = record;
        else _cases.Add(record);

        var mark = record.Status switch
        {
            CaseStatus.Passed => "PASS",
            CaseStatus.Failed => "FAIL",
            CaseStatus.Blocked => "BLOCK",
            _ => "NOTRUN",
        };

        Console.WriteLine($"{mark} {record.Name}｜{record.Requirement}｜{record.Actual}");

        foreach (var evidence in record.Evidence)
        {
            Console.WriteLine($"      证据：{evidence}");
        }

        if (record.Screenshot is not null)
        {
            Console.WriteLine($"      截图：{record.Screenshot}");
        }
    }

    /// <summary>
    /// 退出码：只要有一条失败就是 1；没有失败但有阻塞或未跑就是 2；
    /// 全部通过才是 0。跳过、缺应用、锁屏都得不到 0。
    /// </summary>
    internal int ExitCode()
    {
        if (_cases.Any(caseRecord => caseRecord.Status == CaseStatus.Failed))
        {
            return 1;
        }

        if (_cases.Count == 0)
        {
            return 2;
        }

        return _cases.All(caseRecord => caseRecord.Status == CaseStatus.Passed) ? 0 : 2;
    }

    internal void Write()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        var status = ExitCode() switch
        {
            0 => "all passed",
            1 => "failed",
            _ => "blocked or not run",
        };

        var document = new JsonObject
        {
            ["timestamp"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["exitCode"] = ExitCode(),
            ["status"] = status,
            ["driver"] = "真实桌面输入：UI Automation 定位控件 + SendInput 注入鼠标键盘",
            ["environment"] = EnvironmentInfo is null ? null : new JsonObject
            {
                ["productVersion"] = EnvironmentInfo.ProductVersion,
                ["productPath"] = EnvironmentInfo.ProductPath,
                ["os"] = EnvironmentInfo.OperatingSystem,
                ["session"] = EnvironmentInfo.Session,
                ["display"] = EnvironmentInfo.Display,
                ["driver"] = EnvironmentInfo.Driver,
            },
            ["totals"] = new JsonObject
            {
                ["passed"] = _cases.Count(c => c.Status == CaseStatus.Passed),
                ["failed"] = _cases.Count(c => c.Status == CaseStatus.Failed),
                ["blocked"] = _cases.Count(c => c.Status == CaseStatus.Blocked),
                ["notRun"] = _cases.Count(c => c.Status == CaseStatus.NotRun),
            },
            ["cases"] = new JsonArray([.. _cases.Select(ToJson)]),
            ["log"] = new JsonArray([.. _log.Select(line => JsonValue.Create(line))]),
        };

        File.WriteAllText(Path, document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,

            // 默认编码器会把中文转成 \uXXXX。证据是写给人看的，保持原样可读。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), Encoding.UTF8);

        var text = System.IO.Path.ChangeExtension(Path, ".log");
        var lines = new StringBuilder();
        lines.AppendLine($"GlassDesk 桌面验收：{status}（退出码 {ExitCode()}）");
        lines.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        // 表单里逐项复述环境，方便单条用例被单独引用时也自带上下文。
        if (EnvironmentInfo is not null)
        {
            lines.AppendLine($"产品：{EnvironmentInfo.ProductVersion}（{EnvironmentInfo.ProductPath}）");
            lines.AppendLine($"系统：{EnvironmentInfo.OperatingSystem}");
            lines.AppendLine($"会话：{EnvironmentInfo.Session}");
            lines.AppendLine($"显示：{EnvironmentInfo.Display}");
        }

        lines.AppendLine();

        foreach (var caseRecord in _cases)
        {
            lines.AppendLine($"{caseRecord.Status}｜{caseRecord.Name}");
            lines.AppendLine($"  需求：{caseRecord.Requirement}");
            lines.AppendLine($"  驱动：{caseRecord.Driver}");
            lines.AppendLine($"  预期：{caseRecord.Expected}");
            lines.AppendLine($"  实际：{caseRecord.Actual}");

            foreach (var evidence in caseRecord.Evidence)
            {
                lines.AppendLine($"  证据：{evidence}");
            }

            if (caseRecord.Screenshot is not null)
            {
                lines.AppendLine($"  截图：{caseRecord.Screenshot}");
            }

            lines.AppendLine();
        }

        lines.AppendLine("—— 运行日志 ——");
        foreach (var line in _log)
        {
            lines.AppendLine(line);
        }

        File.WriteAllText(text, lines.ToString(), Encoding.UTF8);
    }

    private JsonObject ToJson(CaseRecord record) => new()
    {
        ["case"] = record.Name,
        ["requirement"] = record.Requirement,
        ["driver"] = record.Driver,
        ["expected"] = record.Expected,
        ["actual"] = record.Actual,
        ["status"] = record.Status.ToString(),
        ["evidence"] = new JsonArray([.. record.Evidence.Select(item => JsonValue.Create(item))]),
        ["screenshot"] = record.Screenshot,
        ["environment"] = EnvironmentInfo is null ? null : new JsonObject
        {
            ["productVersion"] = EnvironmentInfo.ProductVersion,
            ["os"] = EnvironmentInfo.OperatingSystem,
            ["display"] = EnvironmentInfo.Display,
        },
    };

    /// <summary>本机与本次运行的环境快照，产品版本取自产物本身而不是源码常量。</summary>
    internal static EnvironmentSnapshot Snapshot(string? productPath, string driver)
    {
        var version = "（未找到产物）";

        if (productPath is not null && File.Exists(productPath))
        {
            var info = FileVersionInfo.GetVersionInfo(productPath);
            version = info.FileVersion ?? info.ProductVersion ?? "（读不到版本）";
        }

        var monitors = Win32.Monitors();
        var display = monitors.Count == 0
            ? "（枚举不到显示器）"
            : string.Join(
                "；",
                monitors.Select(monitor => $"{(monitor.Primary ? "主" : "副")} {monitor.Rect.Describe()} 工作区 {monitor.Work.Describe()}"))
              + $"；虚拟桌面 {Win32.VirtualScreen().Describe()}；本进程 DPI 感知 {Win32.DpiAwareness()}";

        var session =
            $"pid {Environment.ProcessId}；会话 {Process.GetCurrentProcess().SessionId}"
            + $"；活动控制台会话 {Win32.ActiveConsoleSessionId()}"
            + $"；输入桌面 {Win32.InputDesktopName() ?? "（读取失败）"}";

        return new EnvironmentSnapshot(
            version,
            productPath ?? "（未指定）",
            $"{Environment.OSVersion.VersionString}（{Environment.OSVersion.Version}）",
            session,
            display,
            driver);
    }
}
