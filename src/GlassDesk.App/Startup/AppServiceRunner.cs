using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Imaging;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.App.Startup;

/// <summary>
/// 无界面地跑一条应用库命令并输出报告。
/// </summary>
/// <remarks>
/// 与 <see cref="DiagnosticRunner"/> 同样的用意：让交付物能被脚本验证，
/// 而不是靠人去点界面。阶段 3 的交付是「稳定的共用应用库及导入、启动流程」，
/// 这条流程的每一环——解析快捷方式、取图标、去重、落盘、重新加载、重新定位——
/// 都在这里被真实地走一遍。
///
/// <para>
/// 全部命令共用 <see cref="AppServiceHost"/> 里那一份应用库实例，
/// 因此「导入紧接着列出」看到的就是刚写进去的内容。
/// </para>
/// </remarks>
internal static class AppServiceRunner
{
    public static int Run(AppPaths paths, CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Verb is AppServiceVerb.Phase3Acceptance or AppServiceVerb.Phase4Acceptance
            or AppServiceVerb.Phase5Acceptance or AppServiceVerb.Phase6Acceptance && options.Unknown.Count > 0)
            return AcceptanceArgumentsError("验收命令必须独立运行且参数完整");

        // 验收命令必须先取得数据和输出句柄，再创建目录、加载/恢复数据、记录日志或运行界面。
        switch (options.Verb)
        {
            case AppServiceVerb.Phase3Acceptance:
                if (options.VerbArguments.Count != 3) return AcceptanceArgumentsError("--phase3-acceptance <图像输出> <报告输出> <新数据目录>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[2],
                    [new("image", options.VerbArguments[0]), new("report", options.VerbArguments[1])],
                    workspace => Phase3Acceptance.Run(paths, options.VerbArguments, workspace));
            case AppServiceVerb.Phase4Acceptance:
                if (options.VerbArguments.Count != 2) return AcceptanceArgumentsError("--phase4-acceptance <报告输出> <新数据目录>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[1], [new("report", options.VerbArguments[0])],
                    workspace => Phase4Acceptance.Run(paths, options.VerbArguments, workspace));
            case AppServiceVerb.Phase5Acceptance:
                if (options.VerbArguments.Count != 2) return AcceptanceArgumentsError("--phase5-acceptance <报告输出> <新数据目录>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[1], [new("report", options.VerbArguments[0])],
                    workspace => Phase5Acceptance.Run(paths, options.VerbArguments, workspace));
            case AppServiceVerb.Phase6Acceptance:
                if (options.VerbArguments.Count != 2) return AcceptanceArgumentsError("--phase6-acceptance <报告输出> <新数据目录>");
                return AcceptanceEntryPoint.Run(paths, options.VerbArguments[1], [new("report", options.VerbArguments[0])],
                    workspace => Phase6Acceptance.Run(paths, options.VerbArguments, workspace));
        }

        var report = new StringBuilder();
        var exitCode = 0;

        // 探针不需要应用库与图标缓存，先于数据存储处理——
        // 它们只对桌面做只读枚举，加上自己启动、自己记录、自己收尾的靶子。
        switch (options.Verb)
        {
            case AppServiceVerb.ProbeAumid:
                return Phase4Probe.RunAumid(paths);
            case AppServiceVerb.ProbeRunEvents:
                return Phase4Probe.RunEvents(paths);
            case AppServiceVerb.ProbeDragDrop:
                return DragDropProbe.Run(paths);
            case AppServiceVerb.ProbeShowDesktop:
                return ShowDesktopProbe.Run(paths);
        }

        report.AppendLine("GlassDesk 应用库命令");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"命令：{DescribeVerb(options.Verb)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"数据目录：{paths.RootDirectory}");
        report.AppendLine();

