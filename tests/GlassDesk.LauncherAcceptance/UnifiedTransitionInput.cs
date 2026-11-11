using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;

internal static partial class Program
{
    private static int RunUnifiedClickPeer(string[] args)
    {
        var report = Path.GetFullPath(Argument(args, "--report")!);
        var dpi = double.Parse(Argument(args, "--dpi")!, System.Globalization.CultureInfo.InvariantCulture);
        var x = int.Parse(Argument(args, "--x")!); var y = int.Parse(Argument(args, "--y")!);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window { Width = 340 * 96 / dpi, Height = 340 * 96 / dpi, Left = x * 96 / dpi, Top = y * 96 / dpi,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.DimGray };
        var count = 0;
        window.MouseLeftButtonDown += (_, _) => { count++; File.WriteAllText(report + ".count.tmp", count.ToString()); File.Move(report + ".count.tmp", report + ".count", true); };
        window.Show(); window.UpdateLayout();
        File.WriteAllText(report + ".count", "0");
        File.WriteAllText(report, JsonSerializer.Serialize(new { hwnd = new WindowInteropHelper(window).Handle.ToInt64(), pid = Environment.ProcessId }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; var clock = Stopwatch.StartNew();
        timer.Tick += (_, _) => { if (File.Exists(report + ".stop") || clock.Elapsed.TotalSeconds > 45) { timer.Stop(); window.Close(); app.Shutdown(); } };
        timer.Start(); app.Run(); return 0;
    }

    private sealed class UnifiedInputPeer : IDisposable
    {
        private readonly Process _process;
        private readonly string _report;
        internal nint Handle { get; }
        internal int Clicks => int.Parse(File.ReadAllText(_report + ".count"));
        internal UnifiedInputPeer(string directory, DisplayInfo display, PixelRect panel)
        {
            _report = Path.Combine(directory, "peer.json");
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var arg in new[] { "--unified-click-peer", "--report", _report, "--dpi", display.Dpi.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--x", (panel.X - 48).ToString(), "--y", (panel.Y - 48).ToString() }) info.ArgumentList.Add(arg);
            _process = Process.Start(info)!;
            if (!PumpUntil(() => File.Exists(_report), TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Owned input peer not ready.");
            var ready = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(_report));
            if (ready.GetProperty("pid").GetInt32() != _process.Id) throw new InvalidOperationException("Owned peer PID mismatch.");
            Handle = (nint)ready.GetProperty("hwnd").GetInt64();
        }
        public void Dispose()
        {
            File.WriteAllText(_report + ".stop", "stop");
            if (!_process.WaitForExit(2000)) _process.Kill(true);
            _process.Dispose();
        }
    }

    private static object ProbeUnifiedInput(LauncherWindow window, UnifiedInputPeer peer, string phase)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var panel = (Rect)typeof(LauncherWindow).GetField("_transitionPanelBoundsDip", fields)!.GetValue(window)!;
        var radius = (double)typeof(LauncherWindow).GetField("_transitionPanelRadiusDip", fields)!.GetValue(window)!;
        var host = new WindowInteropHelper(window).Handle;
        var results = new List<object>(); var parentClicks = 0;
        System.Windows.Input.MouseButtonEventHandler observed = (_, _) => parentClicks++;
        window.PreviewMouseLeftButtonDown += observed;
        UnifiedCursor original = default, last = default; var moved = false;
        _ = UnifiedGetCursorPos(out original);
        try
        {
            foreach (var (name, local, expected) in new[] {
                ("shadow", new Point(panel.X - 6, panel.Y + radius + 8), peer.Handle),
                ("corner", new Point(panel.X + 2, panel.Y + 2), peer.Handle),
                ("interior", new Point(panel.X + radius + 12, panel.Y + radius + 12), host) })
            {
                window.Activate(); Pump(TimeSpan.FromMilliseconds(50));
                var screen = window.PointToScreen(local);
                last = new() { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
                var hit = UnifiedWindowFromPoint(((long)last.Y << 32) | (uint)last.X);
                var root = UnifiedGetAncestor(hit, 2);
                if (root != expected) throw new InvalidOperationException($"{phase}/{name}: hit target {root:X}, expected {expected:X}; no click injected.");
                var beforePeer = peer.Clicks; var beforeParent = parentClicks;
                IosInput.SetCursorPos(last.X, last.Y); moved = true;
                IosInput.Mouse(2); IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(100));
                var peerDelta = peer.Clicks - beforePeer; var parentDelta = parentClicks - beforeParent;
                if (expected == host ? parentDelta != 1 || peerDelta != 0 : peerDelta != 1 || parentDelta != 0)
                    throw new InvalidOperationException($"{phase}/{name}: parent {parentDelta}, peer {peerDelta}.");
                results.Add(new { name, last.X, last.Y, peerDelta, parentDelta });
            }
        }
        finally
        {
            window.PreviewMouseLeftButtonDown -= observed;
            if (moved && UnifiedGetCursorPos(out var current) && current.X == last.X && current.Y == last.Y) IosInput.SetCursorPos(original.X, original.Y);
        }
        return new { phase, results };
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnifiedCursor { internal int X, Y; }
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern nint UnifiedWindowFromPoint(long point);
    [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern nint UnifiedGetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnifiedGetCursorPos(out UnifiedCursor point);
}
