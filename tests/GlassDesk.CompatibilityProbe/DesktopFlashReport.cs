using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GlassDesk.Platform.Displays;

internal static class DesktopFlashReport
{
    internal static int Run(int parent, string output)
    {
        var display = DisplayService.Primary()!.Bounds;
        var region = new ScreenRect(display.X + display.Width / 3, display.Bottom - 240, display.Width / 3, 240);
        var samples = new List<CaptureTrace>();
        var diagnostics = new NativeCaptureDiagnostics();
        var traceField = typeof(DocumentedDesktopSceneCapture).GetField("_nextBackgroundTraceTicks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sourceField = typeof(DocumentedDesktopSceneCapture).GetField("_liveWallpaper", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var capture = new DocumentedDesktopSceneCapture(region, false, samples.Add, () => [parent], true);
        var clock = Stopwatch.StartNew();
        var frames = 0; var nextWrite = 0L;
        while (clock.Elapsed < TimeSpan.FromMinutes(10))
        {
            traceField.SetValue(capture, 0L);
            var frame = capture.TryRead(TimeSpan.FromMilliseconds(16));
            var source = (nint)sourceField.GetValue(capture)!;
            GetWindowRect(source, out var bounds);
            DwmGetWindowAttribute(source, 14, out var cloak, 4);
            samples.Add(new("WallpaperSourceState", "AfterRead", Stopwatch.GetTimestamp(), WindowHandle: source,
                Width: bounds.Right - bounds.Left, Height: bounds.Bottom - bounds.Top, Visible: IsWindowVisible(source),
                Detail: $"cloaked={cloak};iconic={IsIconic(source)}"));
            if (frame is not null)
            {
                frames++;
                diagnostics.ObservePixels(frame, 0);
                samples.AddRange(diagnostics.Drain());
            }
            if (clock.ElapsedMilliseconds >= nextWrite)
            {
                nextWrite = clock.ElapsedMilliseconds + 1000;
                File.WriteAllText(output, JsonSerializer.Serialize(new { parent, region, frames, qpcFrequency = Stopwatch.Frequency,
                    durationMilliseconds = clock.ElapsedMilliseconds, samples, pixelsIncluded = false }));
            }
            global::Program.Pump(TimeSpan.FromMilliseconds(1));
        }
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
