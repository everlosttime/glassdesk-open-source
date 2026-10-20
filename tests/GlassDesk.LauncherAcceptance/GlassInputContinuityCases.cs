using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyGlassInputContinuity(LauncherWindow window, UniformGrid grid, string evidence, string? fixtureExe, bool nativePageClicks = false)
    {
        var fixtures = new List<Process>();
        nint unrelatedWindow = 0;
        Process StartFixture(string name)
        {
            var readyPath = Path.Combine(evidence, name + ".json");
            var start = new ProcessStartInfo(fixtureExe!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--moving-backdrop-fixture"); start.ArgumentList.Add(readyPath);
            var process = Process.Start(start)!; fixtures.Add(process);
            if (!PumpUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Fixture did not start.");
            return process;
        }
        if (fixtureExe is not null)
        {
            StartFixture("dynamic-background");
            StartFixture("unrelated-window");
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "unrelated-window.json")));
            unrelatedWindow = (nint)ready.RootElement.GetProperty("windowHandle").GetInt64();
            SetContinuityWindowPos(unrelatedWindow, 0, -10000, -10000, 100, 100, 0x14);
        }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        object? Read(string name) => typeof(LiquidGlassSurface).GetField(name, fields)!.GetValue(surface);
        bool Ready()
        {
            var panel = Read("_panelShape") is Rect shape ? shape : new Rect(surface.RenderSize);
            var background = (GlassBackgroundLayer)Read("_background")!;
            if (background.RequiresBasicBackground || GlassDesk.UI.Theming.ThemeService.Current?.UseOpaquePanels == true) return false;
            var radius = Read("_panelShape") is null ? window.PanelCornerRadius.TopLeft : (double)Read("_panelRadius")!;
            var frameMatches = Read("_frame") is BitmapSource
                && Read("_sampledGlassPanelBounds") is Rect sampled && sampled == panel
                && (double)Read("_sampledGlassPanelRadius")! == radius;
            if (frameMatches && Read("_transitionCaptureBounds") is null) return true;
            var layer = (FrameworkElement)Read("_transitionLayer")!;
            return layer.Opacity == 1 && Read("_transitionBackdrop") is BitmapSource
                && Read("_transitionBackdropBounds") is Rect bounds && bounds.Contains(panel);
        }
        string Scene()
        {
            var scene = Read("_backdropScene");
            if (scene is null) return "unverified";
            var windows = (Array)scene.GetType().GetField("_windows", fields)!.GetValue(scene)!;
            return string.Join(";", windows.Cast<object>().Select(w => w.ToString()));
        }
        // The product opens through the shared container transition, not Window.Show.
        var display = window.PrimaryDisplaySession!.Display;
        window.CloseWithTransition(); Pump(TimeSpan.FromMilliseconds(500));
        window.ShowWithTransition(new GlassDesk.Platform.Displays.PixelRect(display.WorkArea.X + 24, display.WorkArea.Bottom - 88, 48, 64));
        window.Activate();
        bool OpenAndReady() => !(bool)typeof(LauncherWindow).GetField("_transformActive", fields)!.GetValue(window)! && Ready();
        Check("输入前展开完成且完整玻璃已就绪", "glass/input", PumpUntil(OpenAndReady, TimeSpan.FromSeconds(6)), "settled container and complete material", surface.BackdropSource.ToString());
        var rows = new List<object>();
        var gaps = new Dictionary<string, List<double>>();
        var missing = new Dictionary<string, int>();
        var samples = new Dictionary<string, int>();
        var opticalTicks = new Dictionary<string, List<long>>();
        var observations = new List<object>();
        long lastOptical = 0;
        var phase = "idle";
        var observedCapturedDrag = false;
        var pageChanges = 0;
        var previous = Stopwatch.GetTimestamp();
        string? lastScene = null;
        EventHandler observe = (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (!gaps.TryGetValue(phase, out var times)) gaps[phase] = times = [];
            times.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now;
            samples[phase] = samples.GetValueOrDefault(phase) + 1;
            observedCapturedDrag |= typeof(LauncherWindow).GetField("_logicalIconDrag", fields)!.GetValue(window) is true;
            var latest = ((Queue<long>)Read("_opticalFrameTimestamps")!).LastOrDefault();
            if (latest != 0 && latest != lastOptical)
            {
                if (!opticalTicks.TryGetValue(phase, out var ticks)) opticalTicks[phase] = ticks = [];
                ticks.Add(latest); lastOptical = latest;
                observations.Add(new { phase, atTicks = latest, opticsMilliseconds = Read("_lastOpticsMilliseconds") });
            }
            var ready = Ready();
            if (!ready) missing[phase] = missing.GetValueOrDefault(phase) + 1;
            var scene = Scene();
            if (scene != lastScene || !ready)
                rows.Add(new { phase, atTicks = now, ready, source = surface.BackdropSource.ToString(), scene,
                    panel = Read("_panelShape")?.ToString(), sampled = Read("_sampledGlassPanelBounds")?.ToString(),
                    transitionBounds = Read("_transitionBackdropBounds")?.ToString(),
                    basicBackground = ((GlassBackgroundLayer)Read("_background")!).RequiresBasicBackground,
                    layerOpacity = ((FrameworkElement)Read("_transitionLayer")!).Opacity,
                    hasFrame = Read("_frame") is not null, hasTransition = Read("_transitionBackdrop") is not null });
            lastScene = scene;
        };
        CompositionTarget.Rendering += observe;
        try
        {
            Pump(TimeSpan.FromMilliseconds(1500));
            if (unrelatedWindow != 0)
            {
                phase = "unrelated-window-move";
                for (var i = 0; i < 8; i++)
                {
                    SetContinuityWindowPos(unrelatedWindow, 0, -10000 + i * 30, -10000, 100, 100, 0x14);
                    Pump(TimeSpan.FromMilliseconds(120));
                }
            }
            var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
            var end = grid.PointToScreen(new Point(grid.ActualWidth * .65, grid.ActualHeight * .5));
            phase = "pointer-move";
            var moving = Task.Run(async () =>
            {
                for (var i = 0; i < 360; i++)
                {
                    var t = i % 60 / 59d;
                    IosInput.SetCursorPos((int)(start.X + (end.X - start.X) * t), (int)(start.Y + (end.Y - start.Y) * t));
                    await Task.Delay(8);
                }
            });
            PumpUntil(() => moving.IsCompleted, TimeSpan.FromSeconds(6)); moving.GetAwaiter().GetResult();
            phase = "drag";
            IosInput.SetCursorPos((int)start.X, (int)start.Y); IosInput.Mouse(2);
            Pump(TimeSpan.FromMilliseconds(450));
            var dragging = Task.Run(async () =>
            {
                for (var i = 0; i < 120; i++)
                {
                    var t = i % 40 / 39d;
                    IosInput.SetCursorPos((int)(start.X + (end.X - start.X) * t), (int)(start.Y + (end.Y - start.Y) * t));
                    await Task.Delay(10);
                }
                IosInput.Mouse(4);
            });
            PumpUntil(() => dragging.IsCompleted, TimeSpan.FromSeconds(6)); dragging.GetAwaiter().GetResult();
            phase = "after-drop"; Pump(TimeSpan.FromMilliseconds(350));
            phase = "page-after-drag";
            for (var i = 0; i < 6; i++)
            {
                var button = (Button)window.FindName(i % 2 == 0 ? "NextPage" : "PreviousPage")!;
                var pageText = (TextBlock)window.FindName("PageText")!;
                var before = pageText.Text;
                if (nativePageClicks)
                {
                    var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    IosInput.SetCursorPos((int)center.X, (int)center.Y); IosInput.Mouse(2); IosInput.Mouse(4);
                }
                else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(TimeSpan.FromMilliseconds(350));
                if (pageText.Text != before) pageChanges++;
            }
        }
        finally
        {
            IosInput.Mouse(4); CompositionTarget.Rendering -= observe;
            foreach (var fixture in fixtures) { if (!fixture.HasExited) fixture.Kill(); fixture.Dispose(); }
        }
        foreach (var pair in samples)
            Check($"{pair.Key} 保持完整玻璃", "glass/input", missing.GetValueOrDefault(pair.Key) == 0,
                "0 missing material callbacks", $"missing={missing.GetValueOrDefault(pair.Key)},samples={pair.Value}");
        Check("真实鼠标长按进入图标拖动", "glass/input/gesture", observedCapturedDrag, "production captured drag", observedCapturedDrag.ToString());
        Check("拖动后六次翻页实际切换页面", "glass/input/gesture", pageChanges == 6, "6", pageChanges.ToString());
        if (fixtureExe is not null)
        {
            double Rate(string name)
            {
                var ticks = opticalTicks.GetValueOrDefault(name) ?? [];
                return ticks.Count < 2 ? 0 : (ticks.Count - 1) / Stopwatch.GetElapsedTime(ticks[0], ticks[^1]).TotalSeconds;
            }
            var idleRate = Rate("idle"); var movingRate = Rate("pointer-move");
            Check("鼠标移动时动态背景保持刷新", "glass/input/cadence", idleRate > 0 && movingRate >= idleRate * .6,
                "至少保持本机静止背景刷新率的60%，光学更新次数不是显示FPS", $"idle={idleRate:F2},moving={movingRate:F2}");
        }
        File.WriteAllText(System.IO.Path.Combine(evidence, "glass-input-continuity.json"), JsonSerializer.Serialize(new
        {
            note = "Real mouse movement and captured icon drag, then production page buttons. Rendering callback times are not displayed FPS.",
            samples, missing, callbackGapsMilliseconds = gaps, opticalUpdateTicks = opticalTicks, observations,
            observedCapturedDrag, pageChanges, captureTrace = _isolatedTrace?.Snapshot(), materialChanges = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetContinuityWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
