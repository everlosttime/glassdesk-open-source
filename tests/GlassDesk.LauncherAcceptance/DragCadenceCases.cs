using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void WithManualDragTiming(LauncherWindow window, string evidence, Action action, bool inputDeviceDiagnostics = false)
    {
        using var inputProbe = inputDeviceDiagnostics ? new InputDeviceProbe(window, evidence) : null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originField = typeof(LauncherWindow).GetField("_dragOrigin", flags)!;
        var previewField = typeof(LauncherWindow).GetField("_lastDragPreviewMilliseconds", flags)!;
        var commitsField = typeof(LauncherWindow).GetField("_dragPreviewCommits", flags)!;
        var capturedDragField = typeof(LauncherWindow).GetField("_capturedIconDrag", flags);
        var oleDragField = typeof(LauncherWindow).GetField("_oleIconDrag", flags);
        string InputMode() => capturedDragField?.GetValue(window) is true ? "capture"
            : oleDragField?.GetValue(window) is true ? "ole" : "preview";
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        var opticsField = typeof(LiquidGlassSurface).GetField("_lastOpticsMilliseconds", flags)!;
        var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", flags)!.GetValue(surface)!;
        var rows = new List<object>();
        var inputHeartbeat = new List<object>();
        long lastInputTicks = 0;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) =>
        {
            if (originField.GetValue(window) is null) { lastInputTicks = 0; return; }
            var now = Stopwatch.GetTimestamp();
            if (lastInputTicks != 0 && inputHeartbeat.Count < 20000)
            {
                var captured = background.LastCaptureTiming;
                inputHeartbeat.Add(new
                {
                    atTicks = now, inputMode = InputMode(), gapMilliseconds = Stopwatch.GetElapsedTime(lastInputTicks, now).TotalMilliseconds,
                    opticsMilliseconds = (double)opticsField.GetValue(surface)!, backdropSource = surface.BackdropSource.ToString(),
                    sourceAgeMilliseconds = captured is null ? 0 : Stopwatch.GetElapsedTime(captured.SourceTicks, now).TotalMilliseconds,
                    acquisitionMilliseconds = captured is null ? 0 : Stopwatch.GetElapsedTime(captured.StartTicks, captured.EndTicks).TotalMilliseconds
                });
            }
            lastInputTicks = now;
        };
        object? previousOrigin = null;
        TimeSpan? previousRenderingTime = null;
        long previousTicks = 0;
        var session = 0;
        EventHandler sample = (_, e) =>
        {
            var origin = originField.GetValue(window);
            if (origin is null) { previousOrigin = null; previousTicks = 0; previousRenderingTime = null; return; }
            if (!ReferenceEquals(origin, previousOrigin))
            {
                previousOrigin = origin;
                previousTicks = 0;
                previousRenderingTime = null;
                session++;
            }
            if (e is not RenderingEventArgs frame || previousRenderingTime == frame.RenderingTime) return;
            var now = Stopwatch.GetTimestamp();
            rows.Add(new
            {
                session, atTicks = now, inputMode = InputMode(), renderingTimeMilliseconds = frame.RenderingTime.TotalMilliseconds,
                callbackGapMilliseconds = previousTicks == 0 ? 0 : Stopwatch.GetElapsedTime(previousTicks, now).TotalMilliseconds,
                renderingTimeGapMilliseconds = previousRenderingTime is { } last ? (frame.RenderingTime - last).TotalMilliseconds : 0,
                previewCommits = (long)commitsField.GetValue(window)!,
                lastPreviewMilliseconds = (double)previewField.GetValue(window)!,
                gen0Collections = GC.CollectionCount(0), gen1Collections = GC.CollectionCount(1), gen2Collections = GC.CollectionCount(2)
            });
            previousTicks = now;
            previousRenderingTime = frame.RenderingTime;
        };
        CompositionTarget.Rendering += sample;
        heartbeat.Start();
        try { action(); }
        finally
        {
            CompositionTarget.Rendering -= sample;
            heartbeat.Stop();
            File.WriteAllText(Path.Combine(evidence, "manual-drag-timing.json"), JsonSerializer.Serialize(new
            {
                note = "Unique WPF RenderingTime callbacks while actual drag sessions are active; includes pickup and settling. May sample production fields from the previous callback. Does not measure displayed FPS.",
                sessions = session, samples = rows.Count, rows,
                inputHeartbeatNote = "Input-priority dispatcher heartbeat during actual drag, plus latest background worker metrics; not displayed FPS. At most 20000 heartbeat rows.",
                inputHeartbeat
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void VerifyDragCadence(LauncherWindow window, UniformGrid grid, string evidence)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var prepare = typeof(LauncherWindow).GetMethod("PrepareIconDrag", flags)!;
        var finish = typeof(LauncherWindow).GetMethod("FinishIconDrag", flags)!;
        var pointer = typeof(LauncherWindow).GetMethod("UpdateDragPointer", flags)!;
        var queue = typeof(LauncherWindow).GetMethod("QueueIconPreview", flags)!;
        var commit = typeof(LauncherWindow).GetMethod("CommitQueuedIconPreview", flags)!;
        var commits = typeof(LauncherWindow).GetField("_dragPreviewCommits", flags)!;
        var overlay = (Canvas)window.FindName("DragOverlay")!;
        var cursor = DisplayService.GetCursorPixelPoint();
        var arrangeInvalidations = 0;
        try
        {
            var first = (Button)grid.Children[0];
            var fixture = new UniformGrid { Rows = 1, Columns = 2 };
            fixture.Children.Add(new Button { Tag = "left", RenderTransform = new TranslateTransform(0, .2) });
            fixture.Children.Add(new Button { Tag = "right" });
            fixture.Measure(new Size(200, 100));
            fixture.Arrange(new Rect(0, 0, 200, 100));
            Check("同行亚像素位置差不应误报图标排列错误", "regression/visual-sort",
                Equals(VisualTileOrder(fixture)[0].Tag, "left"), "第一格仍为left", VisualTileOrder(fixture)[0].Tag!.ToString()!);
            prepare.Invoke(window, [first, new Point(20, 20)]);
            Pump(TimeSpan.FromMilliseconds(80));
            var ghost = (Button)overlay.Children[0];
            var start = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 2));
            IosInput.SetCursorPos((int)start.X, (int)start.Y);
            pointer.Invoke(window, null);
            overlay.UpdateLayout();
            var before = ghost.TranslatePoint(new Point(20, 20), overlay);
            var lastScreen = new Point((int)start.X + 30, (int)start.Y + 15);
            IosInput.SetCursorPos((int)lastScreen.X, (int)lastScreen.Y);
            pointer.Invoke(window, null);
            var after = ghost.TranslatePoint(new Point(20, 20), overlay);
            var expectedDelta = overlay.PointFromScreen(lastScreen) - overlay.PointFromScreen(new Point((int)start.X, (int)start.Y));
            Check("跟手位置在当前回调立即更新而非等待布局", "regression/ghost-follow",
                ((after - before) - expectedDelta).Length < 1,
                "立即移动30x15物理像素对应的DIP", $"actual={after - before}, expected={expectedDelta}");
            for (var i = 0; i < 120; i++)
            {
                IosInput.SetCursorPos((int)start.X + i % 31, (int)start.Y + i % 19);
                pointer.Invoke(window, null);
                if (!overlay.IsArrangeValid) arrangeInvalidations++;
                overlay.UpdateLayout();
            }
            Check("连续跟手更新不使浮层重新布局", "regression/ghost-follow",
                arrangeInvalidations == 0, "0次布局失效", arrangeInvalidations.ToString());
            finish.Invoke(window, null);
            Pump(TimeSpan.FromMilliseconds(300));
            prepare.Invoke(window, [grid.Children[0], new Point(20, 20)]);
            var frame = (RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs), flags,
                null, [TimeSpan.FromMilliseconds(1)], null)!;
            queue.Invoke(window, [new Point(grid.ActualWidth / 7 * 3.5, grid.ActualHeight / 10), false]);
            commit.Invoke(window, [null, frame]);
            queue.Invoke(window, [new Point(grid.ActualWidth / 7 * 4.5, grid.ActualHeight / 10), false]);
            commit.Invoke(window, [null, frame]);
            var firstFrameCommits = (long)commits.GetValue(window)!;
            Check("同一RenderingTime的重复回调不重复提交预览", "regression/drag-frame",
                firstFrameCommits == 1, "同一渲染时刻只提交一次", firstFrameCommits.ToString());
            var nextFrame = (RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs), flags,
                null, [TimeSpan.FromMilliseconds(8)], null)!;
            commit.Invoke(window, [null, nextFrame]);
            var secondFrameCommits = (long)commits.GetValue(window)!;
            Check("下一渲染时刻保留待处理的最新指针位置", "regression/drag-frame",
                secondFrameCommits == 2, "下一渲染时刻提交保留的位置", secondFrameCommits.ToString());
        }
        finally
        {
            finish.Invoke(window, null);
            if (cursor is { } p) IosInput.SetCursorPos(p.X, p.Y);
        }
        File.WriteAllText(Path.Combine(evidence, "drag-cadence.json"), JsonSerializer.Serialize(new
        { ghostMoves = 120, arrangeInvalidations, note = "Input/visual/layout contracts; not displayed FPS." }));
    }
}
