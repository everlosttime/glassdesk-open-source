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
using System.Windows.Threading;
using GlassDesk.App;
using GlassDesk.App.Startup;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI;

/// <summary>Runs in its own STA process; never starts the installed product or loads its data.</summary>
internal static class WindowTopmostHostProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(string reportPath)
    {
        SetProcessDpiAwarenessContext(-4);
        var results = new List<object>();
        var errors = new List<string>();
        var actions = new List<string>();
        var screenshots = new List<string>();
        var displays = DisplayService.Enumerate();
        var dpiAwareness = DisplayService.ProbeDpiAwareness();
        var crossMonitorTestPerformed = false;
        var failed = 0;
        var hotkeyMessages = 0;
        var output = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "GlassDesk-topmost-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var oldCursor = DisplayService.GetCursorPixelPoint();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = new AppHost(app, CommandLineOptions.Parse([]));
        var available = typeof(AppHost).GetProperty("CanSelectWindowTopmost", Private)!;
        void Check(string name, bool passed, string? detail = null)
        {
            results.Add(new { name, passed, detail });
            if (!passed) failed++;
        }
        try
        {
            Check("The acceptance process matches the shipped app's PerMonitorV2 context", dpiAwareness.IsPerMonitorV2, dpiAwareness.Describe());
            if (!dpiAwareness.IsPerMonitorV2) throw new InvalidOperationException("Run the acceptance apphost executable with the production DPI manifest; a system-aware dotnet host cannot validate mixed-DPI placement.");
            Check("Selection is initially available", available.GetValue(host) is true);
            typeof(AppHost).GetMethod("OnSystemDisplaySettingsChanged", Private)!.Invoke(host, [null, EventArgs.Empty]);
            Check("A display change immediately blocks queued selection commits before dispatcher refresh", available.GetValue(host) is false);
            Set(host, "_displayRefreshPending", 0);
            Check("Selection is available again after display refresh", available.GetValue(host) is true);
            typeof(AppHost).GetMethod("SetPerformanceSessionSuspended", Private)!.Invoke(host, [true]);
            Check("A session lock immediately blocks selection before dispatcher suspension", available.GetValue(host) is false);

            typeof(AppHost).GetMethod("SetPerformanceSessionSuspended", Private)!.Invoke(host, [false]);
            Pump(80);
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(temporaryRoot, "data")));
            data.LoadAll();
            foreach (var (_, definition) in HotkeyText.Bindings(data.Settings.Current.Shortcuts)) definition.Enabled = false;
            data.Settings.Current.Shortcuts.SelectWindowTopmost = new HotkeyDefinition { Enabled = true, Control = true, Alt = true, Key = "T" };
            Set(host, "_data", data);
            var primary = new PrimaryDisplaySession(DisplayService.Primary() ?? throw new InvalidOperationException("Primary monitor unavailable"));
            Set(host, "_primaryDisplaySession", primary);
            Set(host, "_performance", new PerformanceCoordinator(DateTimeOffset.UtcNow));
            Set(host, "_performanceDeadlineTimer", new DispatcherTimer(DispatcherPriority.Input, app.Dispatcher));

            var source = new HwndSource(new HwndSourceParameters("GlassDesk isolated topmost host probe") { Width = 0, Height = 0, WindowStyle = 0 });
            var realMessageHandler = typeof(AppHost).GetMethod("OnMessageWindowProc", Private)!.CreateDelegate<HwndSourceHook>(host);
            source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == 0x0312) hotkeyMessages++;
                return realMessageHandler(hwnd, message, wParam, lParam, ref handled);
            });
            Set(host, "_messageWindow", source);
            var execute = typeof(AppHost).GetMethod("ExecuteInteraction", Private)!.CreateDelegate<Action<InputAction>>(host);
            var interaction = new InteractionController(data, primary, source.Handle, app.Dispatcher, _ => true,
                action => { actions.Add(action.ToString()); execute(action); }, errors.Add);
            Set(host, "_interaction", interaction);
            var initialization = interaction.Initialize();
            Check("An isolated real Ctrl+Alt+T hotkey registers without loading installed user data", initialization is null && interaction.InitializationWarning is null);
            if (initialization is not null || interaction.InitializationWarning is not null)
                throw new InvalidOperationException(initialization ?? interaction.InitializationWarning);

            using var target = new ProbeTarget(temporaryRoot);
            var initial = target.Read();
            Check("The isolated foreign target also runs with PerMonitorV2 awareness", initial.IsPerMonitorV2);
            var targetHandle = (nint)initial.Handle;
            WindowActivation.TryBringToForeground(targetHandle);
            SetCursorPos(initial.ButtonX, initial.ButtonY);
            Pump(80);

            PressChord();
            Check("Native Ctrl+Alt+T travels through WM_HOTKEY, InteractionController and AppHost to open the picker",
                WaitUntil(() => Picker(host)?.IsSelecting == true) && hotkeyMessages == 1 && actions.SequenceEqual(["SelectWindowTopmost"]));
            if (Picker(host)?.IsSelecting != true) throw new InvalidOperationException("Registered native hotkey did not open the picker");
            Pump(180);
            GetWindowRect(targetHandle, out var targetBounds);
            var screenshot = Path.Combine(Path.GetDirectoryName(output)!, "window-topmost-selection.png");
            CaptureScreenRegion(screenshot, targetBounds);
            screenshots.Add(screenshot);
            LeftClick();
            Check("A real selection click pins the foreign original window and closes the picker",
                WaitUntil(() => IsTopmost(targetHandle) && Picker(host)?.IsSelecting == false));
            Check("The window-selection click never presses the foreign target button", target.Read().Clicks == 0);

            PressChord();
            if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException("Second native hotkey did not open the picker");
            Pump(120);
            LeftClick();
            Check("Selecting the same native window again cancels topmost", WaitUntil(() => !IsTopmost(targetHandle) && Picker(host)?.IsSelecting == false));
            Check("Both pin and unpin clicks are consumed by the picker", target.Read().Clicks == 0);

            PressChord();
            if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException("Native hotkey did not enter cancellation test");
            PressChord();
            Check("Repeating the real global hotkey cancels selection without changing topmost",
                WaitUntil(() => Picker(host)?.IsSelecting == false) && !IsTopmost(targetHandle));

            PressChord();
            if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException("Native hotkey did not enter exit-restoration test");
            Pump(120);
            LeftClick();
            if (!WaitUntil(() => IsTopmost(targetHandle))) throw new InvalidOperationException("Exit-restoration target could not be pinned");
            // Keep the initial pipeline's expected count independent of later monitor scenarios.
            Check("Every initial observed action used the new window-topmost command", actions.Count == 5 && actions.All(action => action == "SelectWindowTopmost"));
            // Unpin without adding another input scenario, through normal disposal of this session.
            // A fresh session is created by the next native selection, with the same initial state.
            typeof(AppHost).GetMethod("DisposeWindowTopmost", Private)!.Invoke(host, null);
            Check("The initial session restores its target before multi-monitor acceptance", !IsTopmost(targetHandle));
            var expectedHotkeyMessages = 5;

            for (var index = 0; index < displays.Count; index++)
            {
                var display = displays[index];
                var width = Math.Min(display.WorkArea.Width - 40, display.ToPixels(600));
                var height = Math.Min(display.WorkArea.Height - 40, display.ToPixels(360));
                var placement = new PixelRect(display.WorkArea.X + (display.WorkArea.Width - width) / 2,
                    display.WorkArea.Y + (display.WorkArea.Height - height) / 2, width, height);
                var current = target.MoveTo(placement);
                SetCursorPos(current.ButtonX, current.ButtonY);
                WindowActivation.TryBringToForeground(targetHandle);
                Pump(80);
                Check($"Monitor {index + 1}: the moved foreign window is hit at its current button coordinates",
                    WindowTopmostWindows.FindAt(current.ButtonX, current.ButtonY)?.Handle == targetHandle,
                    $"{display.DeviceName}, DPI={display.Dpi}, requested={placement}, actual={current.Bounds}");
                PressChord();
                expectedHotkeyMessages++;
                if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException($"Monitor {index + 1} hotkey did not open the picker");
                Pump(160);
                var monitorScreenshot = Path.Combine(Path.GetDirectoryName(output)!, $"window-topmost-monitor-{index + 1}.png");
                CaptureScreenRegion(monitorScreenshot, new(current.Bounds.X, current.Bounds.Y, current.Bounds.Right, current.Bounds.Bottom));
                screenshots.Add(monitorScreenshot);
                LeftClick();
                Check($"Monitor {index + 1}: native selection pins the correct original window", WaitUntil(() => IsTopmost(targetHandle) && Picker(host)?.IsSelecting == false));
                PressChord();
                expectedHotkeyMessages++;
                if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException($"Monitor {index + 1} unpin hotkey did not open the picker");
                Pump(100);
                LeftClick();
                Check($"Monitor {index + 1}: native reselection removes topmost without clicking through",
                    WaitUntil(() => !IsTopmost(targetHandle) && Picker(host)?.IsSelecting == false) && target.Read().Clicks == 0);
            }

            var crossing = CrossMonitorPlacement(displays);
            if (crossing is { } cross)
            {
                var current = target.MoveTo(cross.Bounds);
                crossMonitorTestPerformed = true;
                WindowActivation.TryBringToForeground(targetHandle);
                SetCursorPos(cross.FirstPoint.X, cross.FirstPoint.Y);
                Pump(80);
                PressChord();
                expectedHotkeyMessages++;
                if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException("Cross-monitor selection did not open");
                Pump(160);
                var crossScreenshot = Path.Combine(Path.GetDirectoryName(output)!, "window-topmost-cross-monitor.png");
                CaptureScreenRegion(crossScreenshot, new(current.Bounds.X, current.Bounds.Y, current.Bounds.Right, current.Bounds.Bottom));
                screenshots.Add(crossScreenshot);
                LeftClick();
                Check("A window spanning adjacent monitors can be pinned from its first-monitor portion",
                    WaitUntil(() => IsTopmost(targetHandle) && Picker(host)?.IsSelecting == false), $"Actual bounds={current.Bounds}");
                SetCursorPos(cross.SecondPoint.X, cross.SecondPoint.Y);
                PressChord();
                expectedHotkeyMessages++;
                if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException("Cross-monitor reselection did not open");
                Pump(100);
                LeftClick();
                Check("The same spanning window can be unpinned from its other-monitor portion",
                    WaitUntil(() => !IsTopmost(targetHandle) && Picker(host)?.IsSelecting == false) && target.Read().Clicks == 0);
            }
            else
                Check("Cross-monitor coverage availability", true, displays.Count < 2
                    ? "Only one physical monitor is connected; real cross-monitor acceptance is unavailable."
                    : "Connected monitor rectangles have no sufficiently large adjacent boundary; cross-monitor placement was not performed.");

            // Finish with a fresh native pin so normal AppHost.Dispose still proves restoration.
            var finalTarget = target.Read();
            SetCursorPos(finalTarget.ButtonX, finalTarget.ButtonY);
            PressChord();
            expectedHotkeyMessages++;
            if (!WaitUntil(() => Picker(host)?.IsSelecting == true)) throw new InvalidOperationException("Final exit-restoration selection did not open");
            Pump(100);
            LeftClick();
            if (!WaitUntil(() => IsTopmost(targetHandle))) throw new InvalidOperationException("Final exit-restoration target could not be pinned");
            host.Dispose();
            Check("Normal AppHost disposal restores native topmost and releases all selection and feedback windows",
                !IsTopmost(targetHandle) && app.Windows.Count == 0);
            Check("All native hotkey messages retain the correct action through every monitor scenario",
                hotkeyMessages == expectedHotkeyMessages && actions.Count == expectedHotkeyMessages && actions.All(action => action == "SelectWindowTopmost"),
                $"Expected {expectedHotkeyMessages}; WM_HOTKEY={hotkeyMessages}; dispatched actions={actions.Count}");
            Check("The installed user-data directory was not used", !DataDirectoryGuard.Overlaps(data.Paths.RootDirectory, AppPaths.ForCurrentUser().RootDirectory));
        }
        catch (Exception error)
        {
            results.Add(new { name = "Unhandled probe failure", passed = false, error = error.ToString() });
            failed++;
        }
        finally
        {
            host.Dispose();
            app.Shutdown();
            if (oldCursor is { } point) SetCursorPos(point.X, point.Y);
            Directory.Delete(temporaryRoot, true);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            failed, hotkeyMessages, actions, errors, screenshots,
            environment = new { dpiAwareness, displays, hasMultipleMonitors = displays.Count > 1, hasMixedDpi = displays.Select(display => display.Dpi).Distinct().Count() > 1, crossMonitorTestPerformed },
            results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Window topmost host probe: {results.Count - failed} passed, {failed} failed. {output}");
        return failed == 0 ? 0 : 1;
    }

    public static int RunTarget(string marker)
    {
        SetProcessDpiAwarenessContext(-4);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var panel = new StackPanel { Margin = new Thickness(38) };
        panel.Children.Add(new TextBlock { Text = "让想看的内容，一直在眼前", FontSize = 27, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(30, 51, 80)), Margin = new Thickness(0, 0, 0, 15) });
        panel.Children.Add(new TextBlock { Text = "GlassDesk · 窗口置顶实机验收", FontSize = 15, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 30) });
        panel.Children.Add(new TextBlock { Text = "Ctrl + Alt + T 选择窗口\n再次选择同一窗口，取消置顶。", FontSize = 18, LineHeight = 32, Foreground = new SolidColorBrush(Color.FromRgb(57, 76, 101)), Margin = new Thickness(0, 0, 0, 30) });
        var button = new Button { Content = "验收按钮：选择窗口时不应触发", FontSize = 16, Padding = new Thickness(22, 17, 22, 17), HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(button);
        var window = new Window { Title = "GlassDesk 置顶验收窗口", Content = panel, Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)), WindowStartupLocation = WindowStartupLocation.Manual, Width = 760, Height = 450 };
        app.MainWindow = window;
        var clicks = 0;
        var placementSequence = 0;
        void WriteMarker()
        {
            var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var handle = new WindowInteropHelper(window).Handle;
            GetWindowRect(handle, out var bounds);
            var value = new TargetMarker(handle.ToInt64(), (int)center.X, (int)center.Y, clicks, placementSequence, DisplayService.ProbeDpiAwareness().IsPerMonitorV2,
                new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(value));
            File.Move(marker + ".tmp", marker, true);
        }
        button.Click += (_, _) => { clicks++; WriteMarker(); };
        window.Loaded += (_, _) =>
        {
            var display = DisplayService.Primary()!;
            DisplayService.MoveWindowToPixelRect(new WindowInteropHelper(window).Handle,
                new PixelRect(display.Bounds.X + 90, display.Bounds.Y + 80, Math.Min(1000, display.WorkArea.Width - 180), Math.Min(600, display.WorkArea.Height - 160)));
            window.UpdateLayout();
            WriteMarker();
        };
        var placementTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        placementTimer.Tick += (_, _) =>
        {
            var path = marker + ".placement";
            if (!File.Exists(path)) return;
            var requested = JsonSerializer.Deserialize<TargetPlacement>(File.ReadAllText(path))!;
            if (requested.Sequence <= placementSequence) return;
            if (!DisplayService.MoveWindowToPixelRect(new WindowInteropHelper(window).Handle, requested.Bounds))
                throw new InvalidOperationException("Could not place the acceptance window on the requested monitor");
            window.UpdateLayout();
            placementSequence = requested.Sequence;
            WriteMarker();
        };
        placementTimer.Start();
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        deadline.Tick += (_, _) => window.Close();
        deadline.Start();
        app.Run(window);
        deadline.Stop();
        placementTimer.Stop();
        return 0;
    }

    private static void Set(AppHost host, string field, object value) => typeof(AppHost).GetField(field, Private)!.SetValue(host, value);
    private static WindowTopmostPicker? Picker(AppHost host) => (WindowTopmostPicker?)typeof(AppHost).GetField("_windowTopmostPicker", Private)!.GetValue(host);
    private static bool IsTopmost(nint window) => (GetWindowLong(window, -20) & 8) != 0;

    private static void PressChord() => Send([
        Key(0x11), Key(0x12), Key(0x54), Key(0x54, true), Key(0x12, true), Key(0x11, true),
    ]);
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Flags = up ? 2u : 0u } } };
    private static void LeftClick() => Send([
        new Input { Data = new() { Mouse = new() { Flags = 2 } } },
        new Input { Data = new() { Mouse = new() { Flags = 4 } } },
    ]);
    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Native input injection was rejected");
    }

    private static bool WaitUntil(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate() && deadline.Elapsed < TimeSpan.FromSeconds(3)) Pump(25);
        return predicate();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void CaptureScreenRegion(string path, NativeRect bounds)
    {
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, bounds.Left, bounds.Top, 0x40CC0020))
                throw new InvalidOperationException("Could not capture the native picker region");
            var pixels = new byte[width * height * 4];
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
                throw new InvalidOperationException("Could not read the native picker image");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4)));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    private static (PixelRect Bounds, (int X, int Y) FirstPoint, (int X, int Y) SecondPoint)? CrossMonitorPlacement(IReadOnlyList<DisplayInfo> displays)
    {
        var pairs = from first in displays from second in displays
            where !ReferenceEquals(first, second)
            orderby first.Dpi != second.Dpi descending
            select (first.Bounds, second.Bounds);
        foreach (var (first, second) in pairs)
        {
            var sharedTop = Math.Max(first.Y, second.Y);
            var sharedBottom = Math.Min(first.Bottom, second.Bottom);
            if (first.Right == second.X && sharedBottom - sharedTop >= 300)
            {
                var width = Math.Min(800, Math.Min(first.Width, second.Width));
                var height = Math.Min(440, sharedBottom - sharedTop - 40);
                var y = sharedTop + (sharedBottom - sharedTop - height) / 2;
                return (new(first.Right - width / 2, y, width, height), (first.Right - 40, y + height / 2), (first.Right + 40, y + height / 2));
            }
            var sharedLeft = Math.Max(first.X, second.X);
            var sharedRight = Math.Min(first.Right, second.Right);
            if (first.Bottom == second.Y && sharedRight - sharedLeft >= 400)
            {
                var width = Math.Min(760, sharedRight - sharedLeft - 40);
                var height = Math.Min(500, Math.Min(first.Height, second.Height));
                var x = sharedLeft + (sharedRight - sharedLeft - width) / 2;
                return (new(x, first.Bottom - height / 2, width, height), (x + width / 2, first.Bottom - 40), (x + width / 2, first.Bottom + 40));
            }
        }
        return null;
    }

    private sealed record TargetMarker(long Handle, int ButtonX, int ButtonY, int Clicks, int PlacementSequence, bool IsPerMonitorV2, PixelRect Bounds);
    private sealed record TargetPlacement(int Sequence, PixelRect Bounds);

    private sealed class ProbeTarget : IDisposable
    {
        private readonly string _marker;
        private readonly Process _process;
        private int _placementSequence;
        public ProbeTarget(string root)
        {
            _marker = Path.Combine(root, "target.json");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(WindowTopmostHostProbe).Assembly.Location);
            start.ArgumentList.Add("--window-topmost-target");
            start.ArgumentList.Add(_marker);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated target");
            if (!SpinWait.SpinUntil(() => File.Exists(_marker) || _process.HasExited, TimeSpan.FromSeconds(8)) || _process.HasExited)
                throw new InvalidOperationException("Isolated target did not create its native window");
        }
        public TargetMarker Read() => JsonSerializer.Deserialize<TargetMarker>(File.ReadAllText(_marker))!;
        public TargetMarker MoveTo(PixelRect bounds)
        {
            var requested = new TargetPlacement(++_placementSequence, bounds);
            var path = _marker + ".placement";
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(requested));
            File.Move(path + ".tmp", path, true);
            if (!WaitUntil(() => Read().PlacementSequence == requested.Sequence))
                throw new InvalidOperationException("Foreign target did not acknowledge the monitor placement");
            return Read();
        }
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                PostMessage((nint)Read().Handle, 0x0010, 0, 0);
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
            _process.WaitForExit(3000);
            _process.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
}
