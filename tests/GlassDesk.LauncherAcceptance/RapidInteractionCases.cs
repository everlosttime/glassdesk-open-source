using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;

internal static partial class Program
{
    private static void VerifyRapidInteraction(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var initialTiles = grid.Children.Cast<Button>().ToArray();
        var pageTimings = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var watch = Stopwatch.StartNew();
            window.NavigatePage(i % 2 == 0 ? 1 : -1);
            pageTimings.Add(watch.Elapsed.TotalMilliseconds);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        Check("往返翻页复用整页控件及已加载模板", "regression/page-reuse",
            initialTiles.SequenceEqual(grid.Children.Cast<Button>()), "首页控件身份不变，仍有双页动画",
            "sameTiles=" + initialTiles.SequenceEqual(grid.Children.Cast<Button>()));
        VerifyPageSlide(window, grid, evidence);

        var prepare = typeof(LauncherWindow).GetMethod("PrepareIconDrag", flags)!;
        var finish = typeof(LauncherWindow).GetMethod("FinishIconDrag", flags)!;
        var queue = typeof(LauncherWindow).GetMethod("QueueIconPreview", flags);
        var before = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        Check("鼠标预览按渲染帧合并且保留最后位置", "regression/drag-coalesce", queue is not null,
            "一个渲染帧最多处理一次预览", queue is null ? "每个指针事件直接布局" : "支持合并");
        if (queue is not null)
        {
            prepare.Invoke(window, [grid.Children[0], new Point(20, 20)]);
            var originalOrder = grid.Children.Cast<Button>().Select(t => t.Tag).ToArray();
            var unaffectedTile = (Button)grid.Children[30];
            var unaffectedMotion = unaffectedTile.RenderTransform;
            var burstClock = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
                queue.Invoke(window, [new Point(grid.ActualWidth / 7 * ((i % 6) + .5), grid.ActualHeight / 10), false]);
            var burstMilliseconds = burstClock.Elapsed.TotalMilliseconds;
            var destination = new Point(grid.ActualWidth / 7 * 3.5, grid.ActualHeight / 10);
            queue.Invoke(window, [destination, false]);
            Check("1000个指针事件没有同步重建网格", "regression/drag-coalesce",
                originalOrder.SequenceEqual(grid.Children.Cast<Button>().Select(t => t.Tag)),
                "布局尚未提交，仅最后位置待处理", "orderUnchanged=" + originalOrder.SequenceEqual(grid.Children.Cast<Button>().Select(t => t.Tag)));
            Check("下一帧采用最后位置进行让位", "regression/drag-coalesce",
                PumpUntil(() => Equals(VisualTileOrder(grid)[3].Tag, "wpf-00")
                    && Equals(VisualTileOrder(grid)[0].Tag, "wpf-01"), TimeSpan.FromSeconds(1)),
                "00移到第4格", string.Join(",", VisualTileOrder(grid).Take(4).Select(t => t.Tag)));
            var commits = (long)typeof(LauncherWindow).GetField("_dragPreviewCommits", flags)!.GetValue(window)!;
            Check("一批指针事件只提交一次预览", "regression/drag-coalesce", commits == 1, "1次", commits.ToString());
            Check("让位不重置未移动图标的变换", "regression/drag-layout",
                ReferenceEquals(unaffectedMotion, unaffectedTile.RenderTransform),
                "远处图标保留视觉及运动状态", "motionRetained=" + ReferenceEquals(unaffectedMotion, unaffectedTile.RenderTransform));
            queue.Invoke(window, [new Point(grid.ActualWidth / 7 * 5.5, grid.ActualHeight / 10), true]);
            finish.Invoke(window, null);
            Pump(TimeSpan.FromMilliseconds(300));
            Check("取消拖动同时取消尚未提交的预览", "regression/drag-coalesce",
                originalOrder.SequenceEqual(grid.Children.Cast<Button>().Select(t => t.Tag)) && before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                "原顺序和文件不变", "fileUnchanged=" + before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)));
            File.WriteAllText(Path.Combine(evidence, "rapid-drag.json"), JsonSerializer.Serialize(new
            { burstEvents=1000, burstMilliseconds, commits, previewMilliseconds=typeof(LauncherWindow).GetField("_lastDragPreviewMilliseconds", flags)!.GetValue(window) }));
            prepare.Invoke(window, [grid.Children[0], new Point(20, 20)]);
            window.NavigatePage(1);
            // Wall-clock eligibility can arrive before WPF advances the outgoing clock.
            typeof(LauncherWindow).GetField("_pagePreviewAfter", flags)!.SetValue(window, DateTimeOffset.MinValue);
            Exception? reparentFailure = null;
            try { typeof(LauncherWindow).GetMethod("PreviewIconPlacement", flags)!.Invoke(window, [new Point(20, 20), true]); }
            catch (TargetInvocationException error) { reparentFailure = error.InnerException; }
            Check("跨页让位先释放旧页视觉归属", "regression/drag-ownership", reparentFailure is null,
                "图标能安全进入新页", reparentFailure?.Message ?? "没有控件归属冲突");
            finish.Invoke(window, null);
            window.NavigatePage(-1);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        File.WriteAllText(Path.Combine(evidence, "rapid-pages.json"), JsonSerializer.Serialize(new
        { note="Synchronous call timings are not displayed FPS.", pageTimings }, new JsonSerializerOptions { WriteIndented=true }));
    }
}
