using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.App.Startup;

/// <summary>隔离验收阶段五的数据、搜索和布局行为。</summary>
internal static class Phase5Acceptance
{
    public static int Run(AppPaths realPaths, IReadOnlyList<string> arguments, GlassDesk.Platform.Storage.AcceptanceWorkspace acceptanceWorkspace)
    {
        var reportPath = arguments.Count > 0 ? Path.GetFullPath(arguments[0])
            : Path.Combine(Environment.CurrentDirectory, "phase5-acceptance.log");
        var root = acceptanceWorkspace.DataRoot;
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var log = new StringBuilder();
        var failed = 0;
        var passed = 0;
        log.AppendLine(CultureInfo.InvariantCulture, $"GlassDesk 阶段五命令验收 | {DateTimeOffset.Now:O}");
        log.AppendLine(CultureInfo.InvariantCulture, $"产物：{typeof(Phase5Acceptance).Assembly.FullName}");
        log.AppendLine(CultureInfo.InvariantCulture, $"环境：{Environment.OSVersion}; {Environment.MachineName}");
        log.AppendLine(CultureInfo.InvariantCulture, $"测试数据：{root}");
        log.AppendLine("驱动：真实文件持久化与纯服务；无 WPF 路由、无系统输入注入");

        void Check(string name, string ids, bool okay, string expected, string actual)
        {
            if (okay) passed++; else failed++;
            log.AppendLine(CultureInfo.InvariantCulture, $"[{(okay ? "Passed" : "Failed")}] {name} | {ids} | 预期：{expected} | 实际：{actual} | {DateTimeOffset.Now:O}");
        }

        try
        {
            var data = new GlassDeskDataStore(paths);
            data.LoadAll();
            for (var i = 0; i < 500; i++)
                data.Library.Add(new AppEntry
                {
                    Id = $"app-{i:000}", DisplayName = i switch
                    {
                        0 => "启动台", 1 => "Visual Studio Code", 499 => "终点 工具 499", _ => $"应用 App {i:000}",
                    },
                    Target = Path.Combine(root, $"中文 路径 {i:000}"), IsTargetMissing = i % 10 == 0,
                    SearchAliases = i == 499 ? ["last-item"] : [],
                });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            var layoutWorkspace = new LauncherWorkspace(host);
            layoutWorkspace.Reconcile(35);
            Check("500 条默认分页", "L-07/L-08", layoutWorkspace.Layout.Pages.Count == 15 && layoutWorkspace.Layout.Pages[^1].Slots.Count == 10,
                "15 页，末页 10 条", $"{layoutWorkspace.Layout.Pages.Count} 页，末页 {layoutWorkspace.Layout.Pages[^1].Slots.Count} 条");
            var stamp = File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath);
            var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            var coldWatch = Stopwatch.StartNew();
            host.Search.SearchApps("last-item", 500);
            coldWatch.Stop();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) host.Search.SearchApps("last-item", 500);
            watch.Stop();
            var searchId = host.Search.SearchApps("last-item", 500).SingleOrDefault()?.Item.Id;
            Check("最后一条可搜索", "L-01-L-06", searchId == "app-499", "app-499", searchId ?? "无结果");
            Check("全拼与首字母", "L-03/L-04",
                host.Search.SearchApps("qidongtai", 500).Any(hit => hit.Item.Id == "app-000")
                && host.Search.SearchApps("qdt", 500).Any(hit => hit.Item.Id == "app-000"),
                "启动台两种拼音均命中", "查询已执行");
            Check("搜索不写布局", "R-08", stamp == File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath)
                && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)), "字节与修改时间不变", "已核对");
            log.AppendLine(CultureInfo.InvariantCulture, $"性能样本：500 条；本机 {Environment.MachineName}；首次冷态 {coldWatch.Elapsed.TotalMilliseconds:F2} ms；100 次暖态全库搜索总耗时 {watch.Elapsed.TotalMilliseconds:F2} ms，平均 {watch.Elapsed.TotalMilliseconds / 100:F3} ms/次。包含匹配排序，不含进程启动与磁盘读取。");
            var edit = layoutWorkspace.BeginEdit();
            edit.Draft = LauncherLayoutEditor.CreateGroup(edit.Draft, "app-000", "app-001", "group-a", 35);
            edit.Draft = LauncherLayoutEditor.CreateGroup(edit.Draft, "app-002", "app-003", "group-b", 35);
            edit.Draft = LauncherLayoutEditor.Move(edit.Draft, "app-499", 0, 2, 35);
            var committed = layoutWorkspace.Commit(edit);
            var committedGroups = layoutWorkspace.Layout.Pages.SelectMany(page => page.Slots)
                .Where(slot => slot.Group is not null).Select(slot => slot.Group!).ToList();
            var firstGroup = committedGroups.SingleOrDefault(group => group.Id == "group-a");
            var secondGroup = committedGroups.SingleOrDefault(group => group.Id == "group-b");
            var committedMove = layoutWorkspace.Layout.Pages.SelectMany(page => page.Slots)
                .Select(slot => slot.ItemId ?? slot.Group?.Id).ToList().IndexOf("app-499") == 2;
            var groupsCorrect = committedGroups.Count == 2
                && firstGroup?.ItemIds.SequenceEqual(["app-001", "app-000"]) == true
                && secondGroup?.ItemIds.SequenceEqual(["app-003", "app-002"]) == true;
            Check("多分组与跨页排序提交", "L-09/L-11-L-16", committed && groupsCorrect && committedMove,
                "两个单层分组成员及顶层顺序正确", $"已提交={committed}；分组数={committedGroups.Count}；末项新索引={layoutWorkspace.Layout.Pages.SelectMany(page => page.Slots).Select(slot => slot.ItemId ?? slot.Group?.Id).ToList().IndexOf("app-499")}");
            var ids = LauncherLayoutRules.AppIds(layoutWorkspace.Layout);
            Check("整理无重复无丢失", "L-09/L-11/L-16", ids.Count == 500 && ids.Distinct().Count() == 500,
                "500 个唯一 Id", $"{ids.Count} 个，唯一 {ids.Distinct().Count()} 个");
            var reloaded = new GlassDeskDataStore(paths);
            reloaded.LoadAll();
            Check("新存储实例重载布局", "L-09/L-11/L-13", LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher).SequenceEqual(ids),
                "顺序一致", "新实例重新读盘核对");
            var beforeCancel = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            var cancelled = layoutWorkspace.BeginEdit();
            cancelled.Draft = LauncherLayoutEditor.Dissolve(cancelled.Draft, "group-a", 35);
            Check("取消预览不写盘", "R-08", beforeCancel.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                "字节不变", "未调用提交");
            layoutWorkspace.Reconcile(20);
            Check("改网格后不丢成员", "L-07", LauncherLayoutRules.AppIds(layoutWorkspace.Layout).Count == 500,
                "500 个成员", $"{LauncherLayoutRules.AppIds(layoutWorkspace.Layout).Count} 个成员");
        }
        catch (Exception ex)
        {
            failed++;
            log.AppendLine("[Failed] runner | L-01-L-25 | 预期：无异常 | 实际：" + ex);
        }
        log.AppendLine(CultureInfo.InvariantCulture, $"合计：Passed={passed} Failed={failed}");
        AcceptanceEntryPoint.WriteText(acceptanceWorkspace.GetOutput("report"), log.ToString());
        ReportWriter.Write(log.ToString(), null);
        return failed == 0 ? 0 : 1;
    }
}
