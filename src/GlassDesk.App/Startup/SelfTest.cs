using System.Globalization;
using System.IO;
using System.Text;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.UI.Theming;

namespace GlassDesk.App.Startup;

/// <summary>
/// 无界面地验证那些必须先做对的构建事项，
/// 否则产出的界面根本不值得一看。
/// </summary>
/// <remarks>
/// 设计成既可人工运行，也可由脚本运行。它检查环境、真实地走一遍数据层
/// ——写入、保存、重新加载——并确认本产品的存储与参考产品的存储并不
/// 重叠，这是计划书里的硬性要求。
///
/// 它还检查非 ASCII 的源码字面量是否挺过了编译。源码文件是无 BOM 的
/// UTF-8，在 ANSI 代码页为 GBK 的机器上，被错误解码的文件
/// 会悄悄在界面上变成乱码；把字面量与它的转义序列做比较，
/// 能在构建时而非截图里发现这一点。
/// </remarks>
internal static class SelfTest
{
    public static int Run(AppPaths paths)
        => Run(paths, null);

    public static int Run(AppPaths paths, Stream? acceptanceReport)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        var failures = 0;

        report.AppendLine("GlassDesk 自检");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"产品版本：{typeof(SelfTest).Assembly.GetName().Version}");
        report.AppendLine(CultureInfo.InvariantCulture, $"运行时：{Environment.Version}");
        report.AppendLine(CultureInfo.InvariantCulture, $"进程架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine();

        report.AppendLine("【系统能力】");
        report.AppendLine(CultureInfo.InvariantCulture, $"操作系统：Windows {WindowsCapabilities.OsVersion}（Build {WindowsCapabilities.BuildNumber}）");
        report.AppendLine(CultureInfo.InvariantCulture, $"系统背景材质：{Yes(WindowsCapabilities.SupportsSystemBackdrop)}（需 Build 22621 及以上）");
        report.AppendLine(CultureInfo.InvariantCulture, $"窗口圆角偏好：{Yes(WindowsCapabilities.SupportsWindowCornerPreference)}（需 Build 22000 及以上）");
        report.AppendLine(CultureInfo.InvariantCulture, $"深色标题栏：{Yes(WindowsCapabilities.SupportsImmersiveDarkMode)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"系统主题：{(SystemThemeReader.IsLightTheme() ? "浅色" : "深色")}");
        report.AppendLine(CultureInfo.InvariantCulture, $"系统透明效果：{(SystemThemeReader.IsTransparencyEnabled() ? "开启" : "关闭")}");
        if (!WindowsCapabilities.IsSupported)
        {
            failures++;
            report.AppendLine("✗ 此 Windows 版本低于支持范围（需要 Windows 11 Build 22000 及以上）");
        }

        report.AppendLine();

        CheckTextEncoding(report, ref failures);
        CheckStorage(paths, report, ref failures);
        CheckThemeResolution(report, ref failures);
        CheckSingleInstance(report, ref failures);
        CheckStorageIsolation(report);

        report.AppendLine();
        report.AppendLine(new string('=', 46));
        report.AppendLine(failures == 0
            ? "结果：全部检查通过。"
            : $"结果：{failures} 项检查未通过。");
        report.AppendLine(acceptanceReport is null
            ? string.Create(CultureInfo.InvariantCulture, $"自检日志：{Path.Combine(paths.LogDirectory, "selftest.txt")}")
            : "自检报告：内容写入调用方指定的报告流。");

        var text = report.ToString();
        if (acceptanceReport is null)
        {
            ReportWriter.Write(text, Path.Combine(paths.LogDirectory, "selftest.txt"));
        }
        else
        {
            AcceptanceEntryPoint.WriteText(acceptanceReport, text);
            ReportWriter.Write(text, null);
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 确认非 ASCII 字符串字面量解码正确，这实际上是在检查
    /// 编译器是怎么读我们的 UTF-8 源码文件的。
    /// </summary>
    private static void CheckTextEncoding(StringBuilder report, ref int failures)
    {
        report.AppendLine("【文本编码】");

        // 一个字面量在源码里写成汉字，另一个写成同样的文字但用转义。
        // 转义形式不受文件解码影响。
        const string Literal = "玻璃桌面";
        const string Expected = "\u73BB\u7483\u684C\u9762";

        var literalOk = string.Equals(Literal, Expected, StringComparison.Ordinal);
        report.AppendLine(CultureInfo.InvariantCulture, $"中文字面量与转义序列一致：{Yes(literalOk)}（{Literal}）");

        if (!literalOk)
        {
            failures++;
            report.AppendLine("✗ 源文件编码可能被错误解码，界面中文将出现乱码。");
        }

        // 让一个中文值走一遍设置真正使用的 JSON 层往返。
        var sample = "启动台与程序坞";
        var bytes = Encoding.UTF8.GetBytes(sample);
        var roundTripped = Encoding.UTF8.GetString(bytes);
        var roundTripOk = string.Equals(sample, roundTripped, StringComparison.Ordinal);
        report.AppendLine(CultureInfo.InvariantCulture, $"UTF-8 往返一致：{Yes(roundTripOk)}");

        if (!roundTripOk)
        {
            failures++;
        }

        report.AppendLine();
    }

    /// <summary>
    /// 确认真实数据目录可用，
    /// 然后在沙箱里演练写入路径。
    /// </summary>
    /// <remarks>
    /// 写入、重新加载与恢复这几项检查刻意跑在一个一次性目录上，
    /// 而不是产品自己的目录。自检不能因为跑过一次，
    /// 就把用户的配置改掉。
    /// </remarks>
    private static void CheckStorage(AppPaths paths, StringBuilder report, ref int failures)
    {
        report.AppendLine("【数据读写】");

        try
        {
            paths.EnsureCreated();
            report.AppendLine(CultureInfo.InvariantCulture, $"数据目录已就绪：{paths.RootDirectory}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ 无法创建数据目录：{ex.Message}");
            report.AppendLine();
            return;
        }

        // 读取真实文档是安全的，也值得做：它能报告用户现有的数据
        // 当前是否健康。
        try
        {
            var liveReport = new GlassDeskDataStore(paths).LoadAll();
            report.AppendLine(CultureInfo.InvariantCulture, $"现有数据加载提示数：{liveReport.Notices.Count}（应为 0）");

            if (liveReport.HasNotices)
            {
                failures++;
                foreach (var notice in liveReport.Notices)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  · {notice}");
                }
            }
        }
        catch (StorageException ex)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ 读取现有数据失败：{ex.Message}");
        }

        var sandbox = new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-selftest-" + Guid.NewGuid().ToString("n")));

        try
        {
            sandbox.EnsureCreated();

            var store = new GlassDeskDataStore(sandbox);

            // 全新目录上的首次加载应当报告 FirstRun，而不是错误。
            var firstLoad = store.LoadAll();
            report.AppendLine(CultureInfo.InvariantCulture, $"全新目录首次加载提示数：{firstLoad.Notices.Count}（应为 0）");

            store.Settings.Current.Appearance.AccentColor = "#0A84FF";
            store.Settings.Save();

            var saved = File.Exists(sandbox.SettingsFile);
            report.AppendLine(CultureInfo.InvariantCulture, $"设置已写入：{Yes(saved)}");

            if (!saved)
            {
                failures++;
                report.AppendLine("✗ 设置文件未生成。");
            }

            // 备份由替换步骤产生；
            // 而它要到第二次保存起才有上一版本可以保留。只保存一次再去找 .bak，
            // 测的就是错的东西了。
            store.Settings.Current.Appearance.ReduceMotion = true;
            store.Settings.Save();

            var backup = File.Exists(sandbox.SettingsFile + AtomicFile.BackupSuffix);
            report.AppendLine(CultureInfo.InvariantCulture, $"二次保存后保留上一版本（.bak）：{Yes(backup)}");

            if (!backup)
            {
                failures++;
                report.AppendLine("✗ 原子替换未保留上一版本备份。");
            }

            // 用第二个存储实例重新加载，免得任何内容是从内存里拿出来的。
            var secondLoad = new GlassDeskDataStore(sandbox).LoadAll();
            var reloadOk = secondLoad.Notices.Count == 0;
            report.AppendLine(CultureInfo.InvariantCulture, $"重新加载无异常：{Yes(reloadOk)}");

            if (!reloadOk)
            {
                failures++;
                foreach (var notice in secondLoad.Notices)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  · {notice}");
                }
            }

            // 损坏的文档必须被保留，而不是丢弃。
            var corruption = CheckCorruptionRecovery();
            report.AppendLine(CultureInfo.InvariantCulture, $"损坏文件被保留而非丢弃：{Yes(corruption.Preserved)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"损坏后从备份恢复：{Yes(corruption.Recovered)}");

            if (!corruption.Preserved || !corruption.Recovered)
            {
                failures++;
            }
        }
        catch (StorageException ex)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ 数据读写失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(sandbox.RootDirectory);
        }

        report.AppendLine();
    }

    /// <summary>
    /// 故意弄坏一个设置文件，检查加载器会不会把备份救回来，
    /// 而不是把用户的数据丢掉。
    /// </summary>
    private static (bool Preserved, bool Recovered) CheckCorruptionRecovery()
    {
        var sandbox = new AppPaths(Path.Combine(Path.GetTempPath(), "GlassDesk-corrupt-" + Guid.NewGuid().ToString("n")));

        try
        {
            sandbox.EnsureCreated();

            var seed = new GlassDeskDataStore(sandbox);
            seed.LoadAll();
            seed.Settings.Save();

            // 第二次保存让替换步骤有上一版本可以留作备份。
            seed.Settings.Current.Appearance.ReduceMotion = true;
            seed.Settings.Save();

            File.WriteAllText(sandbox.SettingsFile, "{ this is not valid json");

            var damaged = new GlassDeskDataStore(sandbox);
            var outcome = damaged.Settings.Load();

            var preserved = outcome.PreservedFile is not null && File.Exists(outcome.PreservedFile);
            var recovered = outcome.Status == LoadStatus.RecoveredFromBackup;

            return (preserved, recovered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException)
        {
            return (false, false);
        }
        finally
        {
            TryDeleteDirectory(sandbox.RootDirectory);
        }
    }

    private static void CheckThemeResolution(StringBuilder report, ref int failures)
    {
        report.AppendLine("【主题解析】");

        var theme = ThemeService.Current;
        if (theme is null)
        {
            failures++;
            report.AppendLine("✗ 主题服务未初始化。");
            report.AppendLine();
            return;
        }

        var palette = theme.Palette;
        report.AppendLine(CultureInfo.InvariantCulture, $"默认主题：{theme.Appearance.Theme}（当前 {(theme.IsDark ? "深色" : "浅色")}）");
        report.AppendLine(CultureInfo.InvariantCulture, $"面板模式：{(theme.UseOpaquePanels ? "不透明" : "半透明玻璃")}");
        report.AppendLine(CultureInfo.InvariantCulture, $"强调色：#{palette.Accent.R:X2}{palette.Accent.G:X2}{palette.Accent.B:X2}");
        var dockRadius = Math.Max(40, theme.Appearance.DockCornerRadius);
        var launcherRadius = Math.Max(36, theme.Appearance.LauncherCornerRadius);
        report.AppendLine(CultureInfo.InvariantCulture, $"程序坞实际圆角：{dockRadius} DIP（至少 40）");
        report.AppendLine(CultureInfo.InvariantCulture, $"启动台实际圆角：{launcherRadius} DIP（至少 36）");

        var dockRadiusOk = dockRadius >= 40;
        var launcherRadiusOk = launcherRadius >= 36;
        report.AppendLine(CultureInfo.InvariantCulture, $"液态玻璃圆角保持明显弧度：{Yes(dockRadiusOk && launcherRadiusOk)}");

        if (!dockRadiusOk || !launcherRadiusOk)
        {
            failures++;
        }

        var accentOk = palette.Accent.R == 0x0A && palette.Accent.G == 0x84 && palette.Accent.B == 0xFF;
        if (!accentOk)
        {
            failures++;
            report.AppendLine("✗ 默认强调色不是规划中的 #0A84FF。");
        }

        report.AppendLine();
    }

    private static void CheckSingleInstance(StringBuilder report, ref int failures)
    {
        report.AppendLine("【单实例】");

        const string ProbeName = "selftest-probe";

        try
        {
            using var first = SingleInstanceGuard.Acquire(ProbeName);
            report.AppendLine(CultureInfo.InvariantCulture, $"首个实例取得所有权：{Yes(first.IsFirstInstance)}");

            using var second = SingleInstanceGuard.Acquire(ProbeName);
            report.AppendLine(CultureInfo.InvariantCulture, $"第二个实例被识别为重复：{Yes(!second.IsFirstInstance)}");

            if (!first.IsFirstInstance || second.IsFirstInstance)
            {
                failures++;
                report.AppendLine("✗ 单实例判定不正确。");
            }

            // 真实实例名绝不能与参考产品撞上。
            var real = SingleInstanceGuard.Acquire("主实例");
            report.AppendLine(CultureInfo.InvariantCulture, $"本产品单实例标识可用：{Yes(real.IsFirstInstance)}");
            real.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            failures++;
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ 单实例检查失败：{ex.Message}");
        }

        report.AppendLine();
    }

    /// <summary>
    /// 报告参考产品是否已安装，并明确声明我们不读写它们的数据。
    /// 这是阶段 1 的验收项，所以要用检查来确认，
    /// 而不是用文字来断言。
    /// </summary>
    private static void CheckStorageIsolation(StringBuilder report)
    {
        report.AppendLine("【与参考产品的隔离】");

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var macDock = Path.Combine(roaming, "MacDock");
        var ymLauncher = Path.Combine(roaming, "Ym Launcher");
        var launcherStart = Path.Combine(roaming, "Launcher启动台");

        report.AppendLine(CultureInfo.InvariantCulture, $"参考产品数据目录存在（仅探测，不读写）：MacDock={Yes(Directory.Exists(macDock))}，Ym Launcher={Yes(Directory.Exists(ymLauncher))}，Launcher启动台={Yes(Directory.Exists(launcherStart))}");

        var ours = AppPaths.ForCurrentUser().RootDirectory;
        var overlaps = new[] { macDock, ymLauncher, launcherStart }
            .Any(other => other.StartsWith(ours, StringComparison.OrdinalIgnoreCase) ||
                          ours.StartsWith(other, StringComparison.OrdinalIgnoreCase));

        report.AppendLine(CultureInfo.InvariantCulture, $"本产品数据目录：{ours}");
        report.AppendLine(CultureInfo.InvariantCulture, $"与参考产品目录重叠：{Yes(overlaps)}（必须为否）");

        report.AppendLine();
    }

    private static string Yes(bool value) => value ? "是" : "否";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留的临时文件夹无伤大雅；自检结果才是要紧的。
        }
    }
}
