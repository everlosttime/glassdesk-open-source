using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;
using GlassDesk.Rendering;

internal static partial class Program
{
    private static int CaptureUnifiedFrames(string[] args)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var directory = Path.GetDirectoryName(Path.GetFullPath(Argument(args, "--report")!))!;
        if (Directory.Exists(directory)) throw new IOException("Use a fresh output directory.");
        Directory.CreateDirectory(directory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        using var theme = ThemeService.Initialize(app.Dispatcher);
        var data = new GlassDeskDataStore(new AppPaths(Path.Combine(directory, "Data"))); data.LoadAll();
        data.Settings.Current.Appearance.GlassMaterial = GlassMaterialMode.AndroidLiquidGlass;
        data.Settings.Current.Launcher.FocusSearchOnOpen = false;
        theme.Update(data.Settings.Current.Appearance);
        for (var i = 0; i < 36; i++) data.Library.Add(new AppEntry { Id = $"frame-{i}", DisplayName = $"应用 App {i:00}", Target = Path.Combine(directory, $"missing-{i}"), IsTargetMissing = true });
        data.Library.Save(); data.Settings.Save();
        using var services = new AppServiceHost(data);
        var display = DisplayService.Primary()!;
        var window = new LauncherWindow(); window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
        window.ShowActivated = false;
        window.AttachServices(services, true); window.ApplySettings(data.Settings.Current.Launcher);
        typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", flags)!.SetValue(window, 1);
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
        UseIsolatedBackdrop(window, Argument(args, "--capture-exe") ?? "D:\\GlassDesk\\GlassDesk.exe");
        if (!args.Contains("--live-source"))
        {
        var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
        surface.CaptureRevealBackdrop = (_, w, h) =>
        {
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            { var i = (y * w + x) * 4; pixels[i] = (byte)(40 + (x / 24 % 2) * 90); pixels[i+1] = (byte)(50 + (y / 24 % 2) * 100); pixels[i+2] = (byte)(80 + x % 140); pixels[i+3] = 255; }
            return pixels;
        };
        surface.IsBackdropActive = () => false;
        }
        var samples = new List<object>();
        var input = new List<object>();
        UnifiedInputPeer? peer = null;
        try
        {
            var anchor = new PixelRect(display.WorkArea.X + display.WorkArea.Width / 2 - 24, display.WorkArea.Bottom - 76, 48, 48);
            if (args.Contains("--monitor-motion"))
            {
                using var result = new MotionWindowScope(window, display, anchor, directory, args.Contains("--cold-opening"),
                    int.Parse(Argument(args, "--sleep-seconds") ?? "0"), int.Parse(Argument(args, "--motion-cycles") ?? "5"),
                    int.Parse(Argument(args, "--delay-material-ms") ?? "0"));
                return 0;
            }
            window.ShowWithTransition(anchor);
            typeof(LauncherWindow).GetMethod("StopContainerTransform", flags)!.Invoke(window, ["diagnostic-paused"]);
            Pump(TimeSpan.FromMilliseconds(80));
            EnableUnifiedDiagnosticReadback(window);
            var destination = (PixelRect)typeof(LauncherWindow).GetField("_transformDestinationBounds", flags)!.GetValue(window)!;
            if (args.Contains("--probe-input")) peer = new UnifiedInputPeer(directory, display, destination);
            var motion = new ContainerMorphMotion(anchor, destination, 16, window.PanelCornerRadius.TopLeft * display.Dpi / 96);
            motion.Retarget(true);
            var previous = 0d;
            foreach (var time in new[] { .0, .04, .08, .12, .18, .3, .55 })
            {
                motion.Advance(time - previous); previous = time;
                var frame = motion.CurrentFrame;
                typeof(LauncherWindow).GetMethod("ApplyContainerTransformFrame", flags)!.Invoke(window, [frame]);
                Pump(TimeSpan.FromMilliseconds(35));
                typeof(LauncherWindow).GetMethod("ApplyContainerTransformFrame", flags)!.Invoke(window, [frame]);
                Pump(TimeSpan.FromMilliseconds(35));
                var image = CaptureOwnerAndShadow(window);
                object? displayCheck = null;
                if (args.Contains("--probe-display") && time >= .08)
                {
                    var output = GlassDesk.NativeRenderingProbe.OwnedWindowMonitorCrop.Capture(new WindowInteropHelper(window).Handle, image.Width, image.Height);
                    var histogram = new int[256]; var tested = 0;
                    for (var y = 8; y < image.Height - 8; y += 3) for (var x = 8; x < image.Width - 8; x += 3)
                    {
                        var at = (y * image.Width + x) * 4;
                        if (image.Pixels[at + 3] != 255 || image.Pixels[at - 32 + 3] != 255 || image.Pixels[at + 32 + 3] != 255) continue;
                        var delta = 0;
                        for (var c = 0; c < 3; c++) delta = Math.Max(delta, Math.Abs(output.Pixels[at+c] - image.Pixels[at+c]));
                        histogram[delta]++; tested++;
                    }
                    displayCheck = new { tested, within3 = histogram.Take(4).Sum(), above16 = histogram.Skip(17).Sum() };
                }
                var name = $"frame-{time:F2}";
                WriteCompositionPng(Path.Combine(directory, name + ".png"), image);
                File.WriteAllBytes(Path.Combine(directory, name + ".bgra"), image.Pixels);
                samples.Add(new { time, frame, image.Width, image.Height, displayCheck, bounds = DisplayService.GetWindowPixelBounds(new WindowInteropHelper(window).Handle),
                    unified = typeof(LauncherWindow).GetMethod("InspectUnifiedTransition", flags)!.Invoke(window, null) });
            }
            if (peer is not null)
            {
                input.Add(ProbeUnifiedInput(window, peer, "native-transition"));
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", flags)!.Invoke(window, [1d]);
                Pump(TimeSpan.FromMilliseconds(150));
                input.Add(ProbeUnifiedInput(window, peer, "resting-handoff"));
            }
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new { samples, input, controlledSource = true, display }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { peer?.Dispose(); window.Close(); }
        return 0;
    }

    private sealed class MotionWindowScope : IDisposable
    {
        private readonly Window _underlay;
        internal MotionWindowScope(LauncherWindow window, DisplayInfo display, PixelRect anchor, string directory, bool coldOpening, int sleepSeconds, int cycles, int delayMaterialMs)
        {
            var scale=96d/display.Dpi;
            _underlay=new Window { Title="Owned magenta animation underlay",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=true,
                Left=display.WorkArea.X*scale,Top=display.WorkArea.Y*scale,Width=display.WorkArea.Width*scale,Height=display.WorkArea.Height*scale,Topmost=true,
                Background=System.Windows.Media.Brushes.Magenta };
            _underlay.Show();
            _underlay.Activate();
            window.Topmost = true;
            if(!coldOpening)
            {
                window.ShowWithTransition(anchor);Pump(TimeSpan.FromMilliseconds(1000));
                window.CloseWithTransition();Pump(TimeSpan.FromMilliseconds(700));
            }
            var owner=new WindowInteropHelper(window).EnsureHandle();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var pending = delayMaterialMs > 0 ? new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne()) : null;
            if (pending is not null)
            {
                var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
                var background = typeof(LiquidGlassSurface).GetField("_background", BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
                typeof(GlassBackgroundLayer).GetField("_compatible", BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(background, pending);
                surface.IsBackdropActive = () => false;
            }
            var admission = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal);
            admission.Tick += (_, _) =>
            {
                admission.Stop();
                surface.CaptureRevealBackdrop = (_, w, h) => Enumerable.Repeat((byte)100, w * h * 4).ToArray();
                const BindingFlags fields = BindingFlags.Instance|BindingFlags.NonPublic;
                var host = (PixelRect)typeof(LauncherWindow).GetField("_transformHostBounds", fields)!.GetValue(window)!;
                var panel = (PixelRect)typeof(LauncherWindow).GetField("_panelPixelBounds", fields)!.GetValue(window)!;
                surface.PrepareRevealMaterial(host, panel, display.Dpi);
            };
            var x=display.WorkArea.X+display.WorkArea.Width/2-display.Bounds.X;
            using var sampler=new MonitorCaptureReference.MotionSampler(owner,new PixelBounds(x,display.WorkArea.Y-display.Bounds.Y,x+2,display.WorkArea.Bottom-display.Bounds.Y-128));
            Pump(TimeSpan.FromMilliseconds(150));
            var operations=new List<object>();
            for(var cycle=0;cycle<cycles;cycle++)foreach(var opening in new[]{true,false})
            {
                if(opening && (cycle>0 && sleepSeconds>0 || delayMaterialMs>0))
                {
                    if (delayMaterialMs > 0) surface.CaptureRevealBackdrop = (_, _, _) => null;
                    window.SetPerformanceSleeping(true); window.SetBackgroundWorkSuspended(true);
                    if (cycle>0 && sleepSeconds>0) Pump(TimeSpan.FromSeconds(sleepSeconds));
                    window.SetPerformanceSleeping(false); window.SetBackgroundWorkSuspended(false);
                }
                if (opening && delayMaterialMs>0) { admission.Interval=TimeSpan.FromMilliseconds(delayMaterialMs); admission.Start(); }
                if (opening) { _underlay.Activate(); Pump(TimeSpan.FromMilliseconds(150)); }
                var baselineSample = sampler.Snapshot().LastOrDefault();
                var start=System.Diagnostics.Stopwatch.GetTimestamp();
                if(opening)window.ShowWithTransition(anchor);else window.CloseWithTransition();
                var settled=PumpUntil(()=>opening?window.IsVisible && !(bool)typeof(LauncherWindow).GetField("_transformActive",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!:!window.IsVisible,TimeSpan.FromSeconds(3));
                var settledAt=System.Diagnostics.Stopwatch.GetTimestamp();
                Pump(TimeSpan.FromMilliseconds(coldOpening?200:120));
                var end=coldOpening?System.Diagnostics.Stopwatch.GetTimestamp():settledAt;
                var samples=sampler.Snapshot().Where(s=>s.SourceQpc>=start && s.SourceQpc<=end).ToArray();
                var changes=samples.Where((s,i)=>i==0 || s.Top!=samples[i-1].Top || s.Bottom!=samples[i-1].Bottom).ToArray();
                operations.Add(new{cycle,opening,start,end,settledAt,settled,sleepSeconds,delayMaterialMs,baselineSample,samples,changes,
                    unified=typeof(LauncherWindow).GetMethod("InspectUnifiedTransition",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)});
            }
            File.WriteAllText(Path.Combine(directory,"motion.json"),JsonSerializer.Serialize(new{operations,sampler.Failure,frequency=System.Diagnostics.Stopwatch.Frequency,display,scope="WGC monitor strip over owned magenta underlay; geometry changes, controlled source; observer may affect cadence"},new JsonSerializerOptions{WriteIndented=true}));
            admission.Stop();
            _underlay.Close();
        }
        public void Dispose()=>_underlay.Close();
    }

    private static CapturePixels CaptureOwnerAndShadow(LauncherWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var status = JsonSerializer.SerializeToElement(typeof(LauncherWindow).GetMethod("InspectUnifiedTransition", flags)!.Invoke(window, null));
        if (status.GetProperty("active").GetBoolean()) return ReadUnifiedTarget(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        _ = DiagnosticSetDisplayAffinity(hwnd, 0);
        var owner = WgcWindowCapture.Capture(hwnd, TimeSpan.FromSeconds(3)) ?? throw new InvalidOperationException("Owner capture missing.");
        var shadow = (GlassShadow?)typeof(GlassWindow).GetField("_shadow", flags)!.GetValue(window);
        if (shadow?.IsVisible != true) return owner;
        // The real independent shadow HWND rejects WGC on this driver. Render
        // the exact production recipe into an explicitly diagnostic owned target;
        // this comparison concerns pixels, not shadow HWND presentation timing.
        var raw = MaterialMemoryNative.CreateWindowExW(0x00200080, "STATIC", "Owned shadow reference", 0x80000000, 0, 0, owner.Width, owner.Height, 0, 0, 0, 0);
        CapturePixels background;
        try
        {
            var (presenter, handle) = CreateDiagnosticMaterialPresenter(raw, owner.Width, owner.Height);
            using (presenter)
            {
                var panel = (Rect)typeof(LauncherWindow).GetField("_transitionPanelBoundsDip", flags)!.GetValue(window)!;
                var radius = (double)typeof(LauncherWindow).GetField("_transitionPanelRadiusDip", flags)!.GetValue(window)!;
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                var layers = new DirectShadowLayer[4];
                var count = GlassShadow.CopyDirectLayers(layers, LiquidGlassDebugState.View, LiquidGlassDebugState.FinishingParameters, 1);
                presenter.RenderShadow(new(new(window.ActualWidth, window.ActualHeight), panel, (float)radius, (float)(.35 + .65 * surface.TransitionProgress), true, true), layers.AsSpan(0, count));
                var pixels = new byte[owner.Pixels.Length]; var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                var library = NativeLibrary.Load(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_RENDER_DLL")!);
                try
                {
                    var read = Marshal.GetDelegateForFunctionPointer<MaterialReadPixels>(NativeLibrary.GetExport(library, "GD_PresenterReadPixels"));
                    Marshal.ThrowExceptionForHR(read(handle.DangerousGetHandle(), pin.AddrOfPinnedObject(), (ulong)pixels.Length, (uint)owner.Width * 4));
                }
                finally { NativeLibrary.Free(library); pin.Free(); }
                background = new(owner.Width, owner.Height, pixels);
            }
        }
        finally { MaterialMemoryNative.DestroyWindow(raw); }
        const int dx = 0, dy = 0;
        for (var y = 0; y < owner.Height; y++) for (var x = 0; x < owner.Width; x++)
        {
            var sx = x + dx; var sy = y + dy;
            if (sx < 0 || sy < 0 || sx >= background.Width || sy >= background.Height) continue;
            var index = (y * owner.Width + x) * 4; var other = (sy * background.Width + sx) * 4;
            var inverse = 255 - owner.Pixels[index + 3];
            for (var c = 0; c < 4; c++) owner.Pixels[index + c] = (byte)Math.Min(255, owner.Pixels[index + c] + (background.Pixels[other + c] * inverse + 127) / 255);
        }
        return owner;
    }
    private static void EnableUnifiedDiagnosticReadback(LauncherWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var status = JsonSerializer.SerializeToElement(typeof(LauncherWindow).GetMethod("InspectUnifiedTransition", flags)!.Invoke(window, null));
        if (!status.GetProperty("active").GetBoolean()) return;
        typeof(LauncherWindow).GetMethod("DisposeUnifiedClock", flags)!.Invoke(window, null);
        var field = typeof(LauncherWindow).GetField("_unifiedPresenter", flags)!;
        ((IDisposable)field.GetValue(window)!).Dispose();
        var (presenter, _) = CreateDiagnosticMaterialPresenter((nint)status.GetProperty("host").GetInt64(), status.GetProperty("targetWidth").GetInt32(), status.GetProperty("targetHeight").GetInt32());
        field.SetValue(window, presenter);
        typeof(LauncherWindow).GetField("_unifiedBackdrop", flags)!.SetValue(window, null);
        typeof(LauncherWindow).GetField("_unifiedOverlay", flags)!.SetValue(window, null);
        typeof(LauncherWindow).GetMethod("CaptureUnifiedForeground", flags)!.Invoke(window, null);
    }
    private static CapturePixels ReadUnifiedTarget(LauncherWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var presenter = (NativeDirectPresenter)typeof(LauncherWindow).GetField("_unifiedPresenter", flags)!.GetValue(window)!;
        var handle = (SafeHandle)typeof(NativeDirectPresenter).GetField("_handle", flags)!.GetValue(presenter)!;
        var dimensions = presenter.Statistics;
        var pixels = new byte[checked((int)(dimensions.Width * dimensions.Height * 4))];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var library = NativeLibrary.Load(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_RENDER_DLL")!);
        try
        {
            var read = Marshal.GetDelegateForFunctionPointer<MaterialReadPixels>(NativeLibrary.GetExport(library, "GD_PresenterReadPixels"));
            Marshal.ThrowExceptionForHR(read(handle.DangerousGetHandle(), pin.AddrOfPinnedObject(), (ulong)pixels.Length, dimensions.Width * 4));
        }
        finally { NativeLibrary.Free(library); pin.Free(); }
        return new((int)dimensions.Width, (int)dimensions.Height, pixels);
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowDisplayAffinity")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DiagnosticSetDisplayAffinity(nint window, uint affinity);
}
