using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;

internal static partial class Program
{
    private static void VerifyOleRenderWake(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        var tile = (Button)grid.Children[0];
        var overlay = (Canvas)window.FindName("DragOverlay")!;
        var tested = false;
        var scheduled = false;
        EventHandler started = (_, _) =>
        {
            if (scheduled) return;
            scheduled = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
            Thread.Sleep(12); // Past the feedback sampling interval, after the initial render completed.
            tested = true;
            var ghost = (Button)overlay.Children[0];
            overlay.UpdateLayout();
            var grab = (Point)typeof(LauncherWindow).GetField("_grabPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var before = ghost.TranslatePoint(grab, overlay);
            var initial = overlay.PointToScreen(before);
            var savedCursor = DisplayService.GetCursorPixelPoint();
            var destination = new Point((int)initial.X + 33, (int)initial.Y + 17);
            try
            {
            IosInput.SetCursorPos((int)destination.X, (int)destination.Y);

            // Characterize WPF's actual pending render operation at Input priority.
            // Only this test accesses WPF internals; product code uses public visual updates.
            var mediaType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
            var media = mediaType.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window.Dispatcher]);
            mediaType.GetMethod("PostRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(media, null);
            var operation = (DispatcherOperation)mediaType.GetField("_currentRenderOp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media)!;
            operation.Priority = DispatcherPriority.Input;
            var commitField = typeof(LauncherWindow).GetField("_dragPreviewCommits", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var commits = (long)commitField.GetValue(window)!;
            var feedback = (GiveFeedbackEventArgs)Activator.CreateInstance(typeof(GiveFeedbackEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [DragDropEffects.Move, false], null)!;
            feedback.RoutedEvent = DragDrop.GiveFeedbackEvent;
            tile.RaiseEvent(feedback);
            var after = ghost.TranslatePoint(grab, overlay);
            var expected = overlay.PointFromScreen(destination) - before;
            Check("渲染尚未推进时OLE反馈仍读取最新跟手位置", "regression/ole-render-wake",
                ((after - before) - expected).Length < 1,
                "33x17物理像素对应的DIP已更新", $"actual={after-before},expected={expected}");
            Check("跟手视觉更新唤醒被降到Input优先级的真实WPF渲染任务", "regression/ole-render-wake",
                operation.Priority >= DispatcherPriority.Render,
                "待处理渲染任务恢复Render或更高优先级", operation.Priority.ToString());
            Check("OLE反馈不提交排序预览也不使浮层重新布局", "regression/ole-render-wake",
                (long)commitField.GetValue(window)! == commits && overlay.IsArrangeValid,
                "预览提交数不变，布局有效", $"commits={(long)commitField.GetValue(window)!-commits},arrange={overlay.IsArrangeValid}");
            }
            finally { if (savedCursor is { } p) IosInput.SetCursorPos(p.X, p.Y); }
            }));
        };
        window.InternalDragStarted += started;
        try { VerifyIosNativeInput(window, grid, data, evidence); }
        finally { window.InternalDragStarted -= started; }
        Check("调度回归检查经过真实长按拾起路径", "regression/ole-render-wake", tested, "拾起事件触发", tested.ToString());
    }
}
