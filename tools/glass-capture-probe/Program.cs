using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--background")) return Background(args.Contains("--full-screen"));
        if (args is ["--export-android-glass", var androidOutputDirectory])
            return AndroidLiquidGlassMatrix.Run(androidOutputDirectory);
        if (args is ["--export-android-glass", var clearOutputDirectory, var previousOutputDirectory])
            return AndroidLiquidGlassMatrix.Run(clearOutputDirectory, previousOutputDirectory);
        if (args is ["--export-chapter05-debug", var debugOutputDirectory])
            return Chapter05DebugMatrix.Run(debugOutputDirectory);
        if (args is ["--export-chapter06-weights", var weightOutputDirectory])
            return Chapter06WeightMatrix.Run(weightOutputDirectory);
        if (args is ["--export-chapter08-finishing", var finishingOutputDirectory])
            return Chapter08FinishingMatrix.Run(finishingOutputDirectory);
        if (args is ["--export-chapter09-brdf", var brdfOutputDirectory])
            return Chapter09BrdfMatrix.Run(brdfOutputDirectory);
        if (args is ["--export-chapter10-dispersion", var dispersionOutputDirectory])
            return Chapter10DispersionMatrix.Run(dispersionOutputDirectory);
        if (args is ["--export-final-matrix", var outputDirectory])
            return FinalFrameMatrix.Run(outputDirectory);
        using var probeTheme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        probeTheme.Update(new GlassDesk.Core.Models.Settings.AppearanceSettings
        {
            GlassMaterial = args.Contains("--android-glass")
                ? GlassDesk.Core.Models.GlassMaterialMode.AndroidLiquidGlass
                : GlassDesk.Core.Models.GlassMaterialMode.PhysicalGlass,
        });
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--background")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true
        };
        using var child = Process.Start(start)!;
        Window? front = null;
        try
        {
            var ready = child.StandardOutput.ReadLine();
            if (ready is null || !ready.StartsWith("READY|", StringComparison.Ordinal))
                throw new InvalidOperationException("Background unavailable: " + ready);
            front = new Window { Title = "GlassDesk controlled layered capture foreground", Width = 240, Height = 160,
                Left = SystemParameters.VirtualScreenLeft + 100, Top = SystemParameters.VirtualScreenTop + 100,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false, Topmost = true, Background = Brushes.Magenta };
            front.Show(); Pump();
            var point = front.PointToScreen(new Point(60, 60));
            var region = new PixelRect((int)point.X, (int)point.Y, 32, 32);
            using var sampler = new DesktopRegionSampler();
            Require(!sampler.TryCapture(region, 16, 16, out _), "Unregistered self capture is blocked");
            var handle = new WindowInteropHelper(front).Handle;
            using (var exclusion = CaptureExclusionSession.TryCreate(handle))
            {
                Require(exclusion is not null && CaptureExclusionSession.CanCapture, "Layered HWND exclusion verified");
                Pump();
                Require(sampler.TryCapture(region, 16, 16, out var green) && IsColor(green, 0, 255, 0), "Production sampler reads green background through excluded magenta window");
                child.StandardInput.WriteLine("blue"); child.StandardInput.Flush();
                Require(child.StandardOutput.ReadLine() == "BLUE", "Background changed"); Pump();
                Require(sampler.TryCapture(region, 16, 16, out var blue) && IsColor(blue, 255, 0, 0), "Production sampler sees live background change to blue");
                // A new own-process popup must block sampling even though the glass is excluded.
                var popup = new Window { Width = 100, Height = 100, Left = front.Left + 40, Top = front.Top + 40,
                    ShowActivated = false, ShowInTaskbar = false, Topmost = true, Background = Brushes.Red };
                try { popup.Show(); Pump(); Require(!sampler.TryCapture(region, 16, 16, out _), "Unregistered overlapping popup blocks feedback"); }
                finally { popup.Close(); }
                Pump(); Require(sampler.TryCapture(region, 16, 16, out _), "Capture resumes after popup closes");
            }
            Require(!sampler.TryCapture(region, 16, 16, out _), "Capture stops after exclusion disposal");
            front.Close(); front = null;
            child.StandardInput.WriteLine("green"); child.StandardInput.Flush(); Require(child.StandardOutput.ReadLine() == "GREEN", "Reset background");
            var surface = new LiquidGlassSurface();
            var glass = new ProbeGlass { Width = 240, Height = 160, Left = SystemParameters.VirtualScreenLeft + 100,
                Top = SystemParameters.VirtualScreenTop + 100, ShowActivated = false, ShowInTaskbar = false, Topmost = true, Content = surface };
            front = glass; glass.Show(); Pump(); Pump();
            var frameField = typeof(LiquidGlassSurface).GetField("_frame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var frameWait = Stopwatch.StartNew();
            while (frameField.GetValue(surface) is null && frameWait.Elapsed < TimeSpan.FromSeconds(6)) Pump();
            Require(surface.BackdropSource == GlassBackdropSource.LiveDesktop, "Production GlassWindow, shadow and surface use LiveDesktop");
            var image = new RenderTargetBitmap(240, 160, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
            var imagePixels = new byte[240 * 160 * 4]; image.CopyPixels(imagePixels, 240 * 4, 0);
            var center = (80 * 240 + 120) * 4;
            if (args.Contains("--android-glass"))
                Require(imagePixels[center + 1] is > 205 and < 225 && imagePixels[center] < 10 && imagePixels[center + 2] < 10,
                    "Integrated Android material applies reference dark tint to live green without feedback");
            else
                Require(imagePixels[center + 1] > 230 && imagePixels[center] < 30 && imagePixels[center + 2] < 30,
                    "Integrated original glass retains clear green center without gray overlay or feedback");
            if (args.Contains("--hover-popup"))
            {
                var before = (BitmapSource)frameField.GetValue(surface)!;
                var beforePixels = new byte[before.PixelWidth * before.PixelHeight * 4];
                before.CopyPixels(beforePixels, before.PixelWidth * 4, 0);
                var tooltip = new ToolTip
                {
                    Content = "Simulated sustained icon hover", PlacementTarget = surface,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
                    StaysOpen = true,
                };
                try
                {
                    tooltip.IsOpen = true;
                    for (var sample = 0; sample < 8; sample++) Pump();
                    var retained = frameField.GetValue(surface) as BitmapSource;
                    Require(retained is not null, "Sustained tooltip retains a complete glass frame");
                    var retainedPixels = new byte[retained!.PixelWidth * retained.PixelHeight * 4];
                    retained.CopyPixels(retainedPixels, retained.PixelWidth * 4, 0);
                    Require(beforePixels.AsSpan().SequenceEqual(retainedPixels), "Tooltip guard freezes last safe material instead of switching to wallpaper or transparent");
                }
                finally { tooltip.IsOpen = false; }
                child.StandardInput.WriteLine("blue"); child.StandardInput.Flush();
                Require(child.StandardOutput.ReadLine() == "BLUE", "Backdrop changes after tooltip closes");
                for (var sample = 0; sample < 6; sample++) Pump();
                var resumed = (BitmapSource)frameField.GetValue(surface)!;
                var resumedPixels = new byte[resumed.PixelWidth * resumed.PixelHeight * 4];
                resumed.CopyPixels(resumedPixels, resumed.PixelWidth * 4, 0);
                Require(!beforePixels.AsSpan().SequenceEqual(resumedPixels), "Live glass resumes after tooltip closes");
            }
            if (args.Contains("--visual"))
            {
                child.StandardInput.WriteLine("pattern"); child.StandardInput.Flush();
                Require(child.StandardOutput.ReadLine() == "PATTERN", "Synthetic optical target loaded"); Pump(); Pump();
                var preview = new DrawingVisual();
                using (var draw = preview.RenderOpen())
                {
                    var pattern = new Pattern(); pattern.Measure(new Size(400,300)); pattern.Arrange(new Rect(0,0,400,300));
                    var backdrop = new RenderTargetBitmap(400,300,96,96,PixelFormats.Pbgra32); backdrop.Render(pattern);
                    draw.DrawImage(backdrop,new Rect(0,0,400,300));
                    var shadow = typeof(GlassWindow).GetField("_shadow",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(glass)!;
                    var shadowWindow = (Window)typeof(GlassShadow).GetField("_window",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(shadow)!;
                    var shadowFrame = new RenderTargetBitmap((int)Math.Ceiling(shadowWindow.ActualWidth),(int)Math.Ceiling(shadowWindow.ActualHeight),96,96,PixelFormats.Pbgra32);
                    shadowFrame.Render(shadowWindow);
                    draw.DrawImage(shadowFrame,new Rect(30 - 42,30 - 42,shadowWindow.ActualWidth,shadowWindow.ActualHeight));
                    var material = new RenderTargetBitmap(240,160,96,96,PixelFormats.Pbgra32); material.Render(glass);
                    draw.DrawImage(material,new Rect(30,30,240,160));
                }
                var output = new RenderTargetBitmap(400,300,96,96,PixelFormats.Pbgra32); output.Render(preview);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
                var path = System.IO.Path.GetFullPath("artifacts/verification/liquid-glass-synthetic.png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var stream = System.IO.File.Create(path); encoder.Save(stream);
                Console.WriteLine("Synthetic preview: " + path);
            }
            Console.WriteLine("PASS: production capture, popup guard, dynamic frames, disposal and integrated glass.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
        finally
        {
            front?.Close();
            if (!child.HasExited) { child.StandardInput.WriteLine("exit"); child.StandardInput.Flush(); if (!child.WaitForExit(3000)) child.Kill(); }
        }
    }
    private static bool IsColor(byte[]? pixels, byte b, byte g, byte r) => pixels is { Length: >= 4 } && pixels[0] == b && pixels[1] == g && pixels[2] == r;
    private static void Require(bool value, string description) { Console.WriteLine((value ? "PASS " : "FAIL ") + description); if (!value) throw new InvalidOperationException(description); }
    private static int Background(bool fullScreen)
    {
        var back = new Window { Title = "GlassDesk controlled capture background",
            Width = fullScreen ? SystemParameters.PrimaryScreenWidth : 400,
            Height = fullScreen ? SystemParameters.PrimaryScreenHeight : 300,
            Left = fullScreen ? 0 : SystemParameters.VirtualScreenLeft + 70,
            Top = fullScreen ? 0 : SystemParameters.VirtualScreenTop + 70,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = fullScreen,
            ShowInTaskbar = false, Topmost = true, Background = Brushes.Lime };
        back.Show();
        if (fullScreen)
        {
            const uint SwpNoSize = 0x0001;
            const uint SwpNoMove = 0x0002;
            const uint SwpNoActivate = 0x0010;
            const uint SwpShowWindow = 0x0040;
            _ = SetWindowPos(new WindowInteropHelper(back).Handle, new nint(-1), 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
        }
        Pump();
        var boundsTopLeft = back.PointToScreen(new Point(0, 0));
        var boundsBottomRight = back.PointToScreen(new Point(back.ActualWidth, back.ActualHeight));
        Console.WriteLine($"READY|active={back.IsActive}|size={back.ActualWidth:0.##}x{back.ActualHeight:0.##}|screen={boundsTopLeft.X:0},{boundsTopLeft.Y:0}-{boundsBottomRight.X:0},{boundsBottomRight.Y:0}");
        Console.Out.Flush();
        _ = Task.Run(() =>
        {
            while (Console.ReadLine() is { } command)
            {
                back.Dispatcher.Invoke(() =>
                {
                    if (command == "exit") { back.Close(); back.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); return; }
                    switch (command)
                    {
                        case "blue":
                            back.Background = Brushes.Blue;
                            back.Content = null;
                            break;
                        case "pattern":
                            back.Background = Brushes.Transparent;
                            back.Content = new Pattern();
                            break;
                        case "light":
                        case "dark":
                        case "contrast":
                        case "grid":
                            back.Background = Brushes.Transparent;
                            back.Content = new FinalFrameBackground(command);
                            break;
                        default:
                            back.Background = Brushes.Lime;
                            back.Content = null;
                            break;
                    }
                    Pump(); Console.WriteLine(command.ToUpperInvariant()); Console.Out.Flush();
                });
                if (command == "exit") return;
            }
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => { back.Close(); back.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); };
        timer.Start(); Dispatcher.Run(); return 0;
    }
    internal static string DescribeTopmostWindowOrder()
    {
        const int GwlExStyle = -20;
        const long WsExTopmost = 0x00000008;
        const uint GwHwndNext = 2;
        var result = new List<string>();
        var handle = GetTopWindow(0);
        for (var index = 0; handle != 0 && index < 30; index++, handle = GetWindow(handle, GwHwndNext))
        {
            var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            if ((extendedStyle & WsExTopmost) == 0) break;
            if (!IsWindowVisible(handle)) continue;
            var title = new StringBuilder(256);
            _ = GetWindowText(handle, title, title.Capacity);
            _ = GetWindowThreadProcessId(handle, out var processId);
            result.Add($"{processId}:{title}");
        }
        return string.Join(" > ", result);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); DwmFlush();
    }
    private sealed class ProbeGlass : GlassWindow;
    private sealed class Pattern : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            var dc = drawingContext;
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(15,83,136),Color.FromRgb(237,153,105),25),null,new Rect(0,0,400,300));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(110,225,204),Color.FromRgb(31,109,142)),null,new Point(125,120),105,105);
            for (var y = 0; y < 300; y += 20) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(125,255,255,255)),1),new Point(0,y),new Point(400,y));
            for (var x = 0; x < 400; x += 20) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(125,255,255,255)),1),new Point(x,0),new Point(x,300));
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    private static nint GetWindowLongPtr(nint window, int index) => GetWindowLongPtrW(window, index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
}
