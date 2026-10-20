using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Settings;
using GlassDesk.UI;
using GlassDesk.UI.Controls;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using Microsoft.Win32;

internal static partial class Program
{
    private static void VerifyDesktopIconToggleContinuity(object host, Type hostType, DockWindow dock,
        LauncherWindow launcher, SettingsWindow settings, string evidence, string? fixtureExe, int windowCycles = 3)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var product = Path.Combine(AppContext.BaseDirectory, "GlassDesk.exe");
        if (!File.Exists(product)) throw new FileNotFoundException("Shipped capture worker required", product);
        foreach (var window in new System.Windows.Window[] { dock, launcher })
        {
            var glass = (LiquidGlassSurface)window.FindName("GlassSurface")!;
            var layer = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(glass)!;
            var feed = (SharedBackdropFeed)typeof(GlassBackgroundLayer).GetField("_compatible", fields)!.GetValue(layer)!;
            Func<ScreenRect, bool, Action<CaptureTrace>?, IDesktopSceneCapture> create = (region, _, trace) =>
            {
                DesktopSceneCaptureFactory.SelectProvider(WindowsCapabilities.BuildNumber);
                return new RecoveringDesktopSceneCapture(region, trace, product);
            };
            typeof(SharedBackdropFeed).GetField("_create", fields)!.SetValue(feed, create);
        }
        var generationProperty = typeof(BackdropRecovery).GetProperty("Generation", BindingFlags.Static | BindingFlags.NonPublic)!;
        long Generation() => (long)generationProperty.GetValue(null)!;
        var surface = (LiquidGlassSurface)dock.FindName("GlassSurface")!;
        var frameField = typeof(LiquidGlassSurface).GetField("_frame", fields)!;
        var transitionField = typeof(LiquidGlassSurface).GetField("_transitionBackdrop", fields)!;
        var readyField = typeof(LiquidGlassSurface).GetField("_lastRenderMaterialReady", fields)!;
        var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
        var sceneField = typeof(LiquidGlassSurface).GetField("_backdropScene", fields)!;
        var stampField = typeof(LiquidGlassSurface).GetField("_materialStamp", fields)!;
        var stampReader = typeof(LiquidGlassSurface).GetMethod("ReadMaterialStamp", fields)!;
        var slideField = typeof(DockWindow).GetField("_slideOffsetPixels", fields)!;
        var snapshotField = typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", fields)!;
        var snapshotBitmap = typeof(LiquidGlassSurface).GetField("_compatibilityBackdrop", fields)!;
        string? Scene()
        {
            var scene = sceneField.GetValue(surface);
            if (scene is null) return null;
            var windows = (Array)scene.GetType().GetField("_windows", fields)!.GetValue(scene)!;
            return string.Join(";", windows.Cast<object>().Select(w => w.ToString()));
        }
        bool HasLive() => frameField.GetValue(surface) is not null || transitionField.GetValue(surface) is not null;
        var preferences = new List<object>();
        var observations = new List<object>();
        var missing = 0;
        var black = 0;
        var samples = 0;
        var windowActions = 0;
        var measurable = 0;
        var hiddenOrSlidingSamples = 0;
        long lastObservation = 0;
        (bool Live, bool Ready, bool Visible, bool Snapshot, string Phase)? lastObservedState = null;
        var phase = "baseline";
        UserPreferenceChangedEventHandler preference = (_, e) => preferences.Add(new
            { atTicks = Stopwatch.GetTimestamp(), category = e.Category.ToString(), generation = Generation() });
        EventHandler observe = (_, _) =>
        {
            var hasLive = HasLive();
            // A fully concealed panel intentionally retires material. The fixed
            // screen band also samples the desktop during a slide, so classify
            // stationary visible pixels here; the separate wake recorder covers
            // the complete reveal animation and its very first displayed frames.
            var stationaryVisible = dock.IsPerformanceSurfaceVisible && Math.Abs((double)slideField.GetValue(dock)!) < .5;
            System.Threading.Volatile.Write(ref measurable, stationaryVisible ? 1 : 0);
            var currentSnapshot = snapshotField.GetValue(surface) is true && snapshotBitmap.GetValue(surface) is not null;
            var ready = readyField.GetValue(surface) is true;
            var state = (hasLive, ready, stationaryVisible, currentSnapshot, phase);
            var now = Stopwatch.GetTimestamp();
            // Never format thousands of material/scene strings every display
            // callback: the probe itself would create GC/Dispatcher stalls and
            // manufacture the missing-frame intervals it is trying to measure.
            var observeMetadata = lastObservedState != state || now - lastObservation >= Stopwatch.Frequency / 10;
            var pixels = background.LastCaptureTiming?.PixelStatistics;
            var pixelStats = !observeMetadata || pixels is null ? default(JsonElement?) : JsonSerializer.SerializeToElement(pixels);
            var nearBlack = pixelStats?.GetProperty("nearBlackFraction").GetDouble() ?? 0;
            if (observeMetadata)
            {
                lastObservedState = state; lastObservation = now;
                observations.Add(new { phase, atTicks = now, generation = Generation(),
                    ready, hasLive, stationaryVisible, currentSnapshot, source = surface.BackdropSource.ToString(), pixelStats, scene = Scene(),
                    snapshotInvalidation = surface.RevealMaterialInvalidationReason,
                    slide = slideField.GetValue(dock), opticalActive = surface.IsBackdropActive?.Invoke(),
                    surfaceState = surface.InspectPerformance(),
                    savedStamp = stampField.GetValue(surface)?.ToString(), currentStamp = stampReader.Invoke(surface, null)?.ToString() });
            }
            if (phase != "baseline" && stationaryVisible)
            {
                samples++;
                if ((!hasLive && !currentSnapshot) || !ready) missing++;
                if (nearBlack > .98) black++;
            }
            else if (phase != "baseline") hiddenOrSlidingSamples++;
        };
        var before = settings.DesktopIconsRead?.Invoke();
        if (!before.HasValue) throw new InvalidOperationException("Desktop icon state unavailable.");
        hostType.GetMethod("ShowLauncher", fields, null, Type.EmptyTypes, null)!.Invoke(host, null);
        Check("切换前 Dock 完整实时玻璃已就绪", "desktop-icons/material",
            PumpUntil(HasLive, TimeSpan.FromSeconds(6)), "live material", surface.BackdropSource.ToString());
        Pump(TimeSpan.FromMilliseconds(500));
        var firstGeneration = Generation();
        var fixtures = new List<Process>();
        SystemEvents.UserPreferenceChanged += preference;
        CompositionTarget.Rendering += observe;
        FirstFrameRecording.DockContinuityRecording? desktopRecording = null;
        try
        {
            Pump(TimeSpan.FromMilliseconds(300));
            var button = (Button)dock.FindName("DesktopIconsButton")!;
            var recordingRegion = DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(dock).Handle);
            desktopRecording = new(recordingRegion!.Value, () => phase, Path.Combine(evidence, "composed-dock"),
                () => System.Threading.Volatile.Read(ref measurable) != 0);
            for (var i = 0; i < 6; i++)
            {
                phase = "toggle-" + i;
                if (i == 0)
                    FirstFrameRecording.Record(dock, () => button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent)),
                        Path.Combine(evidence, "first-toggle"), TimeSpan.FromSeconds(1.5), recordingRegion);
                else
                {
                    button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                    Pump(TimeSpan.FromMilliseconds(700));
                }
                Check("桌面图标显隐切换 " + i, "desktop-icons/action",
                    settings.DesktopIconsRead?.Invoke() == (i % 2 == 0 ? !before.Value : before.Value),
                    "expected icon state", settings.DesktopIconsRead?.Invoke()?.ToString() ?? "null");
            }
            if (fixtureExe is not null)
            {
                for (var i = 0; i < windowCycles; i++)
                {
                    phase = "window-open-" + i;
                    var readyPath = Path.Combine(evidence, "window-fixture-" + i + ".json");
                    var start = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add("--desktop-transition-fixture"); start.ArgumentList.Add(readyPath);
                    var process = Process.Start(start)!; fixtures.Add(process);
                    if (!PumpUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(5))) throw new TimeoutException("Window fixture did not start.");
                    windowActions++;
                    using var fixture = JsonDocument.Parse(File.ReadAllText(readyPath));
                    var handle = (nint)fixture.RootElement.GetProperty("windowHandle").GetInt64();
                    Pump(TimeSpan.FromMilliseconds(500));
                    var originalBounds = DisplayService.GetWindowPixelBounds(handle)!.Value;
                    phase = "window-move-" + i;
                    for (var step = 0; step < 6; step++)
                    {
                        DisplayService.MoveWindowToPixelRect(handle, originalBounds with { X = originalBounds.X + step * 6 });
                        windowActions++;
                        Pump(TimeSpan.FromMilliseconds(45));
                    }
                    phase = "window-resize-" + i;
                    for (var step = 0; step < 6; step++)
                    {
                        DisplayService.MoveWindowToPixelRect(handle, originalBounds with { Width = originalBounds.Width - step * 8 });
                        windowActions++;
                        Pump(TimeSpan.FromMilliseconds(45));
                    }
                    phase = "window-minimize-" + i;
                    WindowControl.Minimize(handle); windowActions++; Pump(TimeSpan.FromMilliseconds(500));
                    phase = "window-restore-" + i;
                    WindowControl.Restore(handle); windowActions++; Pump(TimeSpan.FromMilliseconds(500));
                    phase = "window-maximize-" + i;
                    ShowDesktopFixtureWindow(handle, 3); windowActions++; Pump(TimeSpan.FromMilliseconds(500));
                    Check("外部窗口最大化 " + i, "desktop-icons/window", IsDesktopFixtureZoomed(handle), "maximized", IsDesktopFixtureZoomed(handle).ToString());
                    phase = "window-maximize-restore-" + i;
                    ShowDesktopFixtureWindow(handle, 9); windowActions++; Pump(TimeSpan.FromMilliseconds(500));
                    phase = "window-close-" + i;
                    if (i == 0)
                        FirstFrameRecording.Record(dock, () => PostDesktopFixtureClose(handle, 0x0010, 0, 0),
                            Path.Combine(evidence, "first-window-close"), TimeSpan.FromSeconds(1.5), recordingRegion);
                    else PostDesktopFixtureClose(handle, 0x0010, 0, 0);
                    if (!PumpUntil(() => process.HasExited, TimeSpan.FromSeconds(5))) throw new TimeoutException("Window fixture did not close.");
                    windowActions++;
                    Pump(TimeSpan.FromMilliseconds(700));
                }
            }
            Check("图标显隐不触发整个背景采集重建", "desktop-icons/recovery",
                Generation() == firstGeneration, firstGeneration.ToString(), Generation().ToString());
            desktopRecording.Dispose();
            Check("实际桌面上的 Dock 连续操作无黑闪", "desktop-icons/material",
                desktopRecording.SampleCount > 0 && desktopRecording.WaitingBodySamples == 0 && desktopRecording.Failure is null
                    && samples > 0 && missing == 0,
                "0 waiting-body desktop samples and no lost live textures",
                $"waiting={desktopRecording.WaitingBodySamples}/{desktopRecording.SampleCount}; cache={missing}/{samples}; " + desktopRecording.Failure);
            desktopRecording = null;
            Check("图标显隐期间采集不出现整片黑帧", "desktop-icons/pixels", samples > 0 && black == 0,
                "0 black samples", $"{black}/{samples}");
        }
        finally
        {
            desktopRecording?.Dispose();
            CompositionTarget.Rendering -= observe;
            SystemEvents.UserPreferenceChanged -= preference;
            foreach (var fixture in fixtures) { if (!fixture.HasExited) fixture.Kill(); fixture.Dispose(); }
            settings.DesktopIconsSet?.Invoke(before.Value);
            BackdropRecovery.ExportDiagnosticReport(Path.Combine(evidence, "backdrop-diagnostics.json"));
            File.WriteAllText(Path.Combine(evidence, "desktop-icon-continuity.json"), JsonSerializer.Serialize(new
            {
                note = "Actual AppHost Dock route, original icon state restored. Composition samples are scheduling diagnostics, not displayed FPS.",
                windowCycles, windowActions, shippedCaptureWorkersUsed = true,
                hiddenOrSlidingSamples, firstGeneration, lastGeneration = Generation(), samples, missing, black, preferences, observations
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostDesktopFixtureClose(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowDesktopFixtureWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "IsZoomed")]
    private static extern bool IsDesktopFixtureZoomed(nint window);
}
