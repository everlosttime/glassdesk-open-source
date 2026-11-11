using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

internal static class DesktopBackgroundReport
{
    internal static int Run(string path)
    {
        var traces = new Queue<CaptureTrace>();
        var frames = 0;
        string? errorType = null;
        object? wallpaperMetadata = null;
        ScreenRect captureRegion = default;
        object[] windows = [];
        var windowInventoryTruncated = false;
        CaptureTrace[] pixelStatistics = [];
        try
        {
            var monitor = DisplayService.Primary()!.Bounds;
            var width = Math.Min(1280, monitor.Width);
            var height = Math.Min(720, monitor.Height);
            var region = new ScreenRect(monitor.X + (monitor.Width - width) / 2,
                monitor.Y + (monitor.Height - height) / 2, width, height);
            captureRegion = region;
            windows = ReadWindowInventory(region, out windowInventoryTruncated);
            var snapshot = DesktopWallpaperSource.TryRead();
            var wallpaper = new WallpaperBackdrop();
            wallpaper.Update(new(region.X, region.Y, width, height), width, height, 0);
            var statistics = new NativeCaptureDiagnostics();
            if (wallpaper.Pixels is { } background)
                statistics.ObservePixels(new(width, height, background), 0);
            wallpaperMetadata = new
            {
                available = snapshot is not null, position = snapshot?.Position.ToString(),
                backgroundColor = snapshot?.BackgroundColor, decodedPixelsAvailable = wallpaper.Pixels is not null,
                sourceKind = wallpaper.SourceKind,
                windowsCacheFileAvailable = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes", "TranscodedWallpaper")),
                statistics = statistics.Drain(),
                monitors = snapshot?.Monitors.Select(item => new
                {
                    item.Bounds, pathEmpty = string.IsNullOrEmpty(item.Path), fileExists = File.Exists(item.Path),
                    extension = Path.GetExtension(item.Path),
                    windowsCacheEligible = new WindowsWallpaperCache().Resolve(item.Path, snapshot.Monitors.Count) is not null
                })
            };
            var executable = Path.Combine(AppContext.BaseDirectory, "GlassCaptureProbe.exe");
            using var capture = new RecoveringDesktopSceneCapture(region, trace =>
            {
                if (trace.Stage is not ("DesktopBackground" or "Wallpaper" or "SceneBackground" or "WindowCapture"
                    or "BackdropProvider" or "PrintWindow" or "SceneWindowExcluded")) return;
                if (trace.Reason == "Delivered") return;
                if (traces.Count == 256) traces.Dequeue();
                traces.Enqueue(trace);
            }, executable);
            var watch = Stopwatch.StartNew();
            CapturePixels? frame = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(6))
            {
                var next = capture.TryRead(TimeSpan.FromMilliseconds(16));
                if (next is not null) { frame = next; frames++; }
                global::Program.Pump(TimeSpan.FromMilliseconds(5));
            }
            if (frame is not null)
            {
                statistics = new NativeCaptureDiagnostics();
                statistics.ObservePixels(frame, 0);
                pixelStatistics = statistics.Drain();
            }
        }
        catch (Exception error) { errorType = error.GetType().Name; }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 2, screenPixelsIncluded = false, windowTitlesIncluded = false,
            wallpaperPathsIncluded = false, os = WindowsCapabilities.OsVersion.ToString(),
            version = typeof(DesktopBackgroundReport).Assembly.GetName().Version?.ToString(),
            captureRegion, independentProbe = true, applicationMaterialSampled = false,
            windows, windowInventoryTruncated,
            frames, errorType, wallpaper = wallpaperMetadata, pixelStatistics,
            traces = traces.Select(trace => new
            {
                trace.Stage, trace.Reason, trace.Width, trace.Height, trace.NearBlackFraction,
                trace.MeanChannel, trace.TransparentFraction, trace.ErrorCode,
                trace.WindowClass, trace.WindowProcess, trace.WindowHandle
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        return errorType is null && frames > 0 ? 0 : 1;
    }

    private static object[] ReadWindowInventory(ScreenRect region, out bool truncated)
    {
        var windows = new List<object>();
        var limitReached = false;
        var excluded = CaptureProcessFamily.Snapshot();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0 || excluded.Contains((int)processId)) return true;
            if (DwmGetWindowAttribute(window, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) < 0
                && !GetWindowRect(window, out bounds)) return true;
            var left = Math.Max(region.X, bounds.Left);
            var top = Math.Max(region.Y, bounds.Top);
            var right = Math.Min(region.X + region.Width, bounds.Right);
            var bottom = Math.Min(region.Y + region.Height, bounds.Bottom);
            if (right <= left || bottom <= top) return true;
            if (windows.Count == 64) { limitReached = true; return false; }
            var name = new StringBuilder(128);
            GetClassNameW(window, name, name.Capacity);
            var className = name.ToString();
            var style = GetWindowLongPtrW(window, -16).ToInt64();
            var extendedStyle = GetWindowLongPtrW(window, -20).ToInt64();
            var iconic = IsIconic(window);
            var cloaked = DwmGetWindowAttribute(window, 14, out int cloak, sizeof(int)) >= 0 && cloak != 0;
            var desktopHost = className is "Progman" or "WorkerW";
            var layeredKnown = GetLayeredWindowAttributes(window, out var colorKey, out var alpha, out var layeringFlags);
            var excludedReason = WindowsCapabilities.BuildNumber < 22000 ? CompatibleWindowVisibility.ExclusionReason(window) : null;
            windows.Add(new
            {
                processId, className, bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom },
                style, extendedStyle, cloaked, iconic, desktopHost,
                layeredAttributesAvailable = layeredKnown,
                layeredAlpha = layeredKnown ? (int?)alpha : null,
                layeredFlags = layeredKnown ? (uint?)layeringFlags : null,
                layeredColorKey = layeredKnown && (layeringFlags & 1) != 0 ? (uint?)colorKey : null,
                compatibleExclusionReason = excludedReason,
                compatibleForegroundCandidate = !cloaked && !iconic && !desktopHost && excludedReason is null,
                coverageFraction = (double)((long)(right - left) * (bottom - top)) / ((long)region.Width * region.Height)
            });
            return true;
        }, 0);
        truncated = limitReached;
        return windows.ToArray();
    }

    private delegate bool EnumWindowProc(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect bounds, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
