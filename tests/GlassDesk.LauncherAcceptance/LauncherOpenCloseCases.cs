using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.ComponentModel;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    // Uses the real launcher/scene owner with synthetic library data. It does not
    // drive the installed AppHost, claim input-to-display latency or sample DWM FPS.
    private static int ObserveLauncherOpenClose(string[] args)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var output = Path.GetDirectoryName(report)!;
        var root = Path.Combine(output, "isolated-data");
        if (File.Exists(report) || Directory.Exists(root)) throw new IOException("Use a fresh run directory.");
        Directory.CreateDirectory(output);
        var traceEnabled = !args.Contains("--trace-off");
        Environment.SetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR", traceEnabled ? Path.Combine(output, "traces") : null);
        var material = Enum.Parse<GlassMaterialMode>(Argument(args, "--material") ?? "FrostedGlass");
        var cycles = int.Parse(Argument(args, "--cycles") ?? "10");
        if (cycles is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(cycles));
        var captureExe = Path.GetFullPath(Argument(args, "--capture-exe") ?? throw new ArgumentException("--capture-exe required"));
        if (!File.Exists(captureExe)) throw new FileNotFoundException("Missing isolated capture executable", captureExe);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        using var theme = ThemeService.Initialize(app.Dispatcher);
        var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
        data.Settings.Current.Appearance.GlassMaterial = material;
        data.Settings.Current.Appearance.UseSystemBackdrop = true;
        data.Settings.Current.Appearance.PinBarsToDesktop = false;
        data.Settings.Current.Launcher.HighRefreshAnimationsEnabled = !args.Contains("--high-refresh-off");
        data.Settings.Current.Launcher.FocusSearchOnOpen = false;
        theme.Update(data.Settings.Current.Appearance);
        for (var i = 0; i < 36; i++) data.Library.Add(new AppEntry
            { Id = $"fixture-{i}", DisplayName = $"应用 {i:00}", Target = Path.Combine(root, $"missing-{i}"), IsTargetMissing = true });
        // Persist only the disposable fixture, outside measured operations, so
        // exact published AppHost regression runs can reuse safe synthetic apps.
        data.Library.Save(); data.Settings.Save();
        using var services = new AppServiceHost(data);
        var display = DisplayService.Primary() ?? throw new InvalidOperationException("No primary display");
        var window = new LauncherWindow();
        window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
        window.AttachServices(services, true); window.ApplySettings(data.Settings.Current.Launcher);
        typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", fields)!.SetValue(window, 1);
        var anchor = new PixelRect(display.WorkArea.X + display.WorkArea.Width / 2 - 24, display.WorkArea.Bottom - 76, 48, 48);
        window.ContainerAnchorProvider = () => anchor;
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
        var ablation = Argument(args, "--ablation") ?? "none";
        var removeClips = new List<(DependencyPropertyDescriptor Descriptor, UIElement Element, EventHandler Handler)>();
        if (ablation is "shadow" or "all")
        {
            _ = new WindowInteropHelper(window).EnsureHandle();
            var shadowField = typeof(GlassWindow).GetField("_shadow", fields)!;
            ((IDisposable?)shadowField.GetValue(window))?.Dispose(); shadowField.SetValue(window, null);
        }
        if (ablation is "content" or "all") ((UIElement)window.FindName("LauncherContent")).Visibility = Visibility.Hidden;
        if (ablation is "backdrop" or "all") { surface.SetPerformanceSleeping(true); surface.Visibility = Visibility.Collapsed; }
        if (ablation is "freeze" or "freeze-no-scene")
        {
            surface.IsBackdropActive = () => false;
            if (ablation == "freeze-no-scene") { var fixedScene = new GlassBackdropScene(); surface.ReadBackdropScene = () => fixedScene; }
        }
        if (ablation is "clips" or "all")
        {
            foreach (UIElement element in new UIElement[] { window, surface, (UIElement)window.FindName("ContainerTransformClipRoot") })
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.ClipProperty, element.GetType());
                EventHandler changed = (_, _) => { if (element.Clip is not null) element.Clip = null; };
                descriptor.AddValueChanged(element, changed); element.Clip = null;
                removeClips.Add((descriptor, element, changed));
            }
        }
        var sceneChanges = new List<object>();
        if (args.Contains("--scene-diagnosis"))
        {
            var read = surface.ReadBackdropScene;
            int? previous = null;
            surface.ReadBackdropScene = () =>
            {
                var scene = read();
                var hash = scene?.GetHashCode();
                if (hash != previous && sceneChanges.Count < 1000)
                {
                    previous = hash;
                    sceneChanges.Add(new { qpc = Stopwatch.GetTimestamp(), hash, window.IsVisible,
                        bounds = DisplayService.GetWindowPixelBounds(new WindowInteropHelper(window).Handle),
                        windows = scene is null ? null : ((GlassBackdropScene.SceneWindow[])typeof(GlassBackdropScene).GetField("_windows", fields)!.GetValue(scene)!)
                            .Select(w => new { handle = w.Handle.ToInt64(), w.ProcessId, w.Left, w.Top, w.Right, w.Bottom, w.IsTransientOverlay }).ToArray() });
                }
                return scene;
            };
        }
        var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
        var live = (SharedBackdropFeed)typeof(GlassBackgroundLayer).GetField("_compatible", fields)!.GetValue(background)!;
        typeof(SharedBackdropFeed).GetField("_create", fields)!.SetValue(live,
            (Func<ScreenRect, bool, Action<CaptureTrace>?, IDesktopSceneCapture>)((region, _, trace) => new RecoveringDesktopSceneCapture(region, trace, captureExe)));
        var wallpaper = (SharedBackdropFeed)typeof(GlassBackgroundLayer).GetField("_wallpaperFeed", fields)!.GetValue(background)!;
        typeof(SharedBackdropFeed).GetField("_create", fields)!.SetValue(wallpaper,
            (Func<ScreenRect, bool, Action<CaptureTrace>?, IDesktopSceneCapture>)((region, _, trace) => new IsolatedDesktopSceneCapture(region, GlassBackdropSource.Wallpaper, trace, captureExe)));
        var observations = new List<object>();
        if (Argument(args, "--capture-gate") is { } gate)
        {
            Directory.CreateDirectory(gate);
            File.WriteAllText(Path.Combine(gate, "ready.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId }));
            if (!PumpUntil(() => File.Exists(Path.Combine(gate, "go")), TimeSpan.FromSeconds(60)))
                throw new TimeoutException("Owned presentation capture gate was not released.");
        }
        var valid = true;
        DisplayRefreshInfo? refresh = null;
        bool OpenSettled() => window.IsVisible && !(bool)typeof(LauncherWindow).GetField("_transformActive", fields)!.GetValue(window)!;
        try
        {
            Measure("first", 0, "open", () => window.ShowWithTransition(anchor), OpenSettled);
            refresh = DisplayService.RefreshForWindow(new WindowInteropHelper(window).Handle);
            Measure("first", 0, "close", window.CloseWithTransition, () => !window.IsVisible);
            // Same hidden prewarm and no source changes in both arms. No screenshots
            // or material observations are captured inside the active timing window.
            window.PrewarmNextRevealMaterial(); Pump(TimeSpan.FromMilliseconds(300));
            for (var i = 1; i <= cycles; i++)
            {
                Measure("repeat", i, "open", () => window.ShowWithTransition(anchor), OpenSettled);
                Pump(TimeSpan.FromMilliseconds(50));
                Measure("repeat", i, "close", window.CloseWithTransition, () => !window.IsVisible);
                Pump(TimeSpan.FromMilliseconds(50));
            }
            window.ShowWithTransition(anchor); Pump(TimeSpan.FromMilliseconds(90));
            Measure("reverse", 0, "close", window.CloseWithTransition, () => !window.IsVisible);
            window.ShowWithTransition(anchor); PumpUntil(OpenSettled, TimeSpan.FromSeconds(4));
            window.CloseWithTransition(); Pump(TimeSpan.FromMilliseconds(90));
            Measure("reverse", 0, "open", () => window.ShowWithTransition(anchor), OpenSettled);
            Measure("reverse", 1, "close", window.CloseWithTransition, () => !window.IsVisible);
            window.SetPerformanceSleeping(true); window.SetBackgroundWorkSuspended(true);
            window.SetPerformanceSleeping(false); window.SetBackgroundWorkSuspended(false);
            Measure("sleep-resume", 0, "open", () => window.ShowWithTransition(anchor), OpenSettled);
            Measure("sleep-resume", 0, "close", window.CloseWithTransition, () => !window.IsVisible);
            Pump(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            foreach (var item in removeClips) item.Descriptor.RemoveValueChanged(item.Element, item.Handler);
            window.Close();
            surface.Dispose();
            var modules = new[] { typeof(LauncherWindow).Assembly, typeof(LiquidGlassSurface).Assembly, Assembly.GetExecutingAssembly() }
                .Select(a => new { path = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) });
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, processId = Environment.ProcessId, qpcFrequency = Stopwatch.Frequency,
                ablation, qualityChangedForDiagnostic = ablation != "none",
                material = material.ToString(), cycles, traceEnabled, componentFixture = true,
                placementReuse = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_PLACEMENT_REUSE") == "1",
                displayCadence = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_DISPLAY_CADENCE") == "1",
                visualShadowOpacity = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_VISUAL_SHADOW_OPACITY") == "1",
                sceneReuse = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_SCENE_REUSE") == "1",
                deferredPaintSceneChecks = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_DEFER_PAINT_SCENE_CHECKS") == "1",
                retainRevealGrid = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_RETAIN_REVEAL_GRID") == "1",
                nativeShadow = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW") == "1",
                nativeDllSha256 = Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_RENDER_DLL") is { } nativeDll && File.Exists(nativeDll)
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativeDll))) : null,
                highRefreshEnabled = data.Settings.Current.Launcher.HighRefreshAnimationsEnabled,
                display, refresh,
                displayedFrames = "unmeasured: no attributable application/HWND display trace",
                clickToFirstFrame = "unmeasured: fixture starts at ShowWithTransition, not the product click",
                background = "current composed desktop; wallpaper motion and other applications uncontrolled",
                environment = new { os = Environment.OSVersion.ToString(), SystemParameters.IsRemoteSession, powerStatus = SystemParameters.PowerLineStatus.ToString() },
                valid, modules, observations, sceneChanges,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Launcher {material}: {observations.Count} operations, valid={valid}; {report}");
        return valid ? 0 : 1;

        void Measure(string scenario, int cycle, string direction, Action start, Func<bool> completed)
        {
            var requested = Stopwatch.GetTimestamp();
            start();
            var dispatchMilliseconds = Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
            var requestId = (long)typeof(LauncherWindow).GetField("_animationRequestId", fields)!.GetValue(window)!;
            var activeCapture = JsonSerializer.SerializeToElement(surface.InspectPerformance()).GetProperty("capture");
            var invalidationAtReturn = surface.RevealMaterialInvalidationReason;
            var settled = PumpUntil(completed, TimeSpan.FromSeconds(4));
            var ended = Stopwatch.GetTimestamp();
            var metrics = JsonSerializer.SerializeToElement(surface.InspectPerformance());
            var painted = metrics.GetProperty("firstVisibleRenderTicks").GetInt64();
            var ready = metrics.GetProperty("firstVisibleRenderMaterialReady");
            var shadow = typeof(GlassWindow).GetField("_shadow", fields)!.GetValue(window) as GlassShadow;
            var shadowMetrics = shadow?.InspectPerformance();
            observations.Add(new
            {
                scenario, cycle, direction, requestId, requested, ended, dispatchMilliseconds, settled,
                entryToFirstWpfPaintMilliseconds = direction == "open" && painted >= requested
                    ? (double?)Stopwatch.GetElapsedTime(requested, painted).TotalMilliseconds : null,
                firstPaintMaterialReady = ready.Clone(), activeCapture, metrics,
                invalidationAtReturn, invalidationAtEnd = surface.RevealMaterialInvalidationReason,
                shadowMetrics, unifiedMetrics = typeof(LauncherWindow).GetMethod("InspectUnifiedTransition", fields)?.Invoke(window, null),
            });
            valid &= settled && (material == GlassMaterialMode.PlainGlass || window.SurfaceMode == GlassSurfaceMode.OpticalGlass);
            if (Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW") == "1"
                && material != GlassMaterialMode.PlainGlass && direction == "open")
                valid &= shadowMetrics is not null && JsonSerializer.SerializeToElement(shadowMetrics).GetProperty("nativeActive").GetBoolean();
        }
    }
}
