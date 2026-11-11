using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var marker = new Border { Background = Brushes.Magenta };
        var window = new Fixture { Content = marker, Width = 180, Height = 140,
            Left = 100, Top = 100, Topmost = true, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            Pump(TimeSpan.FromMilliseconds(400));
            var handle = new WindowInteropHelper(window).Handle;
            var readAffinity = GetWindowDisplayAffinity(handle, out var affinity);
            var shadow = (GlassShadow?)typeof(GlassWindow).GetField("_shadow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            var readShadowAffinity = shadow is not null && GetWindowDisplayAffinity(shadow.Handle, out _);
            uint shadowAffinity = uint.MaxValue;
            if (shadow is not null) GetWindowDisplayAffinity(shadow.Handle, out shadowAffinity);
            var capture = MonitorCaptureReference.Capture(handle);
            var point = marker.PointToScreen(new Point(marker.ActualWidth / 2, marker.ActualHeight / 2));
            var monitor = DisplayService.Primary()!.Bounds;
            var x = (int)point.X - monitor.X;
            var y = (int)point.Y - monitor.Y;
            var pixel = capture?.Pixels.AsSpan((y * capture.Width + x) * 4, 4).ToArray();
            var visible = pixel is [> 240, < 15, > 240, > 240];
            var passed = readAffinity && affinity == 0 && readShadowAffinity && shadowAffinity == 0 && visible;
            var report = new { passed, readAffinity, affinity, readShadowAffinity, shadowAffinity,
                monitorCaptureMarkerVisible = visible, markerBgra = pixel?.Select(p => (int)p).ToArray(),
                note = "Actual packaged GlassWindow and ambient shadow; monitor WGC sees an opaque marker in the window. No screen image retained; tool-specific capture UI not tested." };
            File.WriteAllText(args[0], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return passed ? 0 : 1;
        }
        finally { window.Close(); }
    }
    private sealed class Fixture : GlassWindow { }
    internal static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
}
