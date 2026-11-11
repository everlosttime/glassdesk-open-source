using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private DispatcherTimer? _nativeRenderTestTimer;
    private static readonly JsonSerializerOptions NativeRenderTestJson = new() { WriteIndented = true };

    // Isolated full-product measurements only. No commands, paths or file reads
    // are accepted by a normal installation without the explicit package marker.
    private void InitializeNativeRenderTest()
    {
        var output = Environment.GetEnvironmentVariable("GLASSDESK_RENDER_TEST_DIRECTORY");
        var marker = Path.Combine(AppContext.BaseDirectory, ".native-static-test-root");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(marker)
            || File.ReadAllText(marker).Trim() != Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)) return;
        output = Path.GetFullPath(output);
        if (!Directory.Exists(output)) return;
        var written = false;
        _nativeRenderTestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nativeRenderTestTimer.Tick += (_, _) =>
        {
            if (_disposed || _shuttingDown) { _nativeRenderTestTimer.Stop(); return; }
            if (File.Exists(Path.Combine(output, "shutdown.request"))) { ExitApplication(); return; }
            if (written || !File.Exists(Path.Combine(output, "report.request"))) return;
            written = true;
            try
            {
                var surface = _dock?.FindName("GlassSurface") as LiquidGlassSurface;
                var dpi = _dock is null ? new DpiScale(1,1) : VisualTreeHelper.GetDpi(_dock);
                var report = new { processId = Environment.ProcessId, backend = Environment.GetEnvironmentVariable("GLASSDESK_DOCK_RENDERER") ?? "legacy",
                    performance = InspectPerformance(), dock = surface?.InspectPerformance(),
                    launcherVisible = _launcher?.IsVisible == true,
                    settingsCreated = _settings is not null, settingsVisible = _settings?.IsVisible == true,
                    displays = DisplayService.Enumerate(), wallpaper = DesktopWallpaperSource.TryRead(),
                    dockBounds = _dock is null ? null : new { _dock.Left, _dock.Top, _dock.ActualWidth, _dock.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY },
                    nativeLoaded = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName == "GlassDesk.Rendering.Native.dll"),
                    screenshotIsDiagnosticReadback = true, displayedFpsMeasured = false };
                File.WriteAllText(Path.Combine(output, "product.json"), JsonSerializer.Serialize(report, NativeRenderTestJson));
                if (_dock is not null && _dock.ActualWidth > 0 && _dock.ActualHeight > 0)
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_dock.ActualWidth * dpi.DpiScaleX),
                        (int)Math.Ceiling(_dock.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    bitmap.Render(_dock);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, "dock.png")); encoder.Save(stream);
                }
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(output, "diagnostic-error.txt"), error.ToString()); }
        };
        _nativeRenderTestTimer.Start();
    }
}
