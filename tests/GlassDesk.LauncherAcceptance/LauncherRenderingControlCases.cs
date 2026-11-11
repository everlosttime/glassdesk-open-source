using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Platform.Displays;

internal static partial class Program
{
    private static int ObserveLauncherRenderingControl(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var results = new List<object>();
        try
        {
            foreach (var (width, transparent, secondTarget) in new[] { (500d, true, 0), (1780d, true, 0), (1780d, false, 0), (1780d, true, 1), (1780d, true, 2) })
            {
                var rectangle = new System.Windows.Shapes.Rectangle { Width = 180, Height = 100,
                    RadiusX = 25, RadiusY = 25, Fill = Brushes.CornflowerBlue };
                var motion = new TranslateTransform(); rectangle.RenderTransform = motion;
                var secondMotion = new TranslateTransform();
                Window? second = secondTarget == 0 ? null : new Window
                {
                    Width = width, Height = width * .56, Left = 50, Top = 50, Opacity = .45,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                    Background = Brushes.Transparent, Topmost = true, ShowActivated = false,
                    Content = new Grid { Children = { new System.Windows.Shapes.Rectangle { Width = 200, Height = 120,
                        RadiusX = 25, RadiusY = 25, Fill = Brushes.Black, RenderTransform = secondMotion } } }
                };
                var window = new Window { Width = width, Height = width * .56, Left = 40, Top = 40,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = transparent,
                    Background = transparent ? Brushes.Transparent : Brushes.Black,
                    Topmost = true, Content = new Grid { Children = { rectangle } } };
                var stamps = new List<long>(); var previous = TimeSpan.MinValue; var raw = 0;
                var started = Stopwatch.GetTimestamp();
                EventHandler rendering = (_, e) =>
                {
                    raw++;
                    if (e is not RenderingEventArgs frame || frame.RenderingTime == previous) return;
                    previous = frame.RenderingTime; var now = Stopwatch.GetTimestamp(); stamps.Add(now);
                    motion.X = Math.Sin(Stopwatch.GetElapsedTime(started, now).TotalSeconds * 4) * 100;
                    if (secondTarget == 2) secondMotion.X = motion.X;
                };
                second?.Show(); window.Show(); Pump(TimeSpan.FromMilliseconds(300));
                using var process = Process.GetCurrentProcess();
                var before = process.Threads.Cast<ProcessThread>().ToDictionary(t => t.Id, t => t.TotalProcessorTime.TotalMilliseconds);
                CompositionTarget.Rendering += rendering;
                try { Pump(TimeSpan.FromSeconds(3)); }
                finally { CompositionTarget.Rendering -= rendering; }
                process.Refresh();
                var threads = process.Threads.Cast<ProcessThread>().Where(t => before.ContainsKey(t.Id))
                    .Select(t => new { id = t.Id, cpuMilliseconds = t.TotalProcessorTime.TotalMilliseconds - before[t.Id], startAddress = t.StartAddress.ToInt64() }).ToArray();
                results.Add(new { width, transparent, secondTarget, rawCallbacks = raw, callbacks = stamps.Count,
                    refresh = DisplayService.RefreshForWindow(new WindowInteropHelper(window).Handle),
                    gaps = stamps.Zip(stamps.Skip(1), (a,b) => Stopwatch.GetElapsedTime(a,b).TotalMilliseconds).ToArray(), threads });
                window.Close(); second?.Close();
            }
        }
        finally { app.Shutdown(); }
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { note = "Bare WPF rectangle: no GlassDesk window, backdrop, filter, icons, shadow, or custom geometry. Callbacks are not displayed frames.", results }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
