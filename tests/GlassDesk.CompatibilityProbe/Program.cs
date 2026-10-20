using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

internal static class CompatibilityProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--capture-protocol-fixture") return CaptureProtocolFixture.Run(args[1]);
        if (args.Length == 7 && args[0] == "--compare-live-region")
        {
            SetThreadDpiAwarenessContext(-4);
            var parentId = int.Parse(args[1]);
            var region = new ScreenRect(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
            using var capture = new SharedDesktopSceneCapture(region, isolatedParentProcess: parentId);
            var clock = Stopwatch.StartNew();
            var index = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                var frame = capture.TryRead(TimeSpan.FromMilliseconds(16));
                if (frame is not null && index++ % 20 == 0) Save(frame, args[6] + "-" + index + ".png");
                global::Program.Pump(TimeSpan.FromMilliseconds(10));
            }
            return 0;
        }
        if (GlassDesk.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        if (args.Length == 1 && args[0] == "--wallpaper-metadata")
        {
            var snapshot = DesktopWallpaperSource.TryRead();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                available=snapshot is not null,position=snapshot?.Position.ToString(),color=snapshot?.BackgroundColor,
                monitors=snapshot?.Monitors.Select(m => new {m.Bounds,pathEmpty=string.IsNullOrEmpty(m.Path),
                    fileExists=File.Exists(m.Path),extension=Path.GetExtension(m.Path),bytes=File.Exists(m.Path)?new FileInfo(m.Path).Length:0})
            }));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--moving-backdrop-fixture")
            return MovingBackdropFixture.Run(args[1]);
        if (args.Length == 2 && args[0] == "--desktop-transition-fixture")
            return MovingBackdropFixture.Run(args[1], coverWorkArea: false);
        if (args.Length == 3 && args[0] == "capture-window")
        {
            SetThreadDpiAwarenessContext(-4);
            try
            {
                var captured = WgcWindowCapture.Capture((nint)long.Parse(args[1]), TimeSpan.FromSeconds(2));
                if (captured is null) return 1;
                Save(captured, args[2]); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
        if (args.Length == 0 || args[0] == "report")
            return Report(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "Windows11-compatibility-report.json"),
                args.Length > 2 ? args[2] : null);
        if (args.Length != 2 || args[0] != "fixture") return 2;
        var root = args[1];
        SetThreadDpiAwarenessContext(-4);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var bottom = Window(Colors.Lime, 300, 200, 240, 160);
        var top = Window(Colors.Blue, 360, 240, 120, 80);
        Window? added = null;
        bottom.Show(); top.Show();
        SetWindowPos(new WindowInteropHelper(bottom).Handle, -1, 300, 200, 240, 160, 0x10);
        SetWindowPos(new WindowInteropHelper(top).Handle, -1, 360, 240, 120, 80, 0x10);
        var ready = new { bottom = new WindowInteropHelper(bottom).Handle.ToInt64(), top = new WindowInteropHelper(top).Handle.ToInt64() };
        File.WriteAllText(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(ready));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        var previous = "";
        timer.Tick += (_, _) =>
        {
            var path = Path.Combine(root, "command.txt");
            string command;
            try { command = File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
            catch (IOException) { return; }
            if (command == previous) return;
            previous = command;
            switch (command)
            {
                case "red": bottom.Background = Brushes.Red; break;
                case "hide": top.Hide(); break;
                case "close": top.Close(); break;
                case "move": SetWindowPos(new WindowInteropHelper(top).Handle, -1, 450, 280, 120, 80, 0x10); break;
                case "maximize": top.WindowState = WindowState.Maximized; break;
                case "restore": top.WindowState = WindowState.Normal; break;
                case "add":
                    added = Window(Colors.Yellow, 400, 280, 64, 32); added.Show();
                    SetWindowPos(new WindowInteropHelper(added).Handle, -1, 400, 280, 64, 32, 0x10); break;
                case "remove": added?.Close(); added = null; break;
                case "exit": app.Shutdown(); break;
            }
            File.WriteAllText(Path.Combine(root, "ack.txt"), command);
        };
        timer.Start();
        app.Run();
        return 0;
    }

    private static Window Window(Color color, double x, double y, double width, double height) => new()
    {
        Title = "GlassDesk compatibility fixture", WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize, Left = x, Top = y, Width = width, Height = height,
        Background = new SolidColorBrush(color), Topmost = true, ShowInTaskbar = false
    };

    private static int Report(string path, string? workerExecutable = null)
    {
        SetThreadDpiAwarenessContext(-4);
        var traces = new List<CaptureTrace>();
        try
        {
            var monitor = DisplayService.Primary()!.Bounds;
            var width = Math.Min(800, monitor.Width); var height = Math.Min(320, monitor.Height);
            var region = new ScreenRect(monitor.X + (monitor.Width - width) / 2,
                monitor.Bottom - height, width, height);
            using var capture = workerExecutable is null
                ? DesktopSceneCaptureFactory.Create(region, false, traces.Add)
                : new RecoveringDesktopSceneCapture(region, traces.Add, workerExecutable);
            var watch = Stopwatch.StartNew();
            CapturePixels? frame = null;
            var frames = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                var next = capture.TryRead(TimeSpan.FromMilliseconds(16));
                if (next is not null) { frame = next; frames++; }
                global::Program.Pump(TimeSpan.FromMilliseconds(1));
            }
            if (frame is null) throw new TimeoutException("No compatible background frames received.");
            var imagePath = Path.ChangeExtension(path, ".png");
            Save(frame, imagePath);
            // On the verified local build, save a comparison from the original provider.
            if (WindowsCapabilities.BuildNumber == 26100)
            {
                var reference = SharedDesktopSceneCapture.Capture(region, TimeSpan.FromSeconds(2));
                if (reference is not null) Save(reference, Path.ChangeExtension(path, ".reference.png"));
            }
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                passed = true, os = WindowsCapabilities.OsVersion.ToString(),
                selectedProvider = DesktopSceneCaptureFactory.SelectProvider(WindowsCapabilities.BuildNumber).ToString(),
                receivedFrames = frames, captureMilliseconds = watch.Elapsed.TotalMilliseconds,
                region, imagePath, traces,
                note = "Frame count is capture availability, not displayed FPS. DocumentedFallback retains ordinary windows and static wallpaper, but not a live third-party wallpaper."
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { passed = false, os = WindowsCapabilities.OsVersion.ToString(),
                error = error.ToString(), traces }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void Save(CapturePixels frame, string path)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
