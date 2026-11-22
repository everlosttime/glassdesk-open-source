using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int ObserveGlassBackdropCadence(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report is required."));
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var desktop = args.Contains("--fixed-desktop");
        var frosted = args.Contains("--frosted");
        var secondary = args.Contains("--secondary-display");
        if (secondary && MaterialMemoryNative.SetThreadDpiAwarenessContext(-4) == 0)
            throw new InvalidOperationException("Unable to read the selected display in physical pixels.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        using var theme = ThemeService.Initialize(app.Dispatcher);
        theme.Update(new AppearanceSettings { PinBarsToDesktop = desktop, UseSystemBackdrop = true });
        var display = (secondary ? DisplayService.Enumerate().SingleOrDefault(d => !d.IsPrimary) : DisplayService.Primary())
            ?? throw new InvalidOperationException("The requested test display is unavailable.");
        using var surface = new LiquidGlassSurface { UseFrostedMaterial = frosted, TransparentWhileLoading = true };
        var window = new CadenceGlassWindow
        {
            Width = 680, Height = 84, ShowInTaskbar = false, ShowActivated = false, Content = surface,
        };
        // The product host selects Windows Primary. Bind this isolated fixture
        // to the requested output so the normal material code clips to it.
        window.BindPrimaryDisplay(new PrimaryDisplaySession(display with { IsPrimary = true }));
        var bounds = new Rect(0, 0, window.Width, window.Height);
        surface.SetContainerTransitionBounds(bounds);
        surface.SetPanelShape(bounds, 16);
        Process? fixture = null;
        try
        {
            if (!desktop)
            {
                var executable = Argument(args, "--moving-fixture") ?? throw new ArgumentException("--moving-fixture is required.");
                var ready = report + ".source.json";
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--coded-backdrop-fixture");
                start.ArgumentList.Add(ready);
                if (secondary) start.ArgumentList.Add(display.DeviceName);
                fixture = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the moving source.");
                if (!PumpUntil(() => File.Exists(ready), TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Moving source did not open.");
            }
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            var dragProbe = Argument(args, "--drag-probe");
            var top = dragProbe is null ? display.Bounds.Y + display.Bounds.Height / 2 : display.Bounds.Bottom - 210;
            var width = (int)(window.Width * display.ScaleFactor);
            DisplayService.MoveWindowToPixelRect(hwnd, new PixelRect(display.Bounds.X + (display.Bounds.Width - width) / 2,
                top, width, (int)(window.Height * display.ScaleFactor)));
            var attached = desktop && window.RefreshDesktopAttachment();
            Pump(TimeSpan.FromSeconds(3));
            if (dragProbe is not null)
            {
                if (secondary) throw new InvalidOperationException("The native drag probe uses primary-screen coordinates.");
                return ObserveNativeBackdropDrag(dragProbe, report, surface, desktop, attached, top);
            }
            var refresh = DisplayService.RefreshForWindow(hwnd);
            if (refresh?.DeviceName != display.DeviceName)
                throw new InvalidOperationException("The test HWND is not on the selected display.");
            var origin = surface.PointToScreen(new Point());
            var x = (int)origin.X + (int)(window.Width * display.ScaleFactor * .5) - display.Bounds.X;
            var y = (int)origin.Y - display.Bounds.Y;
            // Quarter-height probe above the glass, three-quarter-height probe
            // in the middle of the material at the same source x coordinate.
            var crop = new PixelBounds(x, y - 80, x + 1, y + 100);
            var revision = typeof(LiquidGlassSurface).GetField("_transitionBackdropRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
            long ReadRevision() => Convert.ToInt64(revision.GetValue(surface));
            var before = ReadRevision();
            var startTicks = Stopwatch.GetTimestamp();
            Pump(TimeSpan.FromSeconds(6));
            var duration = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
            var submissions = ReadRevision() - before;
            var performance = surface.InspectPerformance();
            using var sampler = new MonitorCaptureReference.MotionSampler(hwnd, crop);
            var startObservation = Stopwatch.GetTimestamp();
            Pump(TimeSpan.FromSeconds(6));
            var observationSeconds = Stopwatch.GetElapsedTime(startObservation).TotalSeconds;
            var samples = sampler.Snapshot().Where(s => s.SourceQpc >= startObservation).ToArray();
            var differentPixels = samples.Count(s => s.UpperBgra != s.LowerBgra);
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                desktop, attached, frosted, display, refresh, duration, submissions, submissionsPerSecond = submissions / duration,
                performance, crop, observationSeconds, monitorCaptureFailure = sampler.Failure,
                sampledMonitorFrames = samples.Length,
                glassPixelChanges = Changes(samples, s => s.LowerBgra),
                referencePixelChanges = Changes(samples, s => s.UpperBgra),
                samplesWithDifferentGlassAndReferencePixels = differentPixels,
                samples,
                note = "Monitor WGC observes composited pixels. The two samples measure pixel changes, not source IDs or physical scanout.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            return submissions > 0 && samples.Length > 0 && (desktop || differentPixels > 0) ? 0 : 1;
        }
        finally
        {
            window.Close();
            if (fixture is not null)
            {
                if (!fixture.HasExited) fixture.Kill();
                fixture.Dispose();
            }
            app.Shutdown();
        }
    }

    private static int Changes(MonitorCaptureReference.MotionSample[] samples, Func<MonitorCaptureReference.MotionSample, uint> pixel)
        => samples.Zip(samples.Skip(1)).Count(pair => pixel(pair.First) != pixel(pair.Second));

    private static int ObserveNativeBackdropDrag(string executable, string report, LiquidGlassSurface surface, bool desktop, bool attached, int materialTop)
    {
        var runs = new List<object>();
        var failures = 0;
        for (var round = 1; round <= 3; round++)
        foreach (var (area, top) in new[] { ("center", "450"), ("near-material", (materialTop - 190).ToString(System.Globalization.CultureInfo.InvariantCulture)) })
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(top);
            start.ArgumentList.Add("3");
            using var drag = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native drag fixture.");
            var stdout = drag.StandardOutput.ReadToEndAsync();
            var stderr = drag.StandardError.ReadToEndAsync();
            var completed = PumpUntil(() => drag.HasExited, TimeSpan.FromSeconds(8));
            if (!completed) drag.Kill();
            drag.WaitForExit();
            if (!completed || drag.ExitCode != 0) failures++;
            var text = stdout.GetAwaiter().GetResult();
            JsonElement? sample = string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<JsonElement>(text);
            runs.Add(new { round, area, completed, drag.ExitCode, error = stderr.GetAwaiter().GetResult(), sample });
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { desktop, attached, failures, runs, performance = surface.InspectPerformance() },
            new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private sealed class CadenceGlassWindow : GlassWindow
    {
        protected override bool SupportsDesktopLayer => true;
    }
}
