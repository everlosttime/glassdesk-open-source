using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyHighRefreshAnimation(LauncherWindow window, string evidence, bool highRefreshSetting = false)
    {
        typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, 1);
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
        var display = DisplayService.Primary()!;
        var source = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
        var samples = new List<object>();
        var expectedInterval = highRefreshSetting || Environment.GetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ") == "1" ? 1000d / 120 : 16;
        // Exclude helper initialization from the steady acquisition comparison.
        var liveCapturePrimed = PumpUntil(() => Acquisitions() >= 6, TimeSpan.FromSeconds(10));
        window.Hide();
        window.ContainerAnchorProvider = () => source;
        for (var cycle = 0; cycle < 6; cycle++)
        {
            Measure("open", cycle, () => window.ShowWithTransition(source), () => window.CanNavigatePages);
            Measure("close", cycle, window.CloseWithTransition, () => !window.IsVisible);
        }
        window.ShowWithTransition(source); Pump(TimeSpan.FromMilliseconds(80));
        window.CloseWithTransition();
        Check("高刷动画中途反向后完整收起", "animation120/reverse",
            PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(2)), "hidden", window.IsVisible.ToString());
        Check("中途反向完成后恢复默认捕获节奏", "animation120/reverse",
            Interval() == 16, "16ms", Interval().ToString("0.00"));
        File.WriteAllText(Path.Combine(evidence, "high-refresh-animation.json"), JsonSerializer.Serialize(new
        {
            targetCaptureHz = 1000 / expectedInterval,
            note = "Unique WPF rendering callbacks and acquisition counters, not GPU-presented FPS. PresentMon ETW access was denied on this host.",
            processId = Environment.ProcessId, liveCapturePrimed, display, samples,
        }, new JsonSerializerOptions { WriteIndented = true }));

        double Interval()
        {
            var live = JsonSerializer.SerializeToElement(surface.InspectPerformance()).GetProperty("capture").GetProperty("live");
            return live.TryGetProperty("captureIntervalMilliseconds", out var interval) ? interval.GetDouble() : 16;
        }
        long Acquisitions() => JsonSerializer.SerializeToElement(surface.InspectPerformance())
            .GetProperty("capture").GetProperty("live").GetProperty("captureAcquisitions").GetInt64();
        void Measure(string direction, int cycle, Action start, Func<bool> completed)
        {
            var stamps = new List<long>(); var last = TimeSpan.MinValue;
            EventHandler onFrame = (_, e) =>
            {
                if (e is not RenderingEventArgs frame || frame.RenderingTime == last || !window.IsVisible || window.CanNavigatePages) return;
                last = frame.RenderingTime; stamps.Add(Stopwatch.GetTimestamp());
            };
            var acquisitions = Acquisitions();
            CompositionTarget.Rendering += onFrame;
            var watch = Stopwatch.StartNew();
            try
            {
                start(); var dispatchMs = watch.Elapsed.TotalMilliseconds; var activeInterval = Interval();
                var settled = PumpUntil(completed, TimeSpan.FromSeconds(2));
                var elapsedMs = watch.Elapsed.TotalMilliseconds;
                var gaps = stamps.Zip(stamps.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).Order().ToArray();
                samples.Add(new
                {
                    direction, cycle, settled, dispatchMs, elapsedMs, activeCaptureIntervalMs = activeInterval,
                    restingCaptureIntervalMs = Interval(), acquisitions = Acquisitions() - acquisitions,
                    renderingCallbacks = stamps.Count,
                    medianGapMs = gaps.Length == 0 ? 0 : gaps[gaps.Length / 2],
                    p95GapMs = gaps.Length == 0 ? 0 : gaps[(int)((gaps.Length - 1) * .95)],
                    maxGapMs = gaps.Length == 0 ? 0 : gaps[^1],
                });
                Check($"第{cycle + 1}轮{direction}完成并恢复捕获节奏", "animation120/lifecycle",
                    settled && Interval() == 16, "completed; 16ms", $"{settled}; {Interval():0.00}ms");
                Check($"第{cycle + 1}轮{direction}使用所选捕获预算", "animation120/cadence",
                    Math.Abs(activeInterval - expectedInterval) < .01,
                    expectedInterval.ToString("0.00") + "ms", activeInterval.ToString("0.00"));
            }
            finally { CompositionTarget.Rendering -= onFrame; }
        }
    }
}
