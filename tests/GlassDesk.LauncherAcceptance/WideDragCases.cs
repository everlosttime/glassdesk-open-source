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
using GlassDesk.UI.Theming;

internal static partial class Program
{
    // Visual position is the user-facing order; Children order is an implementation detail during pickup.
    private static Button[] VisualTileOrder(UniformGrid grid) => grid.Children.Cast<Button>()
        .OrderBy(tile =>
        {
            var center = tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2), grid);
            var row = Math.Clamp((int)(center.Y / (grid.ActualHeight / grid.Rows)), 0, grid.Rows - 1);
            var column = Math.Clamp((int)(center.X / (grid.ActualWidth / grid.Columns)), 0, grid.Columns - 1);
            return row * grid.Columns + column;
        }).ToArray();

    private static void VerifyWideDrag(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var prepare = typeof(LauncherWindow).GetMethod("PrepareIconDrag", flags)!;
        var preview = typeof(LauncherWindow).GetMethod("PreviewIconPlacement", flags)!;
        var finish = typeof(LauncherWindow).GetMethod("FinishIconDrag", flags)!;
        var original = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var tiles = grid.Children.Cast<Button>().ToArray();
        var synchronousLayouts = 0;
        var insidePreview = false;
        EventHandler layout = (_, _) => { if (insidePreview) synchronousLayouts++; };
        grid.LayoutUpdated += layout;
        var timings = new List<double>();
        var gaps = new List<double>();
        long previousFrame = 0;
        EventHandler frame = (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (previousFrame != 0) gaps.Add(Stopwatch.GetElapsedTime(previousFrame, now).TotalMilliseconds);
            previousFrame = now;
        };
        try
        {
            prepare.Invoke(window, [tiles[0], new Point(20, 20)]);
            Pump(TimeSpan.FromMilliseconds(80));
            CompositionTarget.Rendering += frame;
            // Alternating full-page jumps, row crossings, and reversals while displacement clocks are active.
            int[] destinations = [34, 0, 28, 6, 33, 1, 21, 13, 34, 2, 27, 7];
            for (var i = 0; i < 120; i++)
            {
                var index = destinations[i % destinations.Length];
                var point = new Point(grid.ActualWidth / 7 * (index % 7 + .5), grid.ActualHeight / 5 * (index / 7 + .5));
                insidePreview = true;
                var started = Stopwatch.GetTimestamp();
                preview.Invoke(window, [point, false]);
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                insidePreview = false;
                Pump(TimeSpan.FromMilliseconds(17));
            }
            Check("整页反复顺延不在预览中强制同步布局", "regression/wide-drag",
                synchronousLayouts == 0, "0次同步布局", synchronousLayouts.ToString());
            preview.Invoke(window, [new Point(grid.ActualWidth / 7 * 6.5, grid.ActualHeight / 5 * 4.5), false]);
            Pump(TimeSpan.FromMilliseconds(300));
            var order = VisualTileOrder(grid);
            Check("34个图标顺延后视觉排列正确且复用原控件", "regression/wide-drag",
                order.Take(34).Select(t => t.Tag).SequenceEqual(Enumerable.Range(1, 34).Select(i => (object)$"wpf-{i:00}"))
                    && Equals(order[34].Tag, "wpf-00") && order.ToHashSet().SetEquals(tiles),
                "01至34、00，没有控件丢失或重复", string.Join(",", order.Select(t => t.Tag)));
            Check("连续120次大范围顺延不写盘", "regression/wide-drag",
                original.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)), "原布局字节不变", "核对字节");
            Capture(window, Path.Combine(evidence, "wide-drag-settled.png"));
            finish.Invoke(window, null);
            Pump(TimeSpan.FromMilliseconds(300));
            Check("整页顺延取消恢复视觉排列与持久化布局", "regression/wide-drag",
                VisualTileOrder(grid).Select(t => t.Tag).SequenceEqual(tiles.Select(t => t.Tag))
                    && original.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                "00至34，原文件不变", string.Join(",", VisualTileOrder(grid).Select(t => t.Tag)));
            prepare.Invoke(window, [grid.Children[0], new Point(20, 20)]);
            preview.Invoke(window, [new Point(grid.ActualWidth / 7 * 6.5, grid.ActualHeight / 5 * 4.5), false]);
            Pump(TimeSpan.FromMilliseconds(65));
            var beforeReverse = tiles[7].TranslatePoint(new Point(0, 0), grid);
            preview.Invoke(window, [new Point(grid.ActualWidth / 14, grid.ActualHeight / 10), false]);
            var afterReverse = tiles[7].TranslatePoint(new Point(0, 0), grid);
            Check("顺延中反向拖动从当前视觉位置继续", "regression/wide-drag",
                (beforeReverse - afterReverse).Length < 1,
                "重定向瞬间跳变小于1 DIP", (beforeReverse - afterReverse).Length.ToString("F3"));
            finish.Invoke(window, null);
            Pump(TimeSpan.FromMilliseconds(300));

            data.Settings.Current.Appearance.ReduceMotion = true;
            ThemeService.Current!.Update(data.Settings.Current.Appearance);
            prepare.Invoke(window, [grid.Children[0], new Point(20, 20)]);
            preview.Invoke(window, [new Point(grid.ActualWidth / 7 * 6.5, grid.ActualHeight / 5 * 4.5), true]);
            var swapped = VisualTileOrder(grid);
            Check("减少动画时整页交换立即呈现且中间图标不动", "regression/wide-drag",
                Equals(swapped[0].Tag, "wpf-34") && Equals(swapped[34].Tag, "wpf-00")
                    && swapped.Skip(1).Take(33).Select(t => t.Tag).SequenceEqual(Enumerable.Range(1, 33).Select(i => (object)$"wpf-{i:00}")),
                "34、01至33、00", string.Join(",", swapped.Select(t => t.Tag)));
            finish.Invoke(window, null);
            data.Settings.Current.Appearance.ReduceMotion = false;
            ThemeService.Current!.Update(data.Settings.Current.Appearance);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            insidePreview = false;
            grid.LayoutUpdated -= layout;
            CompositionTarget.Rendering -= frame;
            finish.Invoke(window, null);
        }
        var sorted = timings.Order().ToArray();
        File.WriteAllText(Path.Combine(evidence, "wide-drag.json"), JsonSerializer.Serialize(new
        {
            note = "120 full-page/reversal previews with active animations. CPU preview and WPF callback gaps are not displayed FPS.",
            previews = timings.Count, synchronousLayouts, meanMilliseconds = timings.Average(),
            p95Milliseconds = sorted[(int)((sorted.Length - 1) * .95)], maxMilliseconds = sorted[^1],
            previewMilliseconds = timings, renderingCallbackGapsMilliseconds = gaps
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void VerifyWideDragInput(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence, bool irregular = false, bool sustained = false, bool highRatePackets = false)
    {
        window.Activate();
        var original = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var canvas = (Canvas)window.FindName("DragOverlay")!;
        var screenOrigin = grid.PointToScreen(new Point(0, 0));
        var screenSize = grid.PointToScreen(new Point(grid.ActualWidth, grid.ActualHeight)) - screenOrigin;
        var points = Enumerable.Range(0, 35).Select(i => grid.PointToScreen(new Point(
            grid.ActualWidth / 7 * (i % 7 + .5), grid.ActualHeight / 5 * (i / 7 + .5)))).ToArray();
        void Move(int i) => IosInput.SetCursorPos((int)points[i].X, (int)points[i].Y);
        foreach (var key in new[] { 0x11, 0x10, 0x12, 0x1b }) IosInput.Key((ushort)key, true);
        var previewSamples = new List<double>();
        var gaps = new List<double>();
        var renderingTimes = new List<double>();
        var inputPath = new List<object>();
        var movementActive = false;
        TimeSpan? previousRenderingTime = null;
        TimeSpan? previousMovementRenderingTime = null;
        var previousIndex = -1;
        double[] settledErrors = [];
        long previousFrame = 0, lastCommit = 0;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var commits = typeof(LauncherWindow).GetField("_dragPreviewCommits", flags)!;
        var elapsed = typeof(LauncherWindow).GetField("_lastDragPreviewMilliseconds", flags)!;
        var previewIndex = typeof(LauncherWindow).GetField("_previewIndex", flags)!;
        EventHandler frame = (_, e) =>
        {
            if (canvas.Children.Count == 0) return;
            if (e is RenderingEventArgs rendering)
            {
                if (previousRenderingTime == rendering.RenderingTime) return;
                previousRenderingTime = rendering.RenderingTime;
                if (Volatile.Read(ref movementActive))
                {
                    if (previousMovementRenderingTime is { } lastTime)
                        renderingTimes.Add((rendering.RenderingTime - lastTime).TotalMilliseconds);
                    previousMovementRenderingTime = rendering.RenderingTime;
                }
            }
            var now = Stopwatch.GetTimestamp();
            if (Volatile.Read(ref movementActive))
            {
                if (previousFrame != 0) gaps.Add(Stopwatch.GetElapsedTime(previousFrame, now).TotalMilliseconds);
                previousFrame = now;
            }
            var count = (long)commits.GetValue(window)!;
            var index = (int)previewIndex.GetValue(window)!;
            if (count != lastCommit)
            {
                if (index != previousIndex && Volatile.Read(ref movementActive))
                    previewSamples.Add((double)elapsed.GetValue(window)!);
                lastCommit = count;
                previousIndex = index;
            }
        };
        try
        {
            IosInput.Mouse(4);
            Move(0);
            IosInput.Mouse(2);
            var script = Task.Run(async () =>
            {
                try
                {
                    if (highRatePackets) IosInput.timeBeginPeriod(1);
                    await Task.Delay(550);
                    Volatile.Write(ref movementActive, true);
                    int[] path = [34, 1, 28, 6, 33, 2, 21, 13];
                    var count = highRatePackets ? 6000 : sustained ? 2400 : irregular ? 480 : 120;
                    for (var i = 0; i < count; i++)
                    {
                        if (irregular)
                        {
                            var t = i * .091;
                            var x = .5 + 6 * (.5 + .5 * Math.Sin(t * 1.7 + 1.3 * Math.Sin(t * .37)));
                            var y = .5 + 3.9 * (.5 + .5 * Math.Sin(t * 2.3 + 1.7 * Math.Cos(t * .61)));
                            // Deterministic discontinuities add abrupt, non-periodic reversals to the curves.
                            if (i % 47 == 0) { x = i % 94 == 0 ? 6.5 : .5; y = i % 141 == 0 ? 4.5 : .5; }
                            var p = screenOrigin + new Vector(screenSize.X / 7 * x, screenSize.Y / 5 * y);
                            if (highRatePackets) IosInput.MovePacket(p);
                            else IosInput.SetCursorPos((int)p.X, (int)p.Y);
                            inputPath.Add(new { step = i, column = x, row = y });
                        }
                        else Move(path[i % path.Length]);
                        await Task.Delay(highRatePackets ? 1 : irregular ? 4 : 24);
                    }
                    Move(34);
                    Volatile.Write(ref movementActive, false);
                    await Task.Delay(400);
                    await window.Dispatcher.InvokeAsync(() =>
                    {
                        Check(highRatePackets ? $"注入{count}个不合并鼠标移动包保持OLE拖动会话" : irregular ? $"真实鼠标{count}次曲线及突然反向移动保持OLE拖动会话" : "真实鼠标120次跨行大范围移动保持OLE拖动会话", "IOS-INPUT",
                            canvas.Children.Count == 1 && previewSamples.Count >= 10,
                            "浮动图标仍在，多个渲染帧提交预览", $"ghost={canvas.Children.Count}, commits={previewSamples.Count}");
                        var order = VisualTileOrder(grid);
                        settledErrors = order.Select((tile, i) =>
                        {
                            var center = tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2), grid);
                            var target = new Point(grid.ActualWidth / 7 * (i % 7 + .5), grid.ActualHeight / 5 * (i / 7 + .5));
                            return (center - target).Length;
                        }).ToArray();
                        Check("真实鼠标整页顺延最终视觉排列正确且未写盘", "IOS-INPUT",
                            order.Take(34).Select(t => t.Tag).SequenceEqual(Enumerable.Range(1, 34).Select(i => (object)$"wpf-{i:00}"))
                                && Equals(order[34].Tag, "wpf-00") && settledErrors.All(error => error < 1)
                                && original.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                            "01至34、00，尚未保存", string.Join(",", order.Select(t => t.Tag)));
                        Capture(window, Path.Combine(evidence, "native-wide-drag.png"));
                    });
                }
                finally
                {
                    if (highRatePackets) IosInput.timeEndPeriod(1);
                    Volatile.Write(ref movementActive, false);
                    IosInput.Mouse(4);
                }
            });
            CompositionTarget.Rendering += frame;
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(sustained ? 45 : irregular ? 20 : 8));
            script.GetAwaiter().GetResult();
            Pump(TimeSpan.FromMilliseconds(500));
            var saved = GlassDesk.Core.Models.LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("真实整页顺延松手后保存并清除浮层", "IOS-INPUT",
                saved.Take(34).SequenceEqual(Enumerable.Range(1, 34).Select(i => $"wpf-{i:00}"))
                    && saved[34] == "wpf-00" && saved[35] == "wpf-35" && canvas.Children.Count == 0,
                "01至34、00、35，浮层清空", string.Join(",", saved));
        }
        finally
        {
            CompositionTarget.Rendering -= frame;
            IosInput.Mouse(4);
            foreach (var key in new[] { 0x11, 0x10, 0x12, 0x1b }) IosInput.Key((ushort)key, true);
        }
        File.WriteAllText(Path.Combine(evidence, sustained ? "sustained-drag-input.json" : irregular ? "irregular-drag-input.json" : "wide-drag-input.json"), JsonSerializer.Serialize(new
        {
            note = "Actual SendInput through production mouse capture. Unique RenderingTime samples during active movement exclude pickup, settle and screenshot. Preview samples include changed slots only. These are not displayed FPS.",
            irregular, sustained, highRatePackets, inputMovements = highRatePackets ? 6000 : sustained ? 2400 : irregular ? 480 : 120,
            previews = previewSamples.Count, previewMilliseconds = previewSamples, renderingCallbackGapsMilliseconds = gaps,
            renderingTimeGapsMilliseconds = renderingTimes, settledCellErrorsDip = settledErrors, inputPath
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (highRatePackets)
        {
            Check("持续高频不合并鼠标包期间保持渲染推进", "regression/high-rate-drag",
                renderingTimes.Count >= 100 && renderingTimes.Max() < 250,
                "至少100次不同渲染时刻，最大间隔小于250ms", $"frames={renderingTimes.Count},maxGap={(renderingTimes.Count == 0 ? 0 : renderingTimes.Max()):F3}ms");
        }
    }
}