        try
        {
            var data = new GlassDeskDataStore(paths);
            var loadReport = data.LoadAll();

            foreach (var notice in loadReport.Notices)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"数据加载提示：{notice}");
            }

            using var host = new AppServiceHost(data);

            exitCode = options.Verb switch
            {
                AppServiceVerb.Import => RunImport(host, options, report),
                AppServiceVerb.ScanStartMenu => RunScan(options, report),
                AppServiceVerb.ImportStartMenu => RunImportStartMenu(host, options, report),
                AppServiceVerb.ListLibrary => RunList(host, options, report),
                AppServiceVerb.CheckLibrary => RunCheck(host, report),
                AppServiceVerb.Relocate => RunRelocate(host, options, report),
                AppServiceVerb.Launch => RunLaunch(host, options, report, activate: false),
                AppServiceVerb.Activate => RunLaunch(host, options, report, activate: true),
                AppServiceVerb.VerifyIcons => RunVerifyIcons(host, report),
                AppServiceVerb.IconContactSheet => RunContactSheet(host, options, report),
                AppServiceVerb.ClearIconCache => RunClearIcons(host, report),
            AppServiceVerb.MakeSamples => RunMakeSamples(options, report),
            AppServiceVerb.SeedLibrary => RunSeed(host, options, report),
            _ => 0,
            };

            if (options.Verb is AppServiceVerb.Phase3Acceptance or AppServiceVerb.Phase4Acceptance)
            {
                // 验收自己会写一份完整报告，这里不再重复输出一遍外壳。
                return exitCode;
            }
        }
        catch (StorageException ex)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"✗ 数据无法写入：{ex.Message}");
            exitCode = 1;
        }

        ReportWriter.Write(report.ToString(), Path.Combine(paths.LogDirectory, "appservices.log"));
        return exitCode;
    }

    private static int AcceptanceArgumentsError(string usage)
    {
        Console.Error.WriteLine("验收参数不足或冲突。用法：" + usage);
        return 2;
    }

    // ---- 各条命令 ----

    private static int RunImport(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        if (options.VerbArguments.Count == 0)
        {
            report.AppendLine("没有给出要导入的来源。");
            return 1;
        }

        var requests = options.VerbArguments.Select(CreateRequest).ToList();

        report.AppendLine(CultureInfo.InvariantCulture, $"要导入的来源 {requests.Count} 个：");

        foreach (var request in requests)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · {(request.IsUrl ? "网址" : "路径")} {request.Source}");
        }

        report.AppendLine();

        var result = host.Import.ImportItems(requests);
        DescribeImport(result, report);
        DescribeIconWork(host, report);
        DescribeLibrarySize(host, report);

        return result.Failures.Count > 0 && result.Added.Count == 0 ? 1 : 0;
    }

    private static int RunScan(CommandLineOptions options, StringBuilder report)
    {
        var scan = AppServiceHost.ScanStartMenu();

        report.AppendLine("「开始」菜单扫描");
        report.AppendLine(CultureInfo.InvariantCulture, $"扫描的根目录 {scan.Roots.Count} 个：");

        foreach (var root in scan.Roots)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · {root}");
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"看到的 .lnk 文件：{scan.ScannedFileCount} 个");
        report.AppendLine(CultureInfo.InvariantCulture, $"去重后：{scan.Shortcuts.Count} 个（同名让位 {scan.DuplicateNameCount} 个）");

        var limit = ReadIntOption(options.VerbArguments, 0, 40);

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"按分组与名称排序，列出前 {Math.Min(limit, scan.Shortcuts.Count)} 条：");

        foreach (var shortcut in scan.Shortcuts.Take(limit))
        {
            var group = shortcut.Group.Length == 0 ? "（根）" : shortcut.Group;
            var owner = shortcut.FromCurrentUser ? "当前用户" : "所有用户";
            report.AppendLine(CultureInfo.InvariantCulture, $"  · [{owner}] {group} / {shortcut.Name}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      {shortcut.Path}");
        }

        return 0;
    }

    private static int RunImportStartMenu(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var scan = AppServiceHost.ScanStartMenu();

        report.AppendLine(CultureInfo.InvariantCulture, $"「开始」菜单里找到 {scan.Shortcuts.Count} 条（原始 {scan.ScannedFileCount} 条，同名让位 {scan.DuplicateNameCount} 条）。");

        var limit = ReadIntOption(options.VerbArguments, 0, int.MaxValue);
        var selected = scan.Shortcuts.Take(limit).ToList();

        report.AppendLine(CultureInfo.InvariantCulture, $"本次导入其中 {selected.Count} 条。");
        report.AppendLine();

        var result = host.ImportStartMenuShortcuts(selected);
        DescribeImport(result, report);
        DescribeIconWork(host, report);
        DescribeLibrarySize(host, report);

        return 0;
    }

    private static int RunList(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var store = host.Library;

        var usable = store.Items.Where(entry => entry is not null && entry.IsUsable).ToList();
        var anomalous = store.Items.Count - usable.Count;

        report.AppendLine(CultureInfo.InvariantCulture, $"应用库共 {usable.Count} 条可用的记录。");

        if (anomalous > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"另有 {anomalous} 条无法使用（空条目或缺字段），保留在文件中未动。");
        }

        var missing = usable.Count(entry => entry.IsTargetMissing);
        report.AppendLine(CultureInfo.InvariantCulture, $"其中标记为失效的：{missing} 条");

        var withArguments = usable.Count(entry => !string.IsNullOrEmpty(entry.Arguments));
        report.AppendLine(CultureInfo.InvariantCulture, $"带启动参数的：{withArguments} 条");

        var withCustomIcon = usable.Count(entry => !string.IsNullOrEmpty(entry.CustomIconPath));
        report.AppendLine(CultureInfo.InvariantCulture, $"带自定义图标的：{withCustomIcon} 条");
        report.AppendLine();

        var limit = ReadIntOption(options.VerbArguments, 0, 20);
        report.AppendLine(CultureInfo.InvariantCulture, $"明细（按库中顺序，最多 {limit} 条）：");

        var index = 0;

        foreach (var entry in usable)
        {
            index++;
            var flag = entry.IsTargetMissing ? "失效" : "正常";

            report.AppendLine(CultureInfo.InvariantCulture, $"  {index,3}. [{DescribeKind(entry.Kind)}/{flag}] {entry.DisplayName}");
            report.AppendLine(CultureInfo.InvariantCulture, $"       id       {entry.Id}");
            report.AppendLine(CultureInfo.InvariantCulture, $"       目标     {entry.Target}");

            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"       参数     {entry.Arguments}");
            }

            if (!string.IsNullOrEmpty(entry.WorkingDirectory))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"       工作目录 {entry.WorkingDirectory}");
            }

            if (!string.IsNullOrEmpty(entry.CustomIconPath))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"       自定义图标 {entry.CustomIconPath}");
            }

            if (limit > 0 && index >= limit && index < usable.Count)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  ……（其余 {usable.Count - index} 条未列出）");
                break;
            }
        }

        return 0;
    }

    private static int RunCheck(AppServiceHost host, StringBuilder report)
    {
        var result = host.Maintenance.RefreshMissingTargets();

        report.AppendLine("失效目标复查");
        report.AppendLine(CultureInfo.InvariantCulture, $"检查条目：{result.Checked}");
        report.AppendLine(CultureInfo.InvariantCulture, $"目标找不到：{result.Missing}");
        report.AppendLine(CultureInfo.InvariantCulture, $"状态有变化：{(result.Changed ? "是，已保存" : "否，未写文件")}");
        report.AppendLine();

        if (result.NewlyBroken.Count > 0)
        {
            report.AppendLine("本次新发现失效（条目保留、只标记）：");

            foreach (var entry in result.NewlyBroken)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · {entry.DisplayName}");
                report.AppendLine(CultureInfo.InvariantCulture, $"      {entry.Target}");
            }

            report.AppendLine();
        }

        if (result.Recovered.Count > 0)
        {
            report.AppendLine("目标又回来了，已取消失效标记：");

            foreach (var entry in result.Recovered)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · {entry.DisplayName}");
            }

            report.AppendLine();
        }

        if (!result.Changed && result.Missing == 0)
        {
            report.AppendLine("全部条目的目标都在。");
        }

        return 0;
    }

    private static int RunRelocate(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        if (options.VerbArguments.Count < 2)
        {
            report.AppendLine("用法：--relocate <条目 id> <新路径>");
            return 1;
        }

        var outcome = host.Maintenance.Relocate(options.VerbArguments[0], options.VerbArguments[1]);

        report.AppendLine(CultureInfo.InvariantCulture, $"结果：{outcome.Result}");
        report.AppendLine(CultureInfo.InvariantCulture, $"说明：{outcome.Message}");

        if (outcome.Result == RelocateResult.Relocated)
        {
            var entry = host.Library.Find(options.VerbArguments[0]);

            if (entry is not null)
            {
                report.AppendLine();
                report.AppendLine(CultureInfo.InvariantCulture, $"  名称     {entry.DisplayName}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  新目标   {entry.Target}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  类型     {DescribeKind(entry.Kind)}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  失效标记 {entry.IsTargetMissing}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  图标来源 {entry.IconSource}");
            }
        }

        return outcome.Result == RelocateResult.Relocated ? 0 : 1;
    }

    private static int RunLaunch(AppServiceHost host, CommandLineOptions options, StringBuilder report, bool activate)
    {
        if (options.VerbArguments.Count == 0)
        {
            report.AppendLine(activate ? "用法：--activate <条目 id>" : "用法：--launch <条目 id>");
            return 1;
        }

        var itemId = options.VerbArguments[0];
        var entry = host.Library.Find(itemId);

        if (entry is not null)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"条目：{entry.DisplayName}（{DescribeKind(entry.Kind)}）");
            report.AppendLine(CultureInfo.InvariantCulture, $"目标：{entry.Target}");

            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"参数：{entry.Arguments}");
            }

            report.AppendLine();
        }

        if (activate)
        {
            var outcome = host.Launch.ActivateOrLaunch(itemId);

            report.AppendLine(CultureInfo.InvariantCulture, $"结果：{DescribeActivation(outcome.Outcome)}");

            foreach (var candidate in outcome.Candidates)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  · 候选窗口 0x{candidate.Handle:X} {candidate.Title}");
            }

            if (!string.IsNullOrEmpty(outcome.Message))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"说明：{outcome.Message}");
            }

            return outcome.Outcome is ActivationOutcome.Activated or ActivationOutcome.Launched or ActivationOutcome.MultipleWindowsFound
                ? 0
                : 1;
        }

        var result = host.Launch.LaunchItem(itemId);

        report.AppendLine(CultureInfo.InvariantCulture, $"结果：{(result.Succeeded ? "已交给外壳打开" : "未能打开")}");

        if (result.Failure is not null)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"失败分类：{result.Failure}");
        }

        if (!string.IsNullOrEmpty(result.Message))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"说明：{result.Message}");
        }

        if (result.Succeeded)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"被启动的进程 id：{host.Launch.LastLaunchedProcessId}");
        }

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// 用系统图像解码器（WIC）读一遍缓存里的每一张 PNG。
    /// </summary>
    /// <remarks>
    /// 这一条是必须的：PNG 是我们自己按字节写出来的，只检查
    /// 「文件存在且长度不为零」等于没检查。用 WIC 解码，是找一个
    /// <strong>独立于我们自己实现</strong>的解码器来确认它真的是合法 PNG，
    /// 顺便确认尺寸与像素真的在里面。
    /// </remarks>
    private static int RunVerifyIcons(AppServiceHost host, StringBuilder report)
    {
        var directory = host.Paths.IconCacheDirectory;

        if (!Directory.Exists(directory))
        {
            report.AppendLine("图标缓存目录不存在。");
            return 0;
        }

        var files = Directory.GetFiles(directory, "*.png");

        report.AppendLine(CultureInfo.InvariantCulture, $"缓存目录：{directory}");
        report.AppendLine(CultureInfo.InvariantCulture, $"PNG 文件：{files.Length} 个");
        report.AppendLine();

        var failures = 0;
        var totalPixels = 0L;
        long smallest = long.MaxValue;
        long largest = 0;

        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);

                var decoder = new PngBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];
                var visible = CountVisiblePixels(frame);
                var pixels = (long)frame.PixelWidth * frame.PixelHeight;

                totalPixels += pixels;
                smallest = Math.Min(smallest, pixels);
                largest = Math.Max(largest, pixels);

                if (visible == 0)
                {
                    failures++;
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} 全是透明像素");
                }
                else
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  ✓ {Path.GetFileName(file)} {frame.PixelWidth}×{frame.PixelHeight} 可见像素 {visible} {frame.Format}");
                }
            }
            catch (Exception ex) when (ex is FileFormatException or NotSupportedException or IOException)
            {
                failures++;
                report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {Path.GetFileName(file)} 解码失败：{ex.Message}");
            }
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"像素总数：{totalPixels}");

        if (files.Length > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"单张最小 {smallest} 像素，最大 {largest} 像素");
        }

        var verdict = failures == 0
            ? "全部图标都能被系统解码器读出，且有可见内容。"
            : $"有 {failures} 个文件不可用。";

        report.AppendLine(verdict);

        return failures == 0 ? 0 : 1;
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

    /// <summary>把缓存图标拼成一张联系表，供人工核对图标确实取到了。</summary>
    private static int RunContactSheet(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var entries = host.Library.Items
            .Where(entry => entry is not null && entry.IsUsable && !string.IsNullOrWhiteSpace(entry.IconSource))
            .Take(ReadIntOption(options.VerbArguments, 0, 60))
            .ToList();

        if (entries.Count == 0)
        {
            report.AppendLine("应用库里没有带图标来源的记录。");
            return 1;
        }

        const int Cell = 64;
        const int Columns = 10;

        var rows = (entries.Count + Columns - 1) / Columns;
        var stride = Columns * Cell * 4;
        var canvas = new byte[stride * rows * Cell];

        var drawn = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var iconPath = host.Icons.EnsureFor(entry.IconSource)?.Path;

            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
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

                // 最近邻缩放到格子里：这里只为核对图标内容，不做插值。
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

        var output = options.VerbArguments.Count > 1
            ? options.VerbArguments[1]
            : Path.Combine(Path.GetTempPath(), "glassdesk-icon-contact-sheet.png");

        var sheet = PngWriter.EncodeBgra(canvas, Columns * Cell, rows * Cell);
        File.WriteAllBytes(output, sheet);

        report.AppendLine(CultureInfo.InvariantCulture, $"库中带图标来源的记录：{entries.Count}");
        report.AppendLine(CultureInfo.InvariantCulture, $"实际画出：{drawn}");
        report.AppendLine(CultureInfo.InvariantCulture, $"联系表：{output}（{Columns * Cell}×{rows * Cell}，{sheet.Length} 字节）");

        return drawn > 0 ? 0 : 1;
    }

    private static int RunClearIcons(AppServiceHost host, StringBuilder report)
    {
        var result = host.Icons.Clear();

        report.AppendLine(CultureInfo.InvariantCulture, $"已删除 {result.Removed} 个可再生缓存图标文件。");
        report.AppendLine("自定义图标（custom-*）不清：它们不是缓存，是从用户选的文件复制进来的，删了找不回来。");

        if (result.LeftOthers)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"另有 {result.LeftAlone} 个文件不属于缓存，已原样保留。");
        }

        report.AppendLine("条目上的图标来源保持不变，下次导入或重新定位时会重新提取。");
        return 0;
    }

    private static int RunMakeSamples(CommandLineOptions options, StringBuilder report)
    {
        var root = options.VerbArguments.Count > 0 ? options.VerbArguments[0] : null;
        var set = SampleDataBuilder.Build(root);

        report.Append(SampleDataBuilder.Describe(set));
        report.AppendLine();
        report.AppendLine("下一步：用 --import 导入上面「应当成功导入」的那些来源，");
        report.AppendLine(CultureInfo.InvariantCulture, $"再删掉 {set.BrokenTarget}");
        report.AppendLine("然后用 --check-library 观察失效标记，用 --relocate 重新定位。");

        return 0;
    }

    private static int RunSeed(AppServiceHost host, CommandLineOptions options, StringBuilder report)
    {
        var count = ReadIntOption(options.VerbArguments, 0, 500);
        var store = host.Library;

        var added = 0;
        var skipped = 0;

        for (var index = 0; index < count; index++)
        {
            // 目标刻意用真实存在的系统文件，这样合成条目不会被判为失效，
            // 也不会在复查时被标出来。它们要测的是「库大了之后还正常」，
            // 不是「失效判定对不对」。
            var entry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.File,
                Target = @"C:\Windows\System32\notepad.exe",
                DisplayName = string.Create(CultureInfo.InvariantCulture, $"合成条目 {index:D4}"),
                Arguments = string.Create(CultureInfo.InvariantCulture, $"--seed {index}"),
                IconSource = @"C:\Windows\System32\notepad.exe",
            };

            if (store.Add(entry) == AddEntryResult.Added)
            {
                added++;
            }
            else
            {
                skipped++;
            }
        }

        store.Save();

        report.AppendLine(CultureInfo.InvariantCulture, $"要求造出 {count} 条，实际新增 {added} 条，因去重跳过 {skipped} 条。");
        report.AppendLine(CultureInfo.InvariantCulture, $"应用库现在共 {store.Items.Count} 条。");
        return 0;
    }

    // ---- 工具 ----

    /// <summary>把命令行给的一个来源包成导入请求：能解析成 http/https 的按网址处理。</summary>
    private static ImportRequest CreateRequest(string raw)
    {
        var isUrl = Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        return new ImportRequest { Source = raw, IsUrl = isUrl };
    }

    private static void DescribeImport(ImportResult result, StringBuilder report)
    {
        report.AppendLine("导入结果");
        report.AppendLine(CultureInfo.InvariantCulture, $"  新增 {result.Added.Count} 条");
        report.AppendLine(CultureInfo.InvariantCulture, $"  重复 {result.Duplicates.Count} 条");
        report.AppendLine(CultureInfo.InvariantCulture, $"  失败 {result.Failures.Count} 条");
        report.AppendLine();

        foreach (var entry in result.Added)
        {
            var missing = entry.IsTargetMissing ? "（目标暂时找不到，已标记为失效）" : string.Empty;

            report.AppendLine(CultureInfo.InvariantCulture, $"  + [{DescribeKind(entry.Kind)}] {entry.DisplayName}{missing}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      目标 {entry.Target}");
        }

        foreach (var entry in result.Duplicates)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  = 已存在：[{DescribeKind(entry.Kind)}] {entry.DisplayName} → {entry.Target}");
        }

        foreach (var failure in result.Failures)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  ✗ {failure.Reason}：{failure.Message}");
        }
    }

    private static void DescribeIconWork(AppServiceHost host, StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"图标文件：新写 {host.Import.IconsWritten}，复用 {host.Import.IconsReused}，缓存目录现有 {host.Icons.FileCount} 个");
    }

    private static void DescribeLibrarySize(AppServiceHost host, StringBuilder report)
    {
        var usable = host.Library.Items.Count(entry => entry is not null && entry.IsUsable);
        report.AppendLine(CultureInfo.InvariantCulture, $"应用库现有 {usable} 条记录。");
    }

    private static string DescribeKind(AppItemKind kind) => kind switch
    {
        AppItemKind.Program => "程序",
        AppItemKind.Shortcut => "快捷方式",
        AppItemKind.Folder => "文件夹",
        AppItemKind.File => "文件",
        AppItemKind.Url => "网页",
        _ => kind.ToString(),
    };

    private static string DescribeActivation(ActivationOutcome outcome) => outcome switch
    {
        ActivationOutcome.Activated => "已把现有窗口提到前台",
        ActivationOutcome.Launched => "没有已运行的窗口，已启动",
        ActivationOutcome.MultipleWindowsFound => "找到多个窗口，需要用户选择",
        ActivationOutcome.Failed => "失败",
        _ => outcome.ToString(),
    };

    private static string DescribeVerb(AppServiceVerb verb) => verb switch
    {
        AppServiceVerb.Import => "导入条目",
        AppServiceVerb.ScanStartMenu => "扫描「开始」菜单",
        AppServiceVerb.ImportStartMenu => "导入「开始」菜单",
        AppServiceVerb.ListLibrary => "列出应用库",
        AppServiceVerb.CheckLibrary => "复查失效目标",
        AppServiceVerb.Relocate => "重新定位条目",
        AppServiceVerb.Launch => "打开条目",
        AppServiceVerb.Activate => "激活或启动条目",
        AppServiceVerb.VerifyIcons => "校验缓存图标",
        AppServiceVerb.IconContactSheet => "生成图标联系表",
        AppServiceVerb.Phase3Acceptance => "阶段 3 验收",
        AppServiceVerb.ClearIconCache => "清空图标缓存",
        AppServiceVerb.MakeSamples => "造验收样本",
        AppServiceVerb.SeedLibrary => "造合成条目",
        _ => verb.ToString(),
    };

    private static int ReadIntOption(IReadOnlyList<string> arguments, int index, int fallback)
    {
        if (index >= arguments.Count)
        {
            return fallback;
        }

        return int.TryParse(arguments[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
