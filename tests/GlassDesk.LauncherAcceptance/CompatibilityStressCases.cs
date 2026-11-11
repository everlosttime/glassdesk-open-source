using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyCompatibilityStress(LauncherWindow window, TextBlock pageText, string evidence)
    {
        var timings = new List<object>();
        var display = window.PrimaryDisplaySession!.Display;
        var anchor = new GlassDesk.Platform.Displays.PixelRect(display.WorkArea.X+24, display.WorkArea.Bottom-88,48,64);
        var motion = typeof(LauncherWindow).GetField("_transformActive", BindingFlags.Instance|BindingFlags.NonPublic)!;
        for (var i=0; i<100; i++)
        {
            var clock = Stopwatch.StartNew();
            window.CloseWithTransition();
            var closed = PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(2));
            window.ShowWithTransition(anchor);
            var opened = PumpUntil(() => window.IsVisible && motion.GetValue(window) is false, TimeSpan.FromSeconds(2));
            Check($"启动台开关 {i+1}/100", "compatibility/stress-toggle", closed && opened,
                "隐藏完成并恢复可交互窗口", $"closed={closed},opened={opened}");
            timings.Add(new {operation="toggle",index=i+1,milliseconds=clock.Elapsed.TotalMilliseconds});
        }
        var pageFailures=0;
        for (var i=0; i<200; i++)
        {
            var clock = Stopwatch.StartNew();
            var forward = i%2 == 0;
            window.NavigatePage(forward ? 1 : -1);
            // Let the actual page animation and its input boundary complete.
            Pump(TimeSpan.FromMilliseconds(400));
            var expected = forward ? "2 / 2" : "1 / 2";
            var passed = pageText.Text == expected;
            if (!passed) pageFailures++;
            Check($"翻页 {i+1}/200", "compatibility/stress-page", passed, expected, pageText.Text);
            timings.Add(new {operation="page",index=i+1,milliseconds=clock.Elapsed.TotalMilliseconds});
        }
        var surface=(LiquidGlassSurface)window.FindName("GlassSurface")!;
        File.WriteAllText(Path.Combine(evidence,"compatibility-stress.json"),JsonSerializer.Serialize(new
        {
            environment=Environment.OSVersion.VersionString,toggles=100,pages=200,pageFailures,
            windowVisible=window.IsVisible,backgroundMode=surface.BackdropSource.ToString(),timings,
            note="Production components and routed actions on this host. This does not measure displayed FPS or certify physical lock/sleep/device recovery."
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}
