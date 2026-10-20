using System.IO;
using System.Text.Json;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;

namespace GlassDesk.App.Startup;

/// <summary>以独立数据目录运行阶段六纯规则与持久化验收，不创建产品窗口。</summary>
internal static class Phase6Acceptance
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Keep internal callers on the same guarded path as the CLI.
    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2) return 2;
        return AcceptanceEntryPoint.Run(realPaths, arguments[1], [new("report", arguments[0])],
            workspace => Run(realPaths, arguments, workspace));
    }

    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, GlassDesk.Platform.Storage.AcceptanceWorkspace workspace)
    {
        var reportPath = arguments.Count > 0 && !string.IsNullOrWhiteSpace(arguments[0])
            ? Path.GetFullPath(arguments[0])
            : Path.Combine(Environment.CurrentDirectory, "docs", "evidence", "阶段6", "phase6-cli.json");
        var root = workspace.DataRoot;
        var cases = new List<object>();
        var failed = 0;

        void Check(string name, bool okay, string expected, string actual)
        {
            if (!okay) failed++;
            cases.Add(new { name, driver = "settings rules and isolated files", expected, actual,
                status = okay ? "Passed" : "Failed", timestamp = DateTimeOffset.Now });
        }

        if (DataDirectoryGuard.Overlaps(root, realPaths.RootDirectory))
        {
            Check("数据目录重叠保护", false, "拒绝与当前用户数据目录相同或互为父子目录", "候选目录重叠，未读取或创建候选目录");
            Write(workspace.GetOutput("report"), reportPath, root, cases, 0, failed, 2);
            return 2;
        }

        var comparisons = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Check("真实用户目录自身受保护", DataDirectoryGuard.Overlaps(realPaths.RootDirectory, realPaths.RootDirectory),
            "返回重叠", "使用DataDirectoryGuard.Overlaps");
        Check("真实用户目录的子目录受保护", DataDirectoryGuard.Overlaps(Path.Combine(realPaths.RootDirectory, "phase6-probe"), realPaths.RootDirectory),
            "返回重叠", "使用DataDirectoryGuard.Overlaps");
        var sibling = Path.Combine(Path.GetDirectoryName(realPaths.RootDirectory)!, "GlassDesk-Phase6-Sibling");
        Check("同级目录不误判为重叠", !DataDirectoryGuard.Overlaps(sibling, realPaths.RootDirectory),
            "返回不重叠", $"真实目录={realPaths.RootDirectory}; 候选={sibling}; comparison={comparisons}");

        var paths = new AppPaths(root);
        try
        {
            paths.EnsureCreated();
            var data = new GlassDeskDataStore(paths);
            data.LoadAll();
            var changed = SettingsPageEditor.Clone(data.Settings.Current);
            changed.Launcher.Rows = 4;
            changed.Shortcuts.HotCornerDwellMs = 777;
            SettingsPageEditor.Apply(data.Settings.Current, changed);
            data.Settings.Save();
            var reloaded = new GlassDeskDataStore(paths);
            reloaded.LoadAll();
            Check("新增设置保存后可由新实例重载", reloaded.Settings.Current.Launcher.Rows == 4
                && reloaded.Settings.Current.Shortcuts.HotCornerDwellMs == 777,
                "Launcher.Rows=4且停留时间=777", $"Launcher.Rows={reloaded.Settings.Current.Launcher.Rows}; dwell={reloaded.Settings.Current.Shortcuts.HotCornerDwellMs}");

            var beforeReset = SettingsPageEditor.Clone(reloaded.Settings.Current);
            var resetShortcuts = SettingsPageEditor.ResetPage(beforeReset, "快捷操作");
            Check("单页恢复默认保留启动台设置", resetShortcuts.Launcher.Rows == beforeReset.Launcher.Rows
                && resetShortcuts.Shortcuts.HotCornerDwellMs == new ShortcutSettings().HotCornerDwellMs,
                $"启动台行数保留{beforeReset.Launcher.Rows}且快捷操作回默认", $"行数={resetShortcuts.Launcher.Rows}; dwell={resetShortcuts.Shortcuts.HotCornerDwellMs}");

            resetShortcuts.Shortcuts.HotCornerDwellMs = 99;
            Check("范围规则拒绝低于边界的停留时间", SettingsPageEditor.Validate(resetShortcuts) is not null,
                "验证返回错误", SettingsPageEditor.Validate(resetShortcuts) ?? "未返回错误");
            resetShortcuts.Shortcuts.HotCornerDwellMs = 3001;
            Check("范围规则拒绝高于边界的停留时间", SettingsPageEditor.Validate(resetShortcuts) is not null,
                "验证返回错误", SettingsPageEditor.Validate(resetShortcuts) ?? "未返回错误");

            var invalid = SettingsPageEditor.Clone(reloaded.Settings.Current);
            invalid.Shortcuts.ToggleLauncher = new HotkeyDefinition { Enabled = true, Control = true, Key = "NOT_A_KEY" };
            Check("快捷键键名规则拒绝非法值", SettingsPageEditor.Validate(invalid) is not null,
                "验证返回键名错误", SettingsPageEditor.Validate(invalid) ?? "未返回错误");

            var duplicate = SettingsPageEditor.Clone(reloaded.Settings.Current);
            duplicate.Shortcuts.ToggleLauncher = new HotkeyDefinition { Enabled = true, Control = true, Key = "F13" };
            duplicate.Shortcuts.HideLauncher = new HotkeyDefinition { Enabled = true, Control = true, Key = "F13" };
            Check("快捷键规则拒绝同组合重复绑定", SettingsPageEditor.Validate(duplicate)?.Contains("同一个", StringComparison.Ordinal) == true,
                "返回组合重复错误", SettingsPageEditor.Validate(duplicate) ?? "未返回错误");

            var bytesBeforeInvalidSave = File.ReadAllBytes(paths.SettingsFile);
            try
            {
                data.Settings.Current.Shortcuts.HotCornerDwellMs = 99;
                var invalidCurrent = SettingsPageEditor.Validate(data.Settings.Current);
                if (invalidCurrent is null) data.Settings.Save();
                Check("无效设置不提交且磁盘内容保持", invalidCurrent is not null
                    && bytesBeforeInvalidSave.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                    "未执行Save且文件字节不变", invalidCurrent ?? "设置被接受");
            }
            finally
            {
                data.Settings.Current.Shortcuts.HotCornerDwellMs = 777;
            }
        }
        catch (Exception ex)
        {
            Check("纯文件与规则验收未发生未处理异常", false, "无异常", ex.ToString());
        }

        Write(workspace.GetOutput("report"), reportPath, root, cases, cases.Count - failed, failed, failed == 0 && cases.Count > 0 ? 0 : 1);
        Console.WriteLine($"Phase 6 CLI: {cases.Count - failed} passed, {failed} failed. Report: {reportPath}");
        return cases.Count > 0 && failed == 0 ? 0 : 1;
    }

    private static void Write(Stream output, string reportPath, string root, List<object> cases, int passed, int failed, int exitCode)
    {
        AcceptanceEntryPoint.WriteText(output, JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            artifact = typeof(Phase6Acceptance).Assembly.FullName,
            environment = Environment.OSVersion.ToString(),
            machine = Environment.MachineName,
            dataDirectory = root,
            driver = "isolated settings files and validation rules; no product windows or system input",
            passed,
            failed,
            exitCode,
            cases,
        }, JsonOptions));
    }
}
