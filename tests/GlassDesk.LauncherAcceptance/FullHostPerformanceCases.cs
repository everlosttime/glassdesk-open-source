using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private const BindingFlags PerformanceInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Memory/CPU/GPU are sampled externally. No GC collection, working-set trim or per-frame counter queries.
    private static int VerifyFullHostPerformance(string[] args)
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        var marker = Path.Combine(directory, ".full-host-acceptance-root");
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != directory)
            throw new InvalidOperationException("Full-host acceptance requires an explicitly marked test package.");
        if (!Directory.Exists(Path.Combine(directory, "Data")))
            throw new InvalidOperationException("A staged, copied Data profile is required.");
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var evidence = Path.GetDirectoryName(report)!;
        var phaseFile = Path.GetFullPath(Argument(args, "--phase-file") ?? Path.Combine(evidence, "phase.txt"));
        Directory.CreateDirectory(evidence);
        var baseline = args.Contains("--baseline");
        var quick = args.Contains("--quick");
        var phaseSeconds = quick ? 2 : 60;
        var focused = args.Contains("--reveal-sleep-focused");
        var dockOnly = args.Contains("--dock-reveal-only");
        var revealTimings = new List<object>();
        var longRunSeconds = int.TryParse(Argument(args, "--long-run-seconds"), out var seconds) ? Math.Max(0, seconds) : 0;
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "GlassDesk.dll"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml") });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        var hostType = assembly.GetType("GlassDesk.App.AppHost", true)!;
        var options = assembly.GetType("GlassDesk.App.Startup.CommandLineOptions", true)!
            .GetMethod("Parse")!.Invoke(null, [Array.Empty<string>()])!;
        var host = (IDisposable)Activator.CreateInstance(hostType, PerformanceInstance, null, [application, options], null)!;
        var observations = new List<object>();
        var materialChecks = new List<object>();
        var stages = new List<object>();
        var helperExitMs = (double?)null;
        var shaderFailures = 0;
        var previousForeignForeground = GetPerformanceForegroundWindow();
        Process? fixture = null;
        EventHandler shaderFailure = (_, _) => shaderFailures++;
        System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered += shaderFailure;
        LauncherWindow? launcher = null;
        Window? dock = null;
        try
        {
            Phase("startup");
            var started = hostType.GetMethod("Start")!.Invoke(host, null)!.ToString();
            if (started != "Started") throw new InvalidOperationException("Full AppHost did not start: " + started);
            launcher = (LauncherWindow)hostType.GetField("_launcher", PerformanceInstance)!.GetValue(host)!;
            dock = (Window)hostType.GetField("_dock", PerformanceInstance)!.GetValue(host)!;
            UseProductCaptureWorkers();
            launcher.ShowInTaskbar = true;
            if (args.Contains("--observe-dock-repro"))
            {
                ObserveRealDesktopDock();
                return _failed == 0 ? 0 : 1;
            }
            if (dockOnly)
            {
                VerifyCoveredDockReveal();
                return _failed == 0 ? 0 : 1;
            }
            if (focused)
            {
                RunFocusedRevealSleep();
                return _failed == 0 ? 0 : 1;
            }
            SamplePhase("idle-warmup", phaseSeconds);
            SamplePhase("idle", phaseSeconds);
            Phase("opening");
            Reveal("initial-open", checkMaterial: !baseline);
            Check("完整宿主启动台展开", "performance/full-host", launcher.IsVisible && launcher.CanNavigatePages,
                "visible and transition complete", $"visible={launcher.IsVisible},canNavigate={launcher.CanNavigatePages}");
            SamplePhase("open-warmup", phaseSeconds);
            SamplePhase("open", phaseSeconds);
            Phase("closing");
            CloseLauncher();
            SamplePhase("closed-warmup", phaseSeconds);
            SamplePhase("closed", phaseSeconds);

            if (!baseline)
            {
                Phase("sleep-warmup");
                var shutdown = Stopwatch.StartNew();
                SetManualSleep(true);
                var stopped = PumpUntil(AllBackgroundStopped, TimeSpan.FromSeconds(2));
                helperExitMs = shutdown.Elapsed.TotalMilliseconds;
                Check("游戏休眠在2秒内停止捕获助手和渲染时钟", "performance/sleep", stopped,
                    "helpers=0,render/watchdog=false", SnapshotText());
                Pump(TimeSpan.FromSeconds(3));
                var before = CounterSignature();
                SamplePhase("sleeping", phaseSeconds);
                var after = CounterSignature();
                Check("休眠期间捕获/场景/缩放/帧发布计数不再增长", "performance/sleep", before == after,
                    before, after);
                Check("休眠期间运行状态跟踪暂停", "performance/sleep", ReadHostFlag("TrackerPaused"),
                    "true", HostSnapshot().ToString());
                Phase("temporary-wake");
                Reveal("game-temporary-open", true);
                Check("手动休眠中主动打开进入临时活动", "performance/temporary", ReadHostState() == "TemporaryActive",
                    "TemporaryActive", ReadHostState());
                CloseLauncher();
                Check("临时启动台关闭后恢复游戏休眠", "performance/temporary",
                    PumpUntil(() => ReadHostState() == "Sleeping" && AllBackgroundStopped(), TimeSpan.FromSeconds(2)),
                    "Sleeping; helpers=0", SnapshotText());
                SetManualSleep(false);
                Pump(TimeSpan.FromMilliseconds(400));
                VerifyAutomaticFullscreen();
            }

            Phase("rapid-interaction");
            var completedCycles = 0;
            for (var cycle = 0; cycle < 100; cycle++)
            {
                Reveal("rapid-open-" + cycle, !baseline);
                if (!launcher.IsVisible || !launcher.CanNavigatePages) break;
                CloseLauncher();
                if (launcher.IsVisible) break;
                completedCycles++;
            }
            Check("完整宿主快速开合100次", "performance/rapid", completedCycles == 100,
                "100 completed production transitions", completedCycles.ToString());
            Reveal("paging-open", !baseline);
            var next = (Button)launcher.FindName("NextPage")!;
            var previous = (Button)launcher.FindName("PreviousPage")!;
            var pageText = (TextBlock)launcher.FindName("PageText")!;
            var completedPageChanges = 0;
            for (var pageChange = 0; pageChange < 200; pageChange++)
            {
                var before = pageText.Text;
                (pageChange % 2 == 0 ? next : previous).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (PumpUntil(() => pageText.Text != before && launcher.CanNavigatePages, TimeSpan.FromSeconds(2)))
                    completedPageChanges++;
                else break;
            }
            Check("完整宿主生产翻页按钮往返200次", "performance/paging", completedPageChanges == 200,
                "200 distinct page changes", completedPageChanges + "; " + pageText.Text);
            CloseLauncher();

            if (!baseline)
            {
                if (longRunSeconds > 0)
                {
                    Phase("long-run");
                    var longRun = Stopwatch.StartNew();
                    var lastCycle = TimeSpan.FromMinutes(-1);
                    while (longRun.Elapsed.TotalSeconds < longRunSeconds)
                    {
                        if (longRun.Elapsed - lastCycle >= TimeSpan.FromMinutes(1))
                        {
                            Reveal("long-run-" + (int)longRun.Elapsed.TotalSeconds, true);
                            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(TimeSpan.FromMilliseconds(300));
                            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(TimeSpan.FromMilliseconds(300));
                            CloseLauncher();
                            observations.Add(new { phase = "long-run", elapsedSeconds = longRun.Elapsed.TotalSeconds, snapshot = Snapshot() });
                            lastCycle = longRun.Elapsed;
                        }
                        Pump(TimeSpan.FromSeconds(1));
                    }
                    Check("完整宿主长时间重复开合测试", "performance/long-run", longRunSeconds >= 1800,
                        "at least 1800 seconds", longRun.Elapsed.TotalSeconds.ToString("F1"));
                }
                Phase("first-frame-recording");
                Pump(TimeSpan.FromSeconds(quick ? 6 : 10));
                RecordReveal("deep-sleep-first-frame");
                CloseLauncher();
                SamplePhase("hidden-one-minute", quick ? 2 : 60);
                RecordReveal("hidden-one-minute-first-frame");
                CloseLauncher();
                SamplePhase("hidden-ten-minutes", quick ? 5 : 600);
                RecordReveal("hidden-ten-minutes-first-frame");
                CloseLauncher();
                VerifyUnavailableCapture();
                Check("每次首次可见材质均准备完整", "performance/first-material",
                    materialChecks.Count > 0 && materialChecks.All(MaterialCheckPassed),
                    "firstVisibleRenderMaterialReady=true at every first paint", JsonSerializer.Serialize(materialChecks));
            }
            Check("完整宿主未抑制真实失活关闭行为", "performance/full-host", !HasModalKeepOpen(launcher),
                "modalKeepOpenSuppressed=false", HasModalKeepOpen(launcher).ToString());
            Check("无失效玻璃着色器", "performance/full-host", shaderFailures == 0, "0", shaderFailures.ToString());
            observations.Add(new { phase = "final", snapshot = Snapshot() });
        }
        catch (Exception error)
        { Check("完整宿主性能验收", "performance/full-host", false, "no exception", error.ToString()); }
        finally
        {
            if (fixture is not null) StopFixture();
            System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered -= shaderFailure;
            Phase("completed");
            host.Dispose(); application.Shutdown();
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now, processId = Environment.ProcessId,
                productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion,
                baseline, quick, phaseSeconds, longRunSeconds, helperExitMs, shaderFailures,
                liveProfileUsed = false, modalKeepOpenSuppressed = false,
                driver = "Actual production AppHost on staged copied Data; shipped GlassDesk.exe capture workers; production WPF show/close and page-button routes. Not physical user gestures. Memory/CPU/GPU are measured externally; recording phases are excluded.",
                unverified = new[] { "Real DX11/DX12/Vulkan game FPS and 1% low", "Other Win11 versions and GPU/display/HDR hardware combinations", "Physical drag gestures in this performance-only runner" },
                focused, revealTimings, stages, observations, materialChecks, passed = Cases.Count - _failed, failed = _failed, cases = Cases,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return _failed == 0 ? 0 : 1;

        void Phase(string name)
        {
            File.WriteAllText(phaseFile, name);
            stages.Add(new { phase = name, timestamp = DateTimeOffset.Now, processId = Environment.ProcessId });
        }
        void ObserveRealDesktopDock()
        {
            var window = (DockWindow)dock!;
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
            var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", PerformanceInstance)!.GetValue(surface)!;
            var feed = (SharedBackdropFeed)typeof(GlassBackgroundLayer).GetField("_compatible", PerformanceInstance)!.GetValue(background)!;
            var traces = new System.Collections.Concurrent.ConcurrentQueue<CaptureTrace>();
            var original = feed.TraceObserved;
            feed.TraceObserved = trace => { original?.Invoke(trace); traces.Enqueue(trace); while (traces.Count > 20000) traces.TryDequeue(out _); };
            var rect = (PixelRect)typeof(DockWindow).GetField("_restingRect", PerformanceInstance)!.GetValue(window)!;
            var marker = Path.Combine(evidence, "marker.txt");
            File.WriteAllText(marker, "initial");
            File.WriteAllText(Path.Combine(evidence, "ready.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId, dockRect = rect }));
            Phase("observe-ready");
            using var log = new StreamWriter(Path.Combine(evidence, "timeline.jsonl")) { AutoFlush = true };
            var clock = Stopwatch.StartNew();
            var previousMarker = "";
            var observeSeconds = int.TryParse(Argument(args, "--observe-seconds"), out var requestedSeconds)
                ? Math.Clamp(requestedSeconds, 1, 1800) : 180;
            while (clock.Elapsed.TotalSeconds < observeSeconds && !File.Exists(Path.Combine(evidence, "stop")))
            {
                var currentMarker = File.ReadAllText(marker).Trim();
                var row = new { elapsed = clock.Elapsed.TotalMilliseconds, marker = currentMarker,
                    foreground = GetPerformanceForegroundWindow().ToInt64(), snapshot = Snapshot(), timing = background.LastCaptureTiming,
                    slide = typeof(DockWindow).GetField("_slideOffsetPixels", PerformanceInstance)!.GetValue(window),
                    autoHide = typeof(DockWindow).GetField("_autoHide", PerformanceInstance)!.GetValue(window)?.GetType().GetProperty("State")?.GetValue(typeof(DockWindow).GetField("_autoHide", PerformanceInstance)!.GetValue(window))?.ToString(),
                    feedTiming = feed.InspectTiming(), recovery = BackdropRecovery.Generation };
                log.WriteLine(JsonSerializer.Serialize(row));
                File.WriteAllText(Path.Combine(evidence, "current.json"), JsonSerializer.Serialize(row, new JsonSerializerOptions { WriteIndented = true }));
                if (currentMarker != previousMarker)
                {
                    previousMarker = currentMarker;
                    var texture = typeof(LiquidGlassSurface).GetField("_transitionBackdrop", PerformanceInstance)!.GetValue(surface) as BitmapSource;
                    if (texture is not null)
                    {
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(texture));
                        using var output = File.Create(Path.Combine(evidence, currentMarker + "-texture.png")); encoder.Save(output);
                    }
                    File.WriteAllText(Path.Combine(evidence, currentMarker + "-traces.json"), JsonSerializer.Serialize(traces.ToArray()));
                }
                Pump(TimeSpan.FromMilliseconds(100));
            }
            File.WriteAllText(Path.Combine(evidence, "traces.json"), JsonSerializer.Serialize(traces.ToArray()));
            feed.TraceObserved = original;
            observations.Add(new { phase = "real-desktop-observation", seconds = clock.Elapsed.TotalSeconds, snapshot = Snapshot() });
        }
        void RunFocusedRevealSleep()
        {
            SamplePhase("idle-warmup", phaseSeconds); SamplePhase("idle", phaseSeconds);
            TimedReveal("initial");
            SamplePhase("open-warmup", phaseSeconds); SamplePhase("open", phaseSeconds);
            CloseLauncher();
            SamplePhase("closed-warmup", phaseSeconds); SamplePhase("closed", phaseSeconds);
            for (var cycle = 0; cycle < 5; cycle++)
            {
                Phase("cold-reveal-wait"); Pump(TimeSpan.FromSeconds(6));
                TimedReveal("reopen-after-6s-" + cycle); CloseLauncher();
            }
            Phase("hidden-retirement-wait"); Pump(TimeSpan.FromSeconds(31));
            TimedReveal("after-long-hide"); CloseLauncher();
            TimedReveal("before-game-sleep"); CloseLauncher();
            Phase("sleep-warmup"); SetManualSleep(true);
            Check("休眠资源全部停止", "reveal-sleep/focused", PumpUntil(AllBackgroundStopped, TimeSpan.FromSeconds(2)),
                "all workers and clocks stopped", SnapshotText());
            SamplePhase("sleep-warmup", phaseSeconds); SamplePhase("sleeping", phaseSeconds);
            TimedReveal("temporary-game-wake"); CloseLauncher();
            Check("主动唤醒关闭后回到休眠", "reveal-sleep/focused", PumpUntil(AllBackgroundStopped, TimeSpan.FromSeconds(2)),
                "all workers and clocks stopped", SnapshotText());
            SetManualSleep(false); Pump(TimeSpan.FromSeconds(1));
            Phase("recording-first-frame"); Pump(TimeSpan.FromSeconds(6));
            RecordReveal("focused-cold-first-frame"); CloseLauncher();

            VerifyCoveredDockReveal();
        }
        void VerifyCoveredDockReveal()
        {
            Phase("recording-dock-reveal");
            var dockWindow = dock!;
            var resting = (PixelRect)dockWindow.GetType().GetField("_restingRect", PerformanceInstance)!.GetValue(dockWindow)!;
            var slide = dockWindow.GetType().GetField("_slideOffsetPixels", PerformanceInstance)!;
            var hidden = (int)dockWindow.GetType().GetField("_fullyHiddenOffsetPixels", PerformanceInstance)!.GetValue(dockWindow)!;
            var primary = DisplayService.Primary()!;
            var command = Path.Combine(evidence, "dock-cover-command.txt");
            var ready = Path.Combine(evidence, "dock-cover-ready.json");
            var cursor = DisplayService.GetCursorPixelPoint();
            try
            {
                IosInput.SetCursorPos(primary.Bounds.X + 20, primary.Bounds.Y + 20);
                WriteFixtureCommand(command, "cover");
                fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    ArgumentList = { "--fullscreen-fixture", "--fixture-cover-dock", "--fixture-ready", ready, "--fixture-command", command }
                })!;
                if (!PumpUntil(() => File.Exists(ready), TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Dock cover fixture did not initialize.");
                // Windows may refuse background SetForegroundWindow. A native click
                // on this known fixture grants focus through the actual input route.
                IosInput.SetCursorPos(primary.Bounds.X + 32, primary.Bounds.Y + primary.Bounds.Height / 2 + 32);
                IosInput.Mouse(2); IosInput.Mouse(4);
                IosInput.SetCursorPos(primary.Bounds.X + 20, primary.Bounds.Y + 20);
                var covered = PumpUntil(() => (double)slide.GetValue(dockWindow)! >= hidden - .5,
                    TimeSpan.FromSeconds(5));
                Check("外部前台窗口遮挡后Dock正常隐藏", "reveal-sleep/dock", covered, "hidden through production coverage", slide.GetValue(dockWindow)!.ToString()!);
                if (!covered) return;
                Pump(TimeSpan.FromSeconds(6));
                RecordDockWake("dock-window-uncover", () => WriteFixtureCommand(command, "uncover"));
                Check("移开遮挡窗口后Dock唤出", "reveal-sleep/dock", (double)slide.GetValue(dockWindow)! == 0,
                    "resting offset=0", slide.GetValue(dockWindow)!.ToString()!);
                WriteFixtureCommand(command, "cover");
                IosInput.SetCursorPos(primary.Bounds.X + 20, primary.Bounds.Y + 20);
                if (!PumpUntil(() => (double)slide.GetValue(dockWindow)! >= hidden - .5, TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Dock did not hide for edge-reveal recording.");
                Pump(TimeSpan.FromSeconds(6));
                RecordDockWake("dock-bottom-edge-reveal", () => IosInput.SetCursorPos(resting.X + resting.Width / 2, resting.Bottom - 2));
                // GIF encoding blocks this dispatcher after the sampled desktop
                // interval. Drain any queued slide ticks before checking its endpoint.
                var edgeSettled = PumpUntil(() => (double)slide.GetValue(dockWindow)! == 0,
                    TimeSpan.FromSeconds(2));
                Check("遮挡时靠近底边正常唤出Dock", "reveal-sleep/dock", edgeSettled,
                    "resting offset=0", slide.GetValue(dockWindow)!.ToString()!);
                if (args.Contains("--dock-reveal-long"))
                {
                    IosInput.SetCursorPos(primary.Bounds.X + 32, primary.Bounds.Y + primary.Bounds.Height / 2 + 32);
                    IosInput.Mouse(2); IosInput.Mouse(4);
                    IosInput.SetCursorPos(primary.Bounds.X + 20, primary.Bounds.Y + 20);
                    if (!PumpUntil(() => (double)slide.GetValue(dockWindow)! >= hidden - .5, TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("Dock did not hide before the warm-content test.");
                    WriteFixtureCommand(command, "content-blue");
                    Pump(TimeSpan.FromSeconds(1));
                    RecordDockWake("dock-short-hidden-content-change", () => IosInput.SetCursorPos(resting.X + resting.Width / 2, resting.Bottom - 2));
                    IosInput.SetCursorPos(primary.Bounds.X + 32, primary.Bounds.Y + primary.Bounds.Height / 2 + 32);
                    IosInput.Mouse(2); IosInput.Mouse(4);
                    IosInput.SetCursorPos(primary.Bounds.X + 20, primary.Bounds.Y + 20);
                    if (!PumpUntil(() => (double)slide.GetValue(dockWindow)! >= hidden - .5, TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("Dock did not hide before the long-hidden test.");
                    Pump(TimeSpan.FromSeconds(31));
                    RecordDockWake("dock-long-hidden-edge-reveal", () => IosInput.SetCursorPos(resting.X + resting.Width / 2, resting.Bottom - 2));
                }
                if (args.Contains("--dock-reveal-repeat"))
                {
                    for (var repeat = 0; repeat < 8; repeat++)
                    {
                        IosInput.SetCursorPos(primary.Bounds.X + 32, primary.Bounds.Y + primary.Bounds.Height / 2 + 32);
                        IosInput.Mouse(2); IosInput.Mouse(4);
                        IosInput.SetCursorPos(primary.Bounds.X + 20, primary.Bounds.Y + 20);
                        if (!PumpUntil(() => (double)slide.GetValue(dockWindow)! >= hidden - .5, TimeSpan.FromSeconds(5)))
                            throw new InvalidOperationException("Dock did not conceal during repeated reveal " + repeat);
                        WriteFixtureCommand(command, repeat % 2 == 0 ? "content-blue" : "content-red");
                        Pump(TimeSpan.FromMilliseconds(repeat % 3 == 0 ? 250 : repeat % 3 == 1 ? 1000 : 2000));
                        RecordDockWake("dock-repeat-" + repeat, () => IosInput.SetCursorPos(resting.X + resting.Width / 2, resting.Bottom - 2), true);
                    }
                    for (var repeat = 0; repeat < 4; repeat++)
                    {
                        WriteFixtureCommand(command, "cover");
                        Pump(TimeSpan.FromMilliseconds(150));
                        IosInput.SetCursorPos(primary.Bounds.X + 32, primary.Bounds.Y + primary.Bounds.Height / 2 + 32);
                        IosInput.Mouse(2); IosInput.Mouse(4);
                        IosInput.SetCursorPos(primary.Bounds.X + 20, primary.Bounds.Y + 20);
                        if (!PumpUntil(() => (double)slide.GetValue(dockWindow)! >= hidden - .5, TimeSpan.FromSeconds(5)))
                            throw new InvalidOperationException("Dock did not conceal before repeated uncover " + repeat);
                        Pump(TimeSpan.FromMilliseconds(500));
                        RecordDockWake("dock-repeat-uncover-" + repeat, () => WriteFixtureCommand(command, "uncover"), true);
                    }
                }
                observations.Add(new { phase = "dock-reveal", snapshot = Snapshot() });
            }
            finally
            {
                if (fixture is not null) StopFixture();
                if (cursor is { } point) IosInput.SetCursorPos(point.X, point.Y);
            }

            void RecordDockWake(string name, Action wake, bool requireFreshPreparation = false)
            {
                var surface = (LiquidGlassSurface)dockWindow.FindName("GlassSurface")!;
                var sequenceField = typeof(LiquidGlassSurface).GetField("_lastCaptureSequence", PerformanceInstance)!;
                var generationField = typeof(LiquidGlassSurface).GetField("_lastCaptureGeneration", PerformanceInstance)!;
                var frameField = typeof(LiquidGlassSurface).GetField("_frame", PerformanceInstance)!;
                var transitionField = typeof(LiquidGlassSurface).GetField("_transitionBackdrop", PerformanceInstance)!;
                var oldSequence = (long)sequenceField.GetValue(surface)!;
                var oldGeneration = (long)generationField.GetValue(surface)!;
                var preparedField = typeof(LiquidGlassSurface).GetField("_preparedDesktopSnapshots", PerformanceInstance)!;
                var sleepingField = typeof(LiquidGlassSurface).GetField("_performanceSleeping", PerformanceInstance)!;
                var oldPrepared = (int)preparedField.GetValue(surface)!;
                var started = Stopwatch.GetTimestamp();
                double? firstCommitMs = null;
                var startedWaking = false;
                var sleptAgain = false;
                var timeline = new List<object>();
                long lastMetadata = 0;
                (bool Live, bool Sleeping, long Generation)? lastState = null;
                EventHandler observe = (_, _) =>
                {
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var sequence = (long)sequenceField.GetValue(surface)!;
                    var generation = (long)generationField.GetValue(surface)!;
                    var live = frameField.GetValue(surface) is not null || transitionField.GetValue(surface) is not null;
                    var changed = sequence != oldSequence || generation != oldGeneration;
                    var sleeping = (bool)sleepingField.GetValue(surface)!;
                    if (!sleeping) startedWaking = true;
                    else if (startedWaking && firstCommitMs is null) sleptAgain = true;
                    if (firstCommitMs is null && live && changed && surface.IsBackdropActive?.Invoke() == true) firstCommitMs = elapsed;
                    var state = (live, sleeping, generation);
                    var now = Stopwatch.GetTimestamp();
                    // Observe admission and the first commit on every callback,
                    // but never allocate/serialize a complete host and capture
                    // snapshot hundreds of times per second during cold optics.
                    if (state != lastState || now - lastMetadata >= Stopwatch.Frequency / 10)
                    {
                        lastState = state; lastMetadata = now;
                        timeline.Add(new { elapsed, sequence, generation, live, slide = slide.GetValue(dockWindow),
                            host = HostSnapshot(), material = SurfaceSnapshot(dockWindow) });
                    }
                };
                CompositionTarget.Rendering += observe;
                try
                {
                    FirstFrameRecording.Record(dockWindow, () => { started = Stopwatch.GetTimestamp(); wake(); },
                        Path.Combine(evidence, name), TimeSpan.FromSeconds(5), DisplayService.GetWindowPixelBounds(new WindowInteropHelper(dockWindow).Handle));
                }
                finally { CompositionTarget.Rendering -= observe; }
                File.WriteAllText(Path.Combine(evidence, name + "-timing.json"), JsonSerializer.Serialize(new
                { name, firstCommitMs, sleptAgain, oldPrepared, prepared = preparedField.GetValue(surface), note = "Actual production AppHost reveal; composition commit latency is not displayed FPS.", timeline }, new JsonSerializerOptions { WriteIndented = true }));
                if (requireFreshPreparation)
                    Check("重复唤出重新准备当前桌面 " + name, "reveal-sleep/dock-current-content", (int)preparedField.GetValue(surface)! > oldPrepared,
                        "fresh snapshot despite identical HWND scene", oldPrepared + " => " + preparedField.GetValue(surface));
                Check("Dock 正在唤出时不会再次停止采集 " + name, "reveal-sleep/dock-admission", startedWaking && !sleptAgain,
                    "no sleep after wake begins", sleptAgain.ToString());
                Check("Dock 唤出后实时背景及时恢复 " + name, "reveal-sleep/dock-live", firstCommitMs is <= 1000,
                    "fresh composition within 1000 ms", firstCommitMs?.ToString("F1") ?? "no fresh composition");
            }
        }
        void UseProductCaptureWorkers()
        {
            // Match the shipped helper entry point instead of timing the much larger
            // acceptance executable's JIT/startup as though it were product startup.
            var executable = Path.Combine(directory, "GlassDesk.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Product capture executable required", executable);
            foreach (var window in new[] { dock, launcher })
            {
                var surface = (LiquidGlassSurface)window!.FindName("GlassSurface")!;
                var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", PerformanceInstance)!.GetValue(surface)!;
                var feed = (SharedBackdropFeed)typeof(GlassBackgroundLayer).GetField("_compatible", PerformanceInstance)!.GetValue(background)!;
                Func<ScreenRect, bool, Action<CaptureTrace>?, IDesktopSceneCapture> create = (region, _, trace) =>
                {
                    DesktopSceneCaptureFactory.SelectProvider(GlassDesk.Platform.WindowsCapabilities.BuildNumber);
                    return new RecoveringDesktopSceneCapture(region, trace, executable);
                };
                typeof(SharedBackdropFeed).GetField("_create", PerformanceInstance)!.SetValue(feed, create);
            }
        }
        void TimedReveal(string name)
        {
            Phase("reveal-" + name);
            var start = Stopwatch.GetTimestamp();
            var firstVisibleMs = (double?)null;
            hostType.GetMethod("ShowLauncher", PerformanceInstance, null, Type.EmptyTypes, null)!.Invoke(host, null);
            var ready = false;
            var frame = new System.Windows.Threading.DispatcherFrame();
            var observer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
                { Interval = TimeSpan.FromMilliseconds(10) };
            observer.Tick += (_, _) =>
            {
                if (launcher!.IsVisible) firstVisibleMs ??= Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var surface = SurfaceSnapshot(launcher);
                ready = launcher.IsVisible
                    && surface.GetProperty("source").GetString() is "LiveDesktop" or "CompatibleScene"
                    && surface.GetProperty("materialReady").GetBoolean()
                    && !surface.GetProperty("compatibilityReady").GetBoolean();
                if (ready || Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(5)) frame.Continue = false;
            };
            try { observer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { observer.Stop(); }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            PumpUntil(() => launcher!.CanNavigatePages, TimeSpan.FromSeconds(3));
            revealTimings.Add(new { name, ready, firstVisibleMs, dynamicMaterialReadyMs = elapsed,
                navigationReadyMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                visibleToDynamicMs = firstVisibleMs is { } first ? elapsed - first : (double?)null,
                surface = SurfaceSnapshot(launcher!) });
            Check("实时材质恢复-" + name, "reveal-sleep/timing", ready, "fresh live material within 5 seconds", elapsed.ToString("F1") + " ms");
            GlassDesk.UI.BackdropRecovery.ExportDiagnosticReport(Path.Combine(evidence, "capture-timing-" + name + ".json"));
        }
        void SamplePhase(string name, int durationSeconds)
        {
            Phase(name);
            observations.Add(new { phase = name, edge = "start", snapshot = Snapshot() });
            Pump(TimeSpan.FromSeconds(durationSeconds));
            observations.Add(new { phase = name, edge = "end", snapshot = Snapshot() });
        }
        JsonElement HostSnapshot() => SnapshotObject(hostType.GetMethod("InspectPerformance", PerformanceInstance)?.Invoke(host, null));
        string ReadHostState() => HostSnapshot().TryGetProperty("State", out var state) ? state.GetString() ?? "Unknown" : "Unsupported";
        bool ReadHostFlag(string property) => HostSnapshot().TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
        void SetManualSleep(bool sleeping) => (hostType.GetMethod("SetManualGameSleep", PerformanceInstance)
            ?? throw new MissingMethodException("SetManualGameSleep")).Invoke(host, [sleeping]);
        void Reveal(string name, bool checkMaterial)
        {
            hostType.GetMethod("ShowLauncher", PerformanceInstance, null, Type.EmptyTypes, null)!.Invoke(host, null);
            PumpUntil(() => launcher!.IsVisible && launcher.CanNavigatePages, TimeSpan.FromSeconds(3));
            if (!checkMaterial) return;
            PumpUntil(() => SurfaceSnapshot(launcher!).TryGetProperty("firstVisibleRenderMaterialReady", out var first)
                && first.ValueKind is JsonValueKind.True or JsonValueKind.False, TimeSpan.FromSeconds(1));
            var surface = SurfaceSnapshot(launcher!);
            if (!baseline && name == "initial-open")
            {
                var source = surface.GetProperty("firstVisibleMaterialSource").GetString();
                Check("完整宿主首次打开显示当前场景玻璃而非静态壁纸", "performance/current-scene-first-paint",
                    source is "CurrentDesktopSnapshot" or "CompletedLiveMaterial" or "OpaquePreference",
                    "current desktop snapshot or completed live material", source ?? "missing first render");
            }
            materialChecks.Add(new { name, timestamp = DateTimeOffset.Now,
                passed = surface.TryGetProperty("firstVisibleRenderMaterialReady", out var ready) && ready.ValueKind == JsonValueKind.True, surface });
        }
        void CloseLauncher()
        {
            hostType.GetMethod("HideLauncher", PerformanceInstance)!.Invoke(host, null);
            PumpUntil(() => launcher?.IsVisible != true, TimeSpan.FromSeconds(2));
        }
        void RecordReveal(string name)
        {
            var recorder = Assembly.GetExecutingAssembly().GetType("FirstFrameRecording")
                ?? throw new MissingMethodException("FirstFrameRecording.Record is required for visual evidence");
            var action = new Action(() => Reveal(name, true));
            recorder.GetMethod("Record", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [launcher!, action, Path.Combine(evidence, name), TimeSpan.FromSeconds(1)]);
        }
        void VerifyUnavailableCapture()
        {
            var surface = (LiquidGlassSurface)launcher!.FindName("GlassSurface")!;
            var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", PerformanceInstance)!.GetValue(surface)!;
            var field = typeof(GlassBackgroundLayer).GetField("_compatible", PerformanceInstance)!;
            var original = (SharedBackdropFeed)field.GetValue(background)!;
            surface.SetPerformanceSleeping(true);
            Check("捕获失败注入前启动台资源所有者确认退出", "performance/capture-unavailable",
                PumpUntil(() => SnapshotObject(original.InspectPerformance()).GetProperty("activeCaptureOwner").GetInt32() == 0
                    && !SnapshotObject(original.InspectPerformance()).GetProperty("workerAlive").GetBoolean()
                    && SnapshotObject(original.InspectPerformance()).GetProperty("pendingRetirement").GetInt32() == 0,
                    TimeSpan.FromSeconds(2)), "launcher owner retired; a visible Dock may retain its own helper", JsonSerializer.Serialize(original.InspectPerformance()));
            using var unavailable = new SharedBackdropFeed((ScreenRect _, bool _, Action<CaptureTrace>? _) =>
                throw new IOException("Acceptance fixture: native capture is unavailable."));
            try
            {
                field.SetValue(background, unavailable);
                surface.SetPerformanceSleeping(false);
                RecordReveal("capture-unavailable-first-frame");
                Check("实际捕获失败后继续显示兼容玻璃", "performance/capture-unavailable",
                    PumpUntil(() => unavailable.Failure is not null
                        && SurfaceSnapshot(launcher).GetProperty("compatibilityReady").GetBoolean(), TimeSpan.FromSeconds(5)),
                    "capture failed; compatibilityReady=true", SnapshotText());
                observations.Add(new { phase = "capture-unavailable", snapshot = Snapshot() });
            }
            finally
            {
                CloseLauncher(); surface.SetPerformanceSleeping(true);
                field.SetValue(background, original); surface.SetPerformanceSleeping(false);
            }
        }
        object Snapshot() => new { host = HostSnapshot(), launcherVisible = launcher?.IsVisible, dockVisible = dock?.IsVisible,
            launcherSurface = SurfaceSnapshot(launcher), dockSurface = SurfaceSnapshot(dock), feeds = FeedSnapshots(launcher, dock) };
        string SnapshotText() => JsonSerializer.Serialize(Snapshot());
        bool AllBackgroundStopped()
        {
            if (ReadHostState() != "Sleeping" || launcher?.IsVisible == true || dock?.IsVisible == true) return false;
            foreach (var surface in new[] { SurfaceSnapshot(launcher), SurfaceSnapshot(dock) })
                if (!surface.TryGetProperty("sleeping", out var sleeping) || sleeping.ValueKind != JsonValueKind.True
                    || surface.GetProperty("renderClockAttached").ValueKind == JsonValueKind.True
                    || surface.GetProperty("watchdogEnabled").ValueKind == JsonValueKind.True
                    || surface.GetProperty("renderQueued").ValueKind == JsonValueKind.True
                    || surface.GetProperty("refreshQueued").ValueKind == JsonValueKind.True
                    || surface.GetProperty("opticsBusy").ValueKind == JsonValueKind.True) return false;
            return FeedSnapshots(launcher, dock).All(feed => feed.GetProperty("captureWorkerPids").GetArrayLength() == 0
                && feed.GetProperty("activeCaptureOwner").GetInt32() == 0 && feed.GetProperty("pendingRetirement").GetInt32() == 0);
        }
        string CounterSignature() => JsonSerializer.Serialize(FeedSnapshots(launcher, dock).Select(feed => new
        {
            acquisitions = feed.GetProperty("captureAcquisitions").GetInt64(),
            scenes = feed.GetProperty("sceneUpdates").GetInt64(), resizes = feed.GetProperty("resizes").GetInt64(),
            frames = feed.GetProperty("publishedFrames").GetInt64(),
        }));
        void VerifyAutomaticFullscreen()
        {
            foreach (var display in DisplayService.Enumerate())
            {
                Phase("fullscreen-fixture");
                var readyPath = Path.Combine(evidence, "fullscreen-fixture-" + Guid.NewGuid().ToString("N") + ".json");
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing runner process path");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--fullscreen-fixture"); start.ArgumentList.Add("--fixture-display"); start.ArgumentList.Add(display.DeviceName);
                start.ArgumentList.Add("--fixture-ready"); start.ArgumentList.Add(readyPath);
                fixture = Process.Start(start) ?? throw new InvalidOperationException("Fixture process did not start");
                var ready = PumpUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(5));
                var positive = ready && FullscreenDetector.ReadPerformanceObservation().IsFullscreen == true;
                Check("外部无边框全屏前台阳性用例 " + display.DeviceName, "performance/fullscreen", positive,
                    "foreign foreground window covers full monitor", ready ? File.ReadAllText(readyPath) : "fixture not ready");
                if (!positive) { StopFixture(); continue; }
                Check("全屏稳定后自动休眠 " + display.DeviceName, "performance/fullscreen",
                    PumpUntil(() => ReadHostState() == "Sleeping" && AllBackgroundStopped(), TimeSpan.FromSeconds(3)),
                    "Sleeping; background stopped", SnapshotText());
                var own = new Window { Width = 120, Height = 80, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Topmost = true };
                own.Show(); own.Activate();
                var ownBounds = DisplayService.GetWindowPixelBounds(new WindowInteropHelper(own).Handle)!.Value;
                IosInput.SetCursorPos(ownBounds.X + ownBounds.Width / 2, ownBounds.Y + ownBounds.Height / 2);
                IosInput.Mouse(2); IosInput.Mouse(4);
                Check("自身前台阳性用例", "performance/fullscreen", FullscreenDetector.ReadPerformanceObservation().BelongsToCurrentProcess,
                    "own window really owns foreground", GetPerformanceForegroundWindow().ToString());
                Pump(TimeSpan.FromMilliseconds(450));
                Check("自身短暂前台不会误判游戏结束", "performance/fullscreen", ReadHostState() == "Sleeping",
                    "Sleeping", ReadHostState());
                own.Close();
                Reveal("fullscreen-temporary-open-" + display.DeviceName, true);
                Check("全屏时键盘/托盘显示路径临时唤醒", "performance/fullscreen", ReadHostState() == "TemporaryActive",
                    "TemporaryActive", ReadHostState());
                CloseLauncher();
                Check("关闭后重新进入全屏休眠", "performance/fullscreen",
                    PumpUntil(() => ReadHostState() == "Sleeping", TimeSpan.FromSeconds(2)), "Sleeping", ReadHostState());
                StopFixture();
                SetPerformanceForegroundWindow(previousForeignForeground);
                // An own foreground window is intentionally ignored; restore the original foreign desktop/application.
                Check("全屏应用结束后恢复正常策略", "performance/fullscreen",
                    PumpUntil(() => !ReadHostFlag("SuppressionRequested"), TimeSpan.FromSeconds(4)),
                    "SuppressionRequested=false after foreign foreground observation", SnapshotText());
            }
        }
        void StopFixture()
        {
            if (fixture is null) return;
            try { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } }
            finally { fixture.Dispose(); fixture = null; }
        }
    }

    private static bool MaterialCheckPassed(object value) => SnapshotObject(value).GetProperty("passed").GetBoolean();
    private static JsonElement SnapshotObject(object? value) => JsonSerializer.SerializeToElement(value ?? new { supported = false });
    private static JsonElement SurfaceSnapshot(Window? window)
    {
        if (window?.FindName("GlassSurface") is not LiquidGlassSurface surface) return SnapshotObject(null);
        return SnapshotObject(surface.GetType().GetMethod("InspectPerformance", PerformanceInstance)?.Invoke(surface, null));
    }
    private static JsonElement[] FeedSnapshots(params Window?[] windows)
    {
        var snapshots = new List<JsonElement>();
        foreach (var window in windows)
        {
            if (window?.FindName("GlassSurface") is not LiquidGlassSurface surface) continue;
            var background = surface.GetType().GetField("_background", PerformanceInstance)!.GetValue(surface)!;
            foreach (var name in new[] { "_compatible", "_wallpaperFeed" })
            {
                var feed = background.GetType().GetField(name, PerformanceInstance)?.GetValue(background);
                var inspect = feed?.GetType().GetMethod("InspectPerformance", PerformanceInstance);
                if (inspect is not null) snapshots.Add(SnapshotObject(inspect.Invoke(feed, null)));
            }
        }
        return snapshots.ToArray();
    }

    private static int RunPerformanceFullscreenFixture(string[] args)
    {
        var display = DisplayService.Enumerate().FirstOrDefault(value => value.DeviceName == Argument(args, "--fixture-display"))
            ?? DisplayService.Primary() ?? throw new InvalidOperationException("No monitor");
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Topmost = !args.Contains("--fixture-cover-dock"), Background = new SolidColorBrush(Color.FromRgb(16, 24, 32)),
            Content = new TextBlock { Text = "GlassDesk 全屏避让验收", FontSize = 24, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        window.Show();
        var hwnd = new WindowInteropHelper(window).Handle;
        var bounds = args.Contains("--fixture-cover-dock")
            ? new PixelRect(display.Bounds.X, display.Bounds.Y + display.Bounds.Height / 2, display.Bounds.Width, display.Bounds.Height / 2)
            : display.Bounds;
        DisplayService.MoveWindowToPixelRect(hwnd, bounds);
        SetPerformanceWindowPos(hwnd, new nint(args.Contains("--fixture-cover-dock") ? -2 : -1),
            bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
        window.Activate(); SetPerformanceForegroundWindow(hwnd);
        Pump(TimeSpan.FromMilliseconds(150));
        if (GetPerformanceForegroundWindow() != hwnd)
        {
            var savedCursor = DisplayService.GetCursorPixelPoint();
            IosInput.SetCursorPos(bounds.X + 32, bounds.Y + 32);
            IosInput.Mouse(2); IosInput.Mouse(4);
            Pump(TimeSpan.FromMilliseconds(150));
            if (savedCursor is { } point) IosInput.SetCursorPos(point.X, point.Y);
        }
        if (Argument(args, "--fixture-ready") is { } ready)
            File.WriteAllText(ready, JsonSerializer.Serialize(new { processId = Environment.ProcessId, hwnd = hwnd.ToInt64(),
                display = display.DeviceName, desired = bounds, actual = DisplayService.GetWindowPixelBounds(hwnd),
                foreground = GetPerformanceForegroundWindow() == hwnd }));
        System.Windows.Threading.DispatcherTimer? mover = null;
        if (Argument(args, "--fixture-command") is { } commands)
        {
            mover = new() { Interval = TimeSpan.FromMilliseconds(50) };
            var previous = "cover";
            mover.Tick += (_, _) =>
            {
                string next;
                try
                {
                    using var input = new FileStream(commands, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(input); next = reader.ReadToEnd();
                }
                catch (IOException) { return; }
                if (next == previous) return;
                previous = next;
                if (next is "content-blue" or "content-red")
                {
                    window.Background = new SolidColorBrush(next == "content-blue" ? Color.FromRgb(40, 100, 180) : Color.FromRgb(180, 50, 35));
                    return;
                }
                var target = next == "cover" ? bounds
                    : new PixelRect(display.Bounds.X, display.Bounds.Y, display.Bounds.Width, display.Bounds.Height / 3);
                DisplayService.MoveWindowToPixelRect(hwnd, target);
            };
            mover.Start();
        }
        application.Run();
        mover?.Stop();
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPerformanceWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPerformanceForegroundWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetPerformanceForegroundWindow();

    private static void WriteFixtureCommand(string path, string command)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, command);
        File.Move(temporary, path, overwrite: true);
    }
}
