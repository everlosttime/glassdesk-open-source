using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Imaging;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>
/// 阶段 3 验收标准的可执行形式。
/// </summary>
/// <remarks>
/// 起飞说明第五节列出的每一条要求，在这里都变成一段会真跑的检查：
/// 四类条目 + 网页各导入一个、中文与空格路径、参数保留、失效路径保留并可重新定位、
/// 导入逐条报告且不破坏既有数据、500 条时仍然正常。
///
/// <para>
/// 与 <see cref="SelfTest"/> 同样的理由：把「已验证」这句话交给一条能重跑的命令，
/// 而不是交给一次人工操作之后的记忆。它<strong>只动</strong>
/// <c>apps.json</c> 与图标缓存目录——设置与布局原样不动，
/// 免得把一个验收流程变成会毁掉用户配置的流程。
/// </para>
/// </remarks>
internal static class Phase3Acceptance
{
    /// <summary>
    /// 跑一遍全部验收检查。
    /// </summary>
    /// <param name="realPaths">用户真正的数据目录。**不会被改动**，只用来在报告里作对照。</param>
    /// <param name="arguments">可选：第一个参数是联系表的输出路径。</param>
    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, GlassDesk.Platform.Storage.AcceptanceWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(realPaths);
        ArgumentNullException.ThrowIfNull(arguments);

        var report = new StringBuilder();
        var checks = new List<(string Label, CheckStatus Status, string Detail)>();

        var paths = new AppPaths(workspace.DataRoot);
        paths.EnsureCreated();

