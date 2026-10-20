using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassDesk.Dock.Tests;

public sealed class PersistentReadbackTests
{
    [Fact]
    public void SceneSessionRefreshDeliversFreshBlackContentAndKeepsLiveCaptureUpdating()
    {
        StaTest.Run(() =>
        {
            var window = new Window { Width = 160, Height = 120, Left = 50, Top = 50,
                WindowStyle = WindowStyle.None, Background = Brushes.Black,
                ShowInTaskbar = false, Topmost = true };
            try
            {
                window.Show();
                using var capture = new PersistentWindowCapture(new WindowInteropHelper(window).Handle);
                var earlier = ReadColor(capture, 0, 0, 0);
                // A recently active stream can become static at the boundary.
                // Refresh must not depend on another application repainting it.
                window.Background=Brushes.Blue; _=ReadColor(capture,255,0,0);
                window.Background=Brushes.Black; _=ReadColor(capture,0,0,0);
                for (var i = 0; i < 3; i++)
                {
                    var boundary = Stopwatch.GetTimestamp();
                    capture.RetireQueuedFramesAtSceneBoundary(boundary);
                    capture.RefreshSessionAtSceneBoundary();
                    var watch = Stopwatch.StartNew();
                    CapturePixels? fresh = null;
                    while (fresh is null && watch.Elapsed < TimeSpan.FromSeconds(3))
                    {
                        global::Program.Pump(TimeSpan.FromMilliseconds(10));
                        fresh = capture.TryRead(boundary, TimeSpan.FromMilliseconds(30));
                    }
                    Assert.NotNull(fresh);
                    Assert.True(fresh.SourceQpcTicks >= boundary);
                    Assert.Equal(new byte[] { 0, 0, 0, 255 }, fresh.Center);
                }
                window.Background = Brushes.Blue;
                Assert.Equal(new byte[] { 255, 0, 0, 255 }, ReadColor(capture, 255, 0, 0).Center);
                Assert.Equal(new byte[] { 0, 0, 0, 255 }, earlier.Center);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CaptureOwnsReferencesToTheSharedDeviceAndSurvivesAnotherSessionClosing()
    {
        StaTest.Run(() =>
        {
            nint device = 0, context = 0;
            var window = new Window { Width = 160, Height = 120, Left = 50, Top = 50,
                WindowStyle = WindowStyle.None, Background = Brushes.Red, ShowInTaskbar = false, Topmost = true };
            try
            {
                window.Show();
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
                var hwnd = new WindowInteropHelper(window).Handle;
                using var first = new PersistentWindowCapture(hwnd, sharedDevice: device, sharedContext: context);
                using var second = new PersistentWindowCapture(hwnd, sharedDevice: device, sharedContext: context);
                var field = typeof(PersistentWindowCapture).GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.Equal(device, (nint)field.GetValue(first)!);
                Assert.Equal(device, (nint)field.GetValue(second)!);
                // Each session owns references. Drop the provider's references, then
                // close one session while the other keeps capturing fresh pixels.
                Marshal.Release(context); context = 0;
                Marshal.Release(device); device = 0;
                _ = ReadColor(first, 0, 0, 255);
                second.Dispose();
                window.Background = Brushes.Blue;
                Assert.Equal(new byte[] { 255, 0, 0, 255 }, ReadColor(first, 255, 0, 0).Center);
            }
            finally
            {
                if (context != 0) Marshal.Release(context);
                if (device != 0) Marshal.Release(device);
                window.Close();
            }
        });
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, uint type, nint software, uint flags,
        nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);

    [Fact]
    public void RepeatedNativeFramesReuseOneReadbackTextureAndPreserveEarlierPixels()
    {
        StaTest.Run(() =>
        {
            var window = new Window
            {
                Width = 160, Height = 120, Left = 50, Top = 50,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Red, ShowInTaskbar = false, Topmost = true,
            };
            var traces = new ConcurrentQueue<CaptureTrace>();
            try
            {
                window.Show();
                using var capture = new PersistentWindowCapture(new WindowInteropHelper(window).Handle, traces.Enqueue);
                var red = ReadColor(capture, 0, 0, 255);
                var original = (byte[])red.Pixels.Clone();
                for (var i = 0; i < 4; i++)
                {
                    window.Background = i % 2 == 0 ? Brushes.Blue : Brushes.Red;
                    _ = i % 2 == 0 ? ReadColor(capture, 255, 0, 0) : ReadColor(capture, 0, 0, 255);
                }
                Assert.Equal(original, red.Pixels);
                Assert.Single(traces, trace => trace.Stage == "ReadbackResource" && trace.Reason == "Created");
                Assert.True(traces.Count(trace => trace.Stage == "ReadbackResource" && trace.Reason == "Reused") >= 4);
            }
            finally { window.Close(); }
        });
    }

    private static CapturePixels ReadColor(PersistentWindowCapture capture, byte blue, byte green, byte red)
        => ReadColor(capture, blue, green, red, false);

    [Fact]
    public void WorkerRawBuffersAreReusedWhileDefaultReadbackStillReturnsStableSnapshots()
    {
        StaTest.Run(() =>
        {
            var window = new Window { Width = 160, Height = 120, Left = 50, Top = 50,
                WindowStyle = WindowStyle.None, Background = Brushes.Red, ShowInTaskbar = false, Topmost = true };
            try
            {
                window.Show();
                using var capture = new PersistentWindowCapture(new WindowInteropHelper(window).Handle);
                var red = ReadColor(capture, 0, 0, 255, true);
                window.Background = Brushes.Blue;
                var blue = ReadColor(capture, 255, 0, 0, true);
                Assert.Same(red.Pixels, blue.Pixels);
                window.Background = Brushes.Lime;
                var stable = ReadColor(capture, 0, 255, 0);
                window.Background = Brushes.Red;
                _ = ReadColor(capture, 0, 0, 255, true);
                Assert.Equal(new byte[] { 0, 255, 0, 255 }, stable.Center);
            }
            finally { window.Close(); }
        });
    }

    private static CapturePixels ReadColor(PersistentWindowCapture capture, byte blue, byte green, byte red, bool reusePixels)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            var dispatcherFrame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => dispatcherFrame.Continue = false));
            Dispatcher.PushFrame(dispatcherFrame);
            var pixels = capture.TryRead(waitForFrame: TimeSpan.FromMilliseconds(30), reusePixels: reusePixels);
            if (pixels is not null && pixels.Center.Take(3).SequenceEqual(new[] { blue, green, red })) return pixels;
        }
        throw new TimeoutException($"Native capture did not deliver BGRA ({blue}, {green}, {red}).");
    }
}
