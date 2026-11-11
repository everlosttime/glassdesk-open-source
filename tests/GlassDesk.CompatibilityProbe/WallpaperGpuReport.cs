using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

internal static class WallpaperGpuReport
{
    internal static int Run(nint source, string path)
    {
        if (File.Exists(path)) throw new IOException("Use a fresh GPU wallpaper report.");
        var traces = new List<CaptureTrace>();
        var frames = new List<object>();
        string? failure = null;
        var region = new ScreenRect(900, 1100, 640, 240);
        Window? sentinel = null;
        try
        {
            sentinel = new Window { Left = 880, Top = 960, Width = 128, Height = 64,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Magenta, ShowInTaskbar = false, ShowActivated = false };
            sentinel.Show(); sentinel.UpdateLayout();
            SetWindowLongPtrW(new WindowInteropHelper(sentinel).Handle, -8, source);
            using var capture = new SharedDesktopSceneCapture(region, false, t => { if (traces.Count < 128) traces.Add(t); },
                Environment.ProcessId, null, desktopOnly: true, () => source);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(4))
            {
                var started = Stopwatch.GetTimestamp();
                var frame = capture.TryRead(TimeSpan.FromMilliseconds(30), reusePixels: true);
                if (frame is not null)
                {
                    long colors = 0; var opaque = 0; var magenta = 0;
                    for (var i = 0; i < frame.Pixels.Length; i += 4)
                    { colors += frame.Pixels[i] + frame.Pixels[i + 1] + frame.Pixels[i + 2]; if (frame.Pixels[i + 3] == 255) opaque++;
                        if(frame.Pixels[i] > 245 && frame.Pixels[i+1] < 10 && frame.Pixels[i+2] > 245)magenta++; }
                    frames.Add(new { frame.SourceQpcTicks, milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        mean = colors / (double)(frame.Width * frame.Height * 3), opaque, magenta,
                        sha256 = Convert.ToHexString(SHA256.HashData(frame.Pixels)) });
                }
                global::Program.Pump(TimeSpan.FromMilliseconds(5));
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        finally { sentinel?.Close(); }
        File.WriteAllText(path, JsonSerializer.Serialize(new { source = source.ToInt64(), region, frames, failure, traces,
            note = "Explicit existing shared-visual provider with selected desktop source; metadata only, no installed settings or windows changed." },
            new JsonSerializerOptions { WriteIndented = true }));
        return failure is null && frames.Count > 0 ? 0 : 1;
    }
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