        report.AppendLine("GlassDesk 阶段 3 验收");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"验收数据目录：{paths.RootDirectory}（隔离的临时目录，跑完保留供核对）");
        report.AppendLine(CultureInfo.InvariantCulture, $"用户真正的数据目录：{realPaths.RootDirectory}——全程未被改动");
        report.AppendLine();

        var set = SampleDataBuilder.Build();
        report.AppendLine(SampleDataBuilder.Describe(set));

        var data = new GlassDeskDataStore(paths);
        data.LoadAll();

        using var host = new AppServiceHost(data);

        Section(report, "一、导入应当被接受的来源");
        var result = ImportAll(host, set, report);
        Check(checks, "全部被接受的来源都新增成功", result.Added.Count == set.ShouldImport.Count,
            $"期望 {set.ShouldImport.Count} 条，实际 {result.Added.Count} 条");
        Check(checks, "被接受的来源没有一条失败", result.Failures.Count == 0,
            $"失败 {result.Failures.Count} 条");

        Section(report, "二、应当被拒绝的来源逐条报告失败");
        var rejected = ImportRejected(host, set, report);
        Check(checks, "被拒绝的来源没有一条进入应用库", rejected.Added.Count == 0,
            $"新增 {rejected.Added.Count} 条");
        Check(checks, "每一个坏来源都被逐条报告",
            rejected.Failures.Count == set.ShouldFail.Count,
            $"期望 {set.ShouldFail.Count} 条失败，实际 {rejected.Failures.Count} 条（{DescribeFailures(rejected)}）");
        Check(checks, "不存在的路径报为 NotFound",
            rejected.Failures.Any(failure => failure.Reason == ImportFailureReason.NotFound),
            DescribeFailures(rejected));
        Check(checks, "读不出目标的快捷方式报为 ShortcutUnreadable",
            rejected.Failures.Any(failure => failure.Reason == ImportFailureReason.ShortcutUnreadable),
            DescribeFailures(rejected));

        Section(report, "三、四类条目与网页都在库里");
        var byKey = MapByKey(host, set);
        Check(checks, "程序条目存在",
            byKey.ContainsKey("program"), $"对上 {byKey.Count} 个样本：{DescribeKeys(byKey)}");
        Check(checks, "文件夹条目存在",
            byKey.ContainsKey("folder"), $"对上 {byKey.Count} 个样本：{DescribeKeys(byKey)}");
        Check(checks, "文件条目存在",
            byKey.ContainsKey("file"), $"对上 {byKey.Count} 个样本：{DescribeKeys(byKey)}");
        Check(checks, "快捷方式（带参数）条目存在",
            byKey.ContainsKey("shortcut-arguments"), $"对上 {byKey.Count} 个样本：{DescribeKeys(byKey)}");
        Check(checks, "网页条目存在（.url 文件）",
            byKey.ContainsKey("url-file"), $"对上 {byKey.Count} 个样本：{DescribeKeys(byKey)}");
        Check(checks, "网页条目存在（指向网址的快捷方式）",
            byKey.ContainsKey("shortcut-url"), $"对上 {byKey.Count} 个样本：{DescribeKeys(byKey)}");
        Check(checks, "只差端口的两个网址被当成两条记录",
            byKey.TryGetValue("url-port-8001", out var portA)
            && byKey.TryGetValue("url-port-8002", out var portB)
            && !string.Equals(portA.Target, portB.Target, StringComparison.Ordinal),
            $"8001 → {byKey.GetValueOrDefault("url-port-8001")?.Target ?? "（缺失）"}；"
            + $"8002 → {byKey.GetValueOrDefault("url-port-8002")?.Target ?? "（缺失）"}");
        Check(checks, "后缀是 .url 但内容不是网页的文件退回按普通文件处理",
            byKey.TryGetValue("bogus-url-file", out var bogus) && bogus.Kind == AppItemKind.File,
            byKey.TryGetValue("bogus-url-file", out var shownBogus) ? $"实际 [{shownBogus.Kind}] {shownBogus.Target}" : "条目缺失");

        Check(checks, "带参数的快捷方式保留了参数",
            byKey.TryGetValue("shortcut-arguments", out var withArgs)
            && withArgs.Arguments == "--flag \"带 空格 的值\" --second",
            byKey.TryGetValue("shortcut-arguments", out var shown) ? $"实际「{shown.Arguments}」" : "条目缺失");

        Check(checks, "带参数的快捷方式保留了工作目录",
            byKey.TryGetValue("shortcut-arguments", out var withDir) && !string.IsNullOrEmpty(withDir.WorkingDirectory),
            byKey.TryGetValue("shortcut-arguments", out var shownDir) ? $"实际「{shownDir.WorkingDirectory}」" : "条目缺失");

        Check(checks, "快捷方式的图标索引没有被拉平成目标本身",
            byKey.TryGetValue("shortcut-arguments", out var withIcon)
            && withIcon.IconSource is not null
            && withIcon.IconSource.EndsWith(",3", StringComparison.Ordinal),
            byKey.TryGetValue("shortcut-arguments", out var shownIcon) ? $"实际「{shownIcon.IconSource}」" : "条目缺失");

        Check(checks, "指向网址的快捷方式被识别为网页，而不是降级保留 .lnk",
            byKey.TryGetValue("shortcut-url", out var web)
            && web.Kind == AppItemKind.Url
            && web.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            byKey.TryGetValue("shortcut-url", out var shownWeb) ? $"实际 [{shownWeb.Kind}] {shownWeb.Target}" : "条目缺失");

        Check(checks, "中文与空格路径被原样保留",
            byKey.TryGetValue("file", out var chinese) && chinese.Target.Contains("季度 报告 2026", StringComparison.Ordinal),
            byKey.TryGetValue("file", out var shownChinese) ? $"实际「{shownChinese.Target}」" : "条目缺失");

        Section(report, "四、图标缓存");
        report.AppendLine(CultureInfo.InvariantCulture, $"新写 {host.Import.IconsWritten} 个，复用 {host.Import.IconsReused} 个，缓存目录共 {host.Icons.FileCount} 个");
        report.AppendLine();
        var iconVerdict = VerifyIcons(paths, report);
        Check(checks, "每个图标的来源都不相同，因此没有重复文件", host.Import.IconsReused == 0,
            $"复用 {host.Import.IconsReused} 个");
        Check(checks, "全部缓存图标都能被系统解码器读出且有可见内容", iconVerdict.Failures == 0,
            $"失败 {iconVerdict.Failures} 个，共 {iconVerdict.Total} 个");

        Section(report, "五、重启之后条目仍在（换一个进程重新加载文件）");
        var reloaded = new AppLibraryStore(paths.AppLibraryFile);
        reloaded.Load();
        var reloadedUsable = reloaded.Items.Count(entry => entry is not null && entry.IsUsable);
        Check(checks, "重新加载后条目数不变", reloadedUsable == host.Library.Items.Count(entry => entry is not null && entry.IsUsable),
            $"重新加载 {reloadedUsable} 条，原 {host.Library.Items.Count} 条");
        Check(checks, "重新加载后参数仍在",
            reloaded.Items.Any(entry => entry?.Arguments == "--flag \"带 空格 的值\" --second"),
            $"重新加载 {reloadedUsable} 条");

        Section(report, "六、目标失效：条目保留、标记失效、可重新定位");
        var deleted = SampleDataBuilder.DeleteTarget(set.BrokenTarget);
        report.AppendLine(CultureInfo.InvariantCulture, $"删除目标：{(deleted ? "成功" : "失败")} → {set.BrokenTarget}");
        report.AppendLine();

        var brokenEntry = byKey.GetValueOrDefault("broken-later");
        Check(checks, "失效样本的目标已被删除", deleted, set.BrokenTarget);

        var missingReport = host.Maintenance.RefreshMissingTargets();
        Check(checks, "复查发现恰好一条失效", missingReport.Missing == 1, $"实际 {missingReport.Missing} 条");
        Check(checks, "失效状态有变化所以写回了文件", missingReport.Changed, $"Changed={missingReport.Changed}，失效 {missingReport.Missing} 条");

        var afterCheck = new AppLibraryStore(paths.AppLibraryFile);
        afterCheck.Load();
        var stillThere = brokenEntry is not null && afterCheck.Find(brokenEntry.Id) is not null;
        Check(checks, "失效条目仍然在文件里，没有被丢掉", stillThere, stillThere ? "记录仍在文件中" : "记录不见了");
        Check(checks, "重新加载后该条目标记为失效",
            brokenEntry is not null && afterCheck.Find(brokenEntry.Id)?.IsTargetMissing == true,
            brokenEntry is null ? "样本条目缺失" : $"持久化后的标记={afterCheck.Find(brokenEntry.Id)?.IsTargetMissing}");

        Section(report, "七、失效条目不影响启动");
        if (brokenEntry is not null)
        {
            var launch = host.Launch.LaunchItem(brokenEntry.Id);
            report.AppendLine(CultureInfo.InvariantCulture, $"打开失效条目 → 成功={launch.Succeeded}，分类={launch.Failure}，说明={launch.Message}");
            report.AppendLine();

            Check(checks, "打开失效条目失败但给出了分类", !launch.Succeeded && launch.Failure == LaunchFailureReason.TargetMissing,
                $"成功={launch.Succeeded}，分类={launch.Failure}");
            Check(checks, "给出的说明是中文且指向重新定位",
                launch.Message?.Contains("重新定位", StringComparison.Ordinal) == true,
                launch.Message ?? "（没有说明）");
        }

        var healthy = byKey.GetValueOrDefault("program");
        if (healthy is not null)
        {
            // 只核对服务层的**判定**，不真的把条目打开。
            //
            // 早先这里调 LaunchItem 去真开一次，代价是在用户桌面上留窗口：
            // 打开文档会拉起记事本、打开文件夹会拉起资源管理器；如果那条记录的目标
            // 正好已经不在（样本目录被清理过），记事本还会弹「系统找不到指定的路径」。
            // 「库里有失效条目时，正常条目不失效」本来就是判定问题；
            // 真正的打开动作由 `--launch` 命令与人工核对覆盖。
            var healthyGone = LaunchService.IsTargetGone(healthy);
            var brokenStillReported = brokenEntry is null
                || host.Launch.LaunchItem(brokenEntry.Id).Failure == LaunchFailureReason.TargetMissing;

            report.AppendLine(CultureInfo.InvariantCulture,
                $"正常条目（{healthy.DisplayName}）目标存在={!healthyGone}，未标记失效={!healthy.IsTargetMissing}");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"失效条目仍被如实报告为 TargetMissing={brokenStillReported}");
            report.AppendLine();

            Check(checks, "库里有失效条目时，正常条目不失效",
                !healthyGone && !healthy.IsTargetMissing && brokenStillReported,
                healthyGone ? "正常条目的目标被判为不存在" : "正常条目判定正常，失效条目仍被如实报告");
        }

        Section(report, "八、重新定位");
        var relocationTarget = Path.Combine(set.Directory, "重新定位后的目标.cmd");
        File.WriteAllText(relocationTarget, "@echo off" + Environment.NewLine, new UTF8Encoding(false));

        if (brokenEntry is not null)
        {
            // 重定位会就地修改这条记录，所以旧路径要先抄下来，
            // 否则之后再导入时拿到的是新路径，得到的是「重复 1」。
            var originalBrokenTarget = brokenEntry.Target;

            var outcome = host.Maintenance.Relocate(brokenEntry.Id, relocationTarget);
            report.AppendLine(CultureInfo.InvariantCulture, $"重新定位 → {outcome.Result}：{outcome.Message}");
            report.AppendLine();

            Check(checks, "重新定位成功", outcome.Result == RelocateResult.Relocated, outcome.Message);

            var afterRelocate = new AppLibraryStore(paths.AppLibraryFile);
            afterRelocate.Load();
            var relocated = afterRelocate.Find(brokenEntry.Id);

            Check(checks, "新目标被持久化", relocated?.Target == relocationTarget, relocated?.Target ?? "记录不见了");
            Check(checks, "失效标记被清除", relocated?.IsTargetMissing == false, $"{relocated?.IsTargetMissing}");
            Check(checks, "图标来源跟着目标走", relocated?.IconSource == relocationTarget, relocated?.IconSource ?? "（空）");

            // 撞车必须被挡住：另一条记录已经指向这个位置时不许改。
            var collision = host.Maintenance.Relocate(healthy?.Id ?? string.Empty, relocationTarget);
            Check(checks, "重新定位到已占用的位置会被拒绝",
                collision.Result == RelocateResult.ConflictsWithExisting,
                $"实际 {collision.Result}");

            // 网址不接受重新定位。
            var urlEntry = byKey.GetValueOrDefault("url-file");
            if (urlEntry is not null)
            {
                var urlRelocate = host.Maintenance.Relocate(urlEntry.Id, relocationTarget);
                Check(checks, "网址不接受重新定位",
                    urlRelocate.Result == RelocateResult.NotRelocatable,
                    $"实际 {urlRelocate.Result}");
            }

            // 重新定位把旧位置空出来了，旧位置必须能再导入。
            // 复核发现：旧键留在去重索引里没清掉，于是再导入旧路径得到
            // 「新增 0、重复 1」，一个合法的导入被拒。
            //
            // 旧路径的文件在第六节已经被删掉了，先把同一个文件造回来——
            // 否则导入会正确地报「找不到」，那验证的是另一件事。
            File.WriteAllText(originalBrokenTarget, "@echo off" + Environment.NewLine);

            var reimport = host.Import.ImportItems([new ImportRequest { Source = originalBrokenTarget }]);

            report.AppendLine(CultureInfo.InvariantCulture,
                $"再导入旧位置 {originalBrokenTarget} → 新增 {reimport.Added.Count}，重复 {reimport.Duplicates.Count}，失败 {reimport.Failures.Count}");
            report.AppendLine();

            Check(checks, "重新定位之后旧位置可以再次被导入",
                reimport.Added.Count == 1 && reimport.Duplicates.Count == 0 && reimport.Failures.Count == 0,
                reimport.Failures.Count > 0
                    ? $"失败：{reimport.Failures[0].Message}"
                    : $"新增 {reimport.Added.Count}，重复 {reimport.Duplicates.Count}");
        }

        Section(report, "九、「开始」菜单扫描");
        var scan = AppServiceHost.ScanStartMenu();
        report.AppendLine(CultureInfo.InvariantCulture, $"扫描根目录：{string.Join("；", scan.Roots)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"看到 .lnk {scan.ScannedFileCount} 个，去重后 {scan.Shortcuts.Count} 个（同名让位 {scan.DuplicateNameCount} 个）");

        var samplesByGroup = scan.Shortcuts
            .GroupBy(shortcut => shortcut.Group.Length == 0 ? "（根）" : shortcut.Group, StringComparer.CurrentCulture)
            .OrderByDescending(group => group.Count())
            .Take(15)
            .ToList();

        foreach (var group in samplesByGroup)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {group.Count(),3}  · {group.Key}");
        }

        report.AppendLine();
        Check(checks, "两个「开始」菜单根目录都被扫描", scan.Roots.Count == 2, $"实际 {scan.Roots.Count} 个");
        Check(checks, "扫描到了真实的快捷方式", scan.ScannedFileCount > 0, $"扫到 {scan.ScannedFileCount} 个 .lnk");
        Check(checks, "去重确实生效或确实无需去重",
            scan.ScannedFileCount >= scan.Shortcuts.Count,
            $"原始 {scan.ScannedFileCount}，去重后 {scan.Shortcuts.Count}");

        Section(report, "十、开始菜单条目真的能导入（取前 40 条）");
        var sampleForImport = scan.Shortcuts.Take(40).ToList();
        var menuImport = host.ImportStartMenuShortcuts(sampleForImport);
        report.AppendLine(CultureInfo.InvariantCulture, $"尝试导入 {sampleForImport.Count} 条：新增 {menuImport.Added.Count}，重复 {menuImport.Duplicates.Count}，失败 {menuImport.Failures.Count}");

        if (menuImport.Failures.Count > 0)
        {
            report.AppendLine("失败明细：");

            foreach (var failure in menuImport.Failures)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}：{failure.Message}");
            }
        }

        report.AppendLine();

        var kinds = menuImport.Added
            .GroupBy(entry => entry.Kind)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key}={group.Count()}")
            .ToList();

        report.AppendLine(CultureInfo.InvariantCulture, $"新增条目的类型分布：{string.Join("，", kinds)}");
        report.AppendLine();

        Check(checks, "开始菜单里的条目大部分能导入", menuImport.Added.Count >= sampleForImport.Count / 2,
            $"尝试 {sampleForImport.Count} 条，新增 {menuImport.Added.Count} 条，失败 {menuImport.Failures.Count} 条");

        Section(report, "十一、500 条规模");
        var seeded = Seed(host, 500);
        report.AppendLine(CultureInfo.InvariantCulture, $"合成条目：新增 {seeded.Added} 条，去重跳过 {seeded.Skipped} 条");

        var bigStore = new AppLibraryStore(paths.AppLibraryFile);
        bigStore.Load();
        var bigCount = bigStore.Items.Count(entry => entry is not null && entry.IsUsable);
        report.AppendLine(CultureInfo.InvariantCulture, $"重新加载：{bigCount} 条");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bigMissing = host.Maintenance.RefreshMissingTargets(persist: false);
        sw.Stop();

        report.AppendLine(CultureInfo.InvariantCulture, $"全库失效复查耗时：{sw.Elapsed.TotalMilliseconds:F1} 毫秒（检查 {bigMissing.Checked} 条）");
        report.AppendLine();

        Check(checks, "应用库达到 500 条以上", bigCount >= 500, $"实际 {bigCount} 条");
        Check(checks, "重新加载 500 条没有异常", bigCount == host.Library.Items.Count(entry => entry is not null && entry.IsUsable),
            "重新加载后的条数与内存中的不一致");
        Check(checks, "全库复查在 2 秒内完成", sw.Elapsed.TotalSeconds < 2.0, $"耗时 {sw.Elapsed.TotalMilliseconds:F0} 毫秒");

        Section(report, "十二、窗口激活：已运行就不起第二个实例");
        var activation = RunActivationCheck(host, report);

        if (activation.Skipped)
        {
            // 条件不具备（缺靶子程序，或用户桌面已开着靶子窗口）：如实记为跳过，
            // 绝不用「通过」冒充。
            Skipped(checks, "窗口激活检查", activation.Detail);
        }
        else
        {
            Check(checks, "第一次请求会启动应用", activation.Launched, activation.Detail);
            Check(checks, "第二次请求找到了已运行的窗口", activation.FoundWindow, activation.Detail);
            Check(checks, "第二次请求把窗口提到前台而不是再起一个", activation.Activated, activation.Detail);
        }

        Section(report, "十三、导出图标联系表作为人工核对材料");
        var sheetPath = arguments[0];
        var drawn = RenderContactSheet(host, workspace.GetOutput("image"), sheetPath, report);
        Check(checks, "联系表里有图标", drawn > 0, $"画出 {drawn} 个");

        Section(report, "十四、跨目录恢复：换一个数据目录也能用");
        var crossDirectory = RunCrossDirectoryRestore(host, paths, workspace, report);
        Check(checks, "恢复到另一个数据目录后图标文件随之到位", crossDirectory.FileRestored, crossDirectory.Detail);
        Check(checks, "恢复后的 apps.json 指向新目录而不是旧目录", crossDirectory.ReferenceRepointed, crossDirectory.Detail);
        Check(checks, "删掉原目录之后图标仍能读取", crossDirectory.StillReadable, crossDirectory.Detail);

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
            $"共 {checks.Count} 项检查：通过 {passed} 项，失败 {failed} 项，跳过 {skipped} 项（○ 是条件不具备、未验证的项）。");

        AcceptanceEntryPoint.WriteText(workspace.GetOutput("report"), report.ToString());
        ReportWriter.Write(report.ToString(), null);

        return failed == 0 ? 0 : 1;
    }

    // ---- 各段 ----

    private static ImportResult ImportAll(AppServiceHost host, SampleSet set, StringBuilder report)
    {
        var requests = set.ShouldImport.Select(source => new ImportRequest
        {
            Source = source.Path,

            // 与 --import 命令同一套判定：能解析成 http/https 的按网址处理。
            IsUrl = Uri.TryCreate(source.Path, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        }).ToList();

        var result = host.Import.ImportItems(requests);

        report.AppendLine(CultureInfo.InvariantCulture, $"导入 {requests.Count} 个来源：新增 {result.Added.Count}，重复 {result.Duplicates.Count}，失败 {result.Failures.Count}");
        report.AppendLine();

        foreach (var entry in result.Added)
        {
            var missing = entry.IsTargetMissing ? "（已标记失效）" : string.Empty;
            report.AppendLine(CultureInfo.InvariantCulture, $"  + [{entry.Kind}] {entry.DisplayName}{missing}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      目标 {entry.Target}");

            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"      参数 {entry.Arguments}");
            }
        }

        foreach (var failure in result.Failures)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}：{failure.Message}");
        }

        report.AppendLine();
        return result;
    }

    private static ImportResult ImportRejected(AppServiceHost host, SampleSet set, StringBuilder report)
    {
        var requests = set.ShouldFail.Select(source => new ImportRequest { Source = source.Path }).ToList();
        var result = host.Import.ImportItems(requests);

        report.AppendLine(CultureInfo.InvariantCulture, $"导入 {requests.Count} 个坏来源：新增 {result.Added.Count}，失败 {result.Failures.Count}");
        report.AppendLine();

        foreach (var failure in result.Failures)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}：{failure.Message}");
        }

        report.AppendLine();
        return result;
    }

    /// <summary>按目标与参数把库里的记录对应回样本的 Key。</summary>
    /// <remarks>
    /// 先按目标精确对应，再退回按显示名。只按显示名对应时，两个只差端口的网址
    /// 推导出的名称都是主机名 <c>localhost</c>，会同时落到同一条记录上——
    /// 那是验收脚本自己看错了，不是产品错。
    /// </remarks>
    private static Dictionary<string, AppEntry> MapByKey(AppServiceHost host, SampleSet set)
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
        var all = host.Library.Items.Where(entry => entry is not null && entry.IsUsable).ToList();

        foreach (var source in set.ShouldImport)
        {
            Func<AppEntry, bool> matches;

            if (source.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || source.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 网址必须按目标精确对应。两个只差端口的网址，用文件名启发式会
                // 都落到同一条上（它们的路径段一样），从而掩盖真正的失败。
                matches = entry => string.Equals(entry.Target, source.Path, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var fileName = Path.GetFileNameWithoutExtension(source.Path);
                matches = entry => entry.DisplayName == fileName
                    || entry.Target.Contains(fileName, StringComparison.Ordinal);
            }

            var match = all.FirstOrDefault(matches);

            if (match is not null)
            {
                map[source.Key] = match;
            }
        }

        return map;
    }

    private static (int Total, int Failures) VerifyIcons(AppPaths paths, StringBuilder report)
    {
        if (!Directory.Exists(paths.IconCacheDirectory))
        {
            return (0, 0);
        }

        var files = Directory.GetFiles(paths.IconCacheDirectory, "*.png");
        var failures = 0;

        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);

                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                var visible = CountVisiblePixels(frame);

                if (visible == 0)
                {
                    failures++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} 全是透明像素");
                }
                else
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✓ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} 可见像素 {visible}");
                }
            }
            catch (Exception ex) when (ex is FileFormatException or NotSupportedException or IOException)
            {
                failures++;
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} 解码失败：{ex.Message}");
            }
        }

        report.AppendLine();
        return (files.Length, failures);
    }

    private static int CountVisiblePixels(BitmapSource frame)
    {
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var count = 0;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                count++;
            }
        }

        return count;
    }

    private static (int Added, int Skipped) Seed(AppServiceHost host, int count)
    {
        var added = 0;
        var skipped = 0;

        for (var index = 0; index < count; index++)
        {
            var entry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.File,
                Target = @"C:\Windows\System32\notepad.exe",
                DisplayName = string.Create(CultureInfo.InvariantCulture, $"合成条目 {index:D4}"),
                Arguments = string.Create(CultureInfo.InvariantCulture, $"--seed {index}"),
                IconSource = @"C:\Windows\System32\notepad.exe",
            };

            if (host.Library.Add(entry) == AddEntryResult.Added)
            {
                added++;
            }
            else
            {
                skipped++;
            }
        }

        host.Library.Save();
        return (added, skipped);
    }

    private static int RenderContactSheet(AppServiceHost host, Stream output, string outputPath, StringBuilder report)
    {
        var entries = host.Library.Items
            .Where(entry => entry is not null && entry.IsUsable && !string.IsNullOrWhiteSpace(entry.IconSource))
            .GroupBy(entry => entry.IconSource!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(60)
            .ToList();

        const int Cell = 64;
        const int Columns = 10;

        var rows = Math.Max(1, (entries.Count + Columns - 1) / Columns);
        var stride = Columns * Cell * 4;
        var canvas = new byte[stride * rows * Cell];

        var drawn = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var iconPath = host.Icons.PathFor(entries[index].IconSource!);

            if (!File.Exists(iconPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(iconPath);

                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var source = new FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);

                var width = source.PixelWidth;
                var height = source.PixelHeight;
                var sourceStride = width * 4;
                var pixels = new byte[sourceStride * height];
                source.CopyPixels(pixels, sourceStride, 0);

                var column = index % Columns;
                var row = index / Columns;

                for (var y = 0; y < Cell; y++)
                {
                    var sourceY = y * height / Cell;

                    for (var x = 0; x < Cell; x++)
                    {
                        var sourceX = x * width / Cell;
                        var from = (sourceY * sourceStride) + (sourceX * 4);
                        var to = ((row * Cell + y) * stride) + ((column * Cell + x) * 4);

                        canvas[to] = pixels[from];
                        canvas[to + 1] = pixels[from + 1];
                        canvas[to + 2] = pixels[from + 2];
                        canvas[to + 3] = pixels[from + 3];
                    }
                }

                drawn++;
            }
            catch (Exception ex) when (ex is FileFormatException or IOException)
            {
                // 这一张画不出来就跳过，联系表整体仍然有用。
            }
        }

        var png = PngWriter.EncodeBgra(canvas, Columns * Cell, rows * Cell);
        output.Write(png);
        output.Flush();

        report.AppendLine(CultureInfo.InvariantCulture, $"带图标来源的条目 {entries.Count} 条（按来源去重），实际画出 {drawn} 个");
        report.AppendLine(CultureInfo.InvariantCulture, $"联系表：{outputPath}（{Columns * Cell}×{rows * Cell}）");
        report.AppendLine();

        return drawn;
    }

    // ---- 输出工具 ----

    /// <summary>
    /// 验证「已运行就激活」这条路径。
    /// </summary>
    /// <remarks>
    /// 用 <c>charmap.exe</c>（字符映射表）做靶子：它是少数仍然自己拥有窗口的经典
    /// Win32 程序。这一步<strong>必须用真实应用</strong>——窗口归属是按进程映像的
    /// 完整路径判定的，用替身或假窗口验证不了这条链路。
    ///
    /// <para>
    /// <b>已知边界（实测）</b>：按映像路径匹配只对「窗口归属进程就是目标程序」的
    /// 应用有效。系统把执行转交给别的进程时匹配不到，实测三类：
    /// 控制台程序由 <c>WindowsTerminal.exe</c> 承载、
    /// <c>System32</c> 里的存根（如 <c>notepad.exe</c>）实际打开的是 WindowsApps 里的打包版本、
    /// UWP 应用的窗口归属 <c>ApplicationFrameHost.exe</c>。
    /// 这三类会退化成「再启动一次」——对单实例应用来说系统自己会把已有窗口调到前面，
    /// 所以表现仍然正确，但对多实例程序（如 cmd）会多开一个窗口。
    /// 完整解法是改用 AppUserModelID 判定归属，属阶段 4 的 D-09 / D-10。
    /// </para>
    /// </remarks>
    private static (bool Launched, bool FoundWindow, bool Activated, bool Skipped, string Detail) RunActivationCheck(
        AppServiceHost host,
        StringBuilder report)
    {
        const string Target = @"C:\Windows\System32\charmap.exe";

        if (!File.Exists(Target))
        {
            report.AppendLine($"本机没有 {Target}，跳过窗口激活检查。");
            report.AppendLine();
            return (false, false, false, true, "本机缺少靶子程序，未执行");
        }

        var importResult = host.Import.ImportItems([new ImportRequest { Source = Target }]);

        var entry = importResult.Added.Count > 0
            ? importResult.Added[0]
            : importResult.Duplicates.Count > 0 ? importResult.Duplicates[0] : null;

        if (entry is null)
        {
            report.AppendLine("无法把靶子程序导入应用库，跳过窗口激活检查。");
            report.AppendLine();
            return (false, false, false, false, "靶子条目创建失败");
        }

        // 用户桌面可能自己开着 charmap。此时既不能杀它（那是用户的窗口），
        // 也不能在同一份结论里混入它的状态——如实标注后跳过。
        // 按**映像路径**检查有没有窗口在，不按进程名扫全局。
        if (Platform.Shell.WindowLocator.FindWindowsForImage(Target).Count > 0)
        {
            report.AppendLine("用户桌面上已有字符映射表窗口在运行；");
            report.AppendLine("为不碰用户窗口，本次激活检查跳过并标注为未验证。");
            report.AppendLine();
            return (false, false, false, true, "用户桌面已有靶子窗口在运行，为不碰用户窗口而跳过");
        }

        var first = host.Launch.ActivateOrLaunch(entry.Id);
        report.AppendLine(CultureInfo.InvariantCulture, $"第一次 ActivateOrLaunch → {first.Outcome}（进程 id {host.Launch.LastLaunchedProcessId}）");

        // 记录自己启动的靶子进程：收尾只按这个 pid，绝不按进程名清理。
        var spawnedProcessId = host.Launch.LastLaunchedProcessId;

        // 窗口不是立刻出现的，等它一下。这里等的是外部的真实事件，
        // 不是「让已排队的界面工作跑完」，所以轮询是合适的做法。
        var windows = Array.Empty<Core.Contracts.WindowCandidate>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var title = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            var located = Platform.Shell.WindowLocator.FindWindowsForImage(Target);

            if (located.Count > 0)
            {
                windows = located
                    .Select(window => new Core.Contracts.WindowCandidate { Handle = window.Handle, Title = window.Title })
                    .ToArray();
                title = located[0].Title;
                break;
            }

            Thread.Sleep(250);
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"按映像路径找到的窗口：{windows.Length} 个{(title.Length > 0 ? $"，标题「{title}」" : string.Empty)}");

        var second = host.Launch.ActivateOrLaunch(entry.Id);
        report.AppendLine(CultureInfo.InvariantCulture, $"第二次 ActivateOrLaunch → {second.Outcome}");

        if (second.Candidates.Count > 0)
        {
            foreach (var candidate in second.Candidates)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · 候选窗口 0x{candidate.Handle:X} {candidate.Title}");
            }
        }

        if (!string.IsNullOrEmpty(second.Message))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"说明：{second.Message}");
        }

        report.AppendLine();

        // 不要让靶子留在用户桌面上。只结束**自己启动并记录**的进程；
        // 用户中途自己打开的 charmap 窗口不会被这条路径碰到。
        KillRecordedProcess(spawnedProcessId);

        var launched = first.Outcome == ActivationOutcome.Launched;
        var found = windows.Length > 0;
        var activated = second.Outcome is ActivationOutcome.Activated or ActivationOutcome.MultipleWindowsFound;

        var detail = windows.Length == 0
            ? "一个窗口都没匹配到——映像路径匹配这一环可能失效了"
            : $"第一次 {first.Outcome}，第二次 {second.Outcome}，窗口「{title}」";

        return (launched, found, activated, false, detail);
    }

    /// <summary>跨目录恢复检查的结果。</summary>
    /// <param name="FileRestored">图标文件随恢复落到了新目录。</param>
    /// <param name="ReferenceRepointed">落盘的 apps.json 指向新目录。</param>
    /// <param name="StillReadable">删掉原目录之后图标仍能读取。</param>
    /// <param name="Detail">给报告看的说明。</param>
    private sealed record CrossDirectoryRestoreResult(
        bool FileRestored,
        bool ReferenceRepointed,
        bool StillReadable,
        string Detail);

    /// <summary>
    /// 备份到归档，恢复到<strong>另一个</strong>数据目录，然后删掉原目录。
    /// </summary>
    /// <remarks>
    /// 这是换电脑 / 迁移数据目录的真实形状。复核第二轮发现的缺陷正是这里：
    /// 图标文件被复制到了新目录，但 <c>CustomIconPath</c> 仍指着旧目录，
    /// 旧目录一删图标就失效——而且当时的判据「原路径不存在才改写」在
    /// 同一台机器上根本不会触发，因为旧目录还在。
    /// </remarks>
    private static CrossDirectoryRestoreResult RunCrossDirectoryRestore(
        AppServiceHost host,
        AppPaths paths,
        GlassDesk.Platform.Storage.AcceptanceWorkspace workspace,
        StringBuilder report)
    {
        // 给某条记录配一个自定义图标：它是从磁盘复制进 icons/ 的用户数据，
        // 不属于可以重新提取的缓存，因此才值得随备份走。
        var entry = host.Library.Items.FirstOrDefault(item => item is not null && item.IsUsable);

        if (entry is null)
        {
            return new CrossDirectoryRestoreResult(false, false, false, "应用库里没有可用条目");
        }

        var iconSource = Path.Combine(paths.RootDirectory, "验收 自定义图标.png");
        File.WriteAllBytes(iconSource, PngWriter.EncodeBgra(new byte[16 * 16 * 4], 16, 16));

        if (!host.Maintenance.SetCustomIcon(entry.Id, iconSource))
        {
            return new CrossDirectoryRestoreResult(false, false, false, "设置自定义图标失败");
        }

        var originalIcon = host.Library.Find(entry.Id)!.CustomIconPath!;
        var archiveDirectory = Path.Combine(paths.BackupDirectory, "验收 归档");

        var backup = new Core.Storage.BackupService(paths).BackupTo(archiveDirectory);

        if (!backup.Succeeded)
        {
            return new CrossDirectoryRestoreResult(false, false, false, backup.Error ?? "备份失败");
        }

        // 新家：另一个数据目录，代表另一台机器上的 %APPDATA%。
        var newHome = new AppPaths(workspace.CreateChildDataRoot("phase3-cross-directory-restore"));
        var restore = new Core.Storage.BackupService(newHome).RestoreFrom(archiveDirectory);

        report.AppendLine(CultureInfo.InvariantCulture, $"备份 → {archiveDirectory}");
        report.AppendLine(CultureInfo.InvariantCulture, $"恢复 → {newHome.RootDirectory}：{(restore.Succeeded ? "成功" : restore.Error)}");
        report.AppendLine("恢复目标位于本次隔离验收工作区内，保留供核对。");
        report.AppendLine(CultureInfo.InvariantCulture, $"改写图标引用 {restore.RepairedIconReferences} 条");
        report.AppendLine();

        if (!restore.Succeeded)
        {
            return new CrossDirectoryRestoreResult(false, false, false, restore.Error ?? "恢复失败");
        }

        var iconInNewHome = AppPaths.ManagedCustomIconPath(newHome.RootDirectory, Path.GetFileName(originalIcon));
        var fileRestored = File.Exists(iconInNewHome);

        // 落盘的 apps.json 必须指向新目录。这一步曾在同一台机器上被漏掉：
        // 旧目录还在，旧路径照样解析得通，于是没人去改它。
        var storedLibrary = new AppLibraryStore(newHome.AppLibraryFile);
        storedLibrary.Load();
        var storedPath = storedLibrary.Find(entry.Id)?.CustomIconPath;
        var referenceRepointed = string.Equals(storedPath, iconInNewHome, StringComparison.OrdinalIgnoreCase);

        report.AppendLine(CultureInfo.InvariantCulture, $"新目录里的图标文件：{iconInNewHome}（{(fileRestored ? "存在" : "缺失")}）");
        report.AppendLine(CultureInfo.InvariantCulture, $"落盘引用：{storedPath}");
        report.AppendLine();

        // 删掉原始图标文件，再读一次——这才是「换电脑」完成后的状态：
        // 旧的那份已经不在了，新目录里的引用必须自己站得住。
        // （刻意只删这一个文件，而不是把整个原数据目录删掉——
        // 后者会顺带拆掉本次验收自己的数据目录。）
        File.Delete(originalIcon);

        var reloaded = new AppLibraryStore(newHome.AppLibraryFile);
        reloaded.Load();
        var finalPath = reloaded.Find(entry.Id)?.CustomIconPath;
        var stillReadable = File.Exists(finalPath);

        report.AppendLine(CultureInfo.InvariantCulture, $"删除原始图标之后：引用 {finalPath}，文件{(stillReadable ? "可读" : "缺失")}");
        report.AppendLine();

        var detail = $"图标 {Path.GetFileName(originalIcon)} → 新目录{(fileRestored ? "已到位" : "缺失")}，"
            + $"引用{(referenceRepointed ? "已改写" : $"仍为 {storedPath}")}，"
            + $"删除原始图标后{(stillReadable ? "仍可读" : "失效")}";

        return new CrossDirectoryRestoreResult(fileRestored, referenceRepointed, stillReadable, detail);
    }

    /// <summary>
    /// 结束验收自己启动并记录的靶子进程。
    /// </summary>
    /// <remarks>
    /// 刻意不按进程名清理：那会把用户自己打开的同名窗口一起杀掉。
    /// 只认启动时记录下来的那个 pid，进程不在就当已经退出了。
    /// 阶段 4 的验收沿用同一条纪律，所以 internal 共享。
    /// </remarks>
    internal static void KillRecordedProcess(uint processId)
    {
        if (processId == 0)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已经不在（或拿不到句柄）——它本来就是我们拉起来的靶子，
            // 退出即达目的，不算失败。
        }
    }

    private static void Section(StringBuilder report, string title)
    {
        report.AppendLine();
        report.AppendLine(title);
        report.AppendLine(new string('-', 46));
    }

    private static void Check(List<(string Label, CheckStatus Status, string Detail)> checks, string label, bool passed, string detail)
        => checks.Add((label, passed ? CheckStatus.Passed : CheckStatus.Failed, detail));

    /// <summary>条件不具备、没跑的检查——既不算通过也不算失败。</summary>
    private static void Skipped(List<(string Label, CheckStatus Status, string Detail)> checks, string label, string detail)
        => checks.Add((label, CheckStatus.Skipped, detail));

    private static string DescribeFailures(ImportResult result)
        => result.Failures.Count == 0
            ? "一条失败都没有"
            : string.Join("；", result.Failures.Select(failure => $"{failure.Reason}"));

    private static string DescribeKeys(Dictionary<string, AppEntry> map)
        => map.Count == 0 ? "一条都没对上" : string.Join("、", map.Keys);
}
