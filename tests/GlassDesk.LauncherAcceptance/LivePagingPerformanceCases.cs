using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Imaging;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static CaptureTraceLog? _isolatedTrace;
    private static void UseIsolatedBackdrop(LauncherWindow window, string executable)
    {
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
        var field = typeof(GlassBackgroundLayer).GetField("_compatible", fields)!;
        ((SharedBackdropFeed)field.GetValue(background)!).Dispose();
        _isolatedTrace = new CaptureTraceLog();
        field.SetValue(background, new SharedBackdropFeed((ScreenRect region, bool _, Action<CaptureTrace>? trace)
            => new RecoveringDesktopSceneCapture(region, trace, executable)) { TraceObserved = _isolatedTrace.Observe });
    }

    private static void VerifyAsyncPageIcons(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string root)
    {
        var path = Path.Combine(root, "page-icon.png");
        byte[] Pixels(byte green, byte red)
        {
            var pixels = new byte[64 * 64 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255; }
            return pixels;
        }
        File.WriteAllBytes(path, PngWriter.EncodeBgra(Pixels(255, 0), 64, 64));
        foreach (var entry in data.Library.Items.ToArray())
        { entry.CustomIconPath = path; data.Library.Update(entry); }
        window.RefreshLibrary();
        Image? Icon() => grid.Children.OfType<Button>().FirstOrDefault()?.Content is StackPanel stack
            && stack.Children[0] is Border border ? border.Child as Image : null;
        Check("冷图标在后台完成后显示真实图片", "performance/icons", PumpUntil(() => Icon()?.Source is BitmapImage, TimeSpan.FromSeconds(3)),
            "后台解码完成，真实Image可见", Icon()?.Source?.GetType().Name ?? "placeholder");
        var first = Icon()?.Source;
        window.NavigatePage(1); Pump(TimeSpan.FromMilliseconds(300));
        window.NavigatePage(-1); Pump(TimeSpan.FromMilliseconds(300));
        Check("再次翻页复用已解码图标", "performance/icons", first is not null && ReferenceEquals(first, Icon()?.Source),
            "同一冻结BitmapImage", "referenceMatches=" + ReferenceEquals(first, Icon()?.Source));
        File.WriteAllBytes(path, PngWriter.EncodeBgra(Pixels(0, 255), 64, 64));
        window.RefreshLibrary();
        bool Red()
        {
            if (Icon()?.Source is not BitmapSource image) return false;
            var pixel = new byte[4]; image.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            return pixel[2] >= 250 && pixel[1] <= 5;
        }
        Check("同路径自定义图标更新后刷新显示新像素", "performance/icons", PumpUntil(Red, TimeSpan.FromSeconds(3)),
            "红色新图片", "red=" + Red());
    }

    private static void VerifyLivePaging(LauncherWindow window, UniformGrid grid, string evidence)
    {
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        Check("完整后台采集流程收到启动台实时背景", "performance/live", PumpUntil(() => surface.BackdropSource == GlassBackdropSource.LiveDesktop,
            TimeSpan.FromSeconds(6)), "LiveDesktop", surface.BackdropSource.ToString());
        var dispatch = new List<double>();
        var heartbeatGaps = new List<double>();
        var previous = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        { var now = Stopwatch.GetTimestamp(); heartbeatGaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now; };
        timer.Start();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var watch = Stopwatch.StartNew();
                window.NavigatePage(i % 2 == 0 ? 1 : -1);
                dispatch.Add(watch.Elapsed.TotalMilliseconds);
                Pump(TimeSpan.FromMilliseconds(280));
            }
        }
        finally { timer.Stop(); }
        Check("连续翻页后内容正确且界面仍响应", "performance/live", grid.Children.Count == 35 && heartbeatGaps.Count > 40,
            "首页35项，输入优先级心跳持续运行", $"items={grid.Children.Count},heartbeats={heartbeatGaps.Count}");
        Check("每次翻页同步处理没有长时间阻塞", "performance/live", dispatch.Max() < 250,
            "同步调用<250ms", $"maxDispatchMs={dispatch.Max():F2}");
        if (_isolatedTrace is not null)
            Check("翻页确实使用隔离采集进程", "performance/live", _isolatedTrace.Snapshot().Counts.Any(c => c.Stage == "BackdropProvider" && c.Reason == "LiveDesktop"),
                "真实IsolatedSharedVisual路径", "native helper trace recorded");
        File.WriteAllText(Path.Combine(evidence, "live-paging-performance.json"), JsonSerializer.Serialize(new
        {
            note = "Real WPF page navigation with the production background worker; dispatcher timings are not displayed FPS. Host build remains the actual host, not an emulated 25H2 OS.",
            dispatchMilliseconds = dispatch, heartbeatGapsMilliseconds = heartbeatGaps,
            capture = _isolatedTrace?.Snapshot()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
