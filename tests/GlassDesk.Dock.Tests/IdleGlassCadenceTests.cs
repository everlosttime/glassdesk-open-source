using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.UI;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class IdleGlassCadenceTests
{
    [Fact]
    public void StaticFeedReducesAcquisitionsAndStillDeliversChangedPixelsPromptly()
    {
        var native = new StaticCapture();
        using var feed = new SharedBackdropFeed((_, _, _) => native);
        var region = new PixelRect(100, 100, 80, 60);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 1000)
        {
            if (feed.TryRead(region, 80, 60, out var frame)) frame?.Dispose();
            Thread.Sleep(10);
        }
        var before = Volatile.Read(ref native.Reads);
        Thread.Sleep(600);
        Assert.InRange(Volatile.Read(ref native.Reads) - before, 1, 12);
        Volatile.Write(ref native.Value, 90);
        var changed = SpinWait.SpinUntil(() =>
        {
            if (!feed.TryRead(region, 80, 60, out var frame) || frame is null) return false;
            using (frame) return frame.Pixels.Span[0] == 90;
        }, 250);
        Assert.True(changed, "Idle capture failed to pick up changed background pixels within 250 ms.");
        before = Volatile.Read(ref native.Reads);
        Thread.Sleep(120);
        Assert.True(Volatile.Read(ref native.Reads) - before >= 3, "Changed content must restore live capture cadence.");
    }

    [Fact]
    public void DynamicBackgroundUsesBoundedRefreshUntilAnInteractionStarts()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new GlassBackdropScene() };
            var background = (GlassBackgroundLayer)Field(surface, "_background");
            StaticCapture? native = null;
            var feed = new SharedBackdropFeed((bounds, _, _) => native = new StaticCapture(bounds) { Continuous = true });
            typeof(GlassBackgroundLayer).GetField("_compatible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(background, feed);
            var window = new TestWindow { Width = 100, Height = 80, Content = surface, ShowActivated = false, Left = 100, Top = 100 };
            try
            {
                window.Show(); Pump(500);
                var before = Volatile.Read(ref native!.Reads);
                Pump(1000);
                Assert.InRange(Volatile.Read(ref native.Reads) - before, 15, 40);
                Assert.False((bool)Field(surface, "_renderClockAttached"));
                surface.SetContainerAnimationActive(true);
                Assert.True((bool)Field(surface, "_renderClockAttached"));
                before = Volatile.Read(ref native.Reads);
                Pump(200);
                Assert.True(Volatile.Read(ref native.Reads) - before >= 8);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void StaticSurfaceStopsTheRenderClockAndAnExplicitRefreshWakesIt()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new GlassBackdropScene() };
            var background = (GlassBackgroundLayer)Field(surface, "_background");
            var feed = new SharedBackdropFeed((bounds, _, _) => new StaticCapture(bounds));
            typeof(GlassBackgroundLayer).GetField("_compatible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(background, feed);
            var window = new TestWindow { Width = 100, Height = 80, Content = surface, ShowActivated = false, Left = 100, Top = 100 };
            try
            {
                window.Show();
                Pump(1500);
                Assert.False((bool)Field(surface, "_renderClockAttached"), System.Text.Json.JsonSerializer.Serialize(surface.InspectPerformance()));
                Assert.True((TimeSpan)Field(surface, "_refreshInterval") >= TimeSpan.FromMilliseconds(80));
                surface.RequestBackdropRefresh();
                Assert.True((bool)Field(surface, "_renderClockAttached"));
            }
            finally { window.Close(); }
        });
    }

    private static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private sealed class TestWindow : GlassWindow { }

    private sealed class StaticCapture(ScreenRect? bounds = null) : IDesktopSceneCapture
    {
        internal int Reads, Value = 30;
        internal bool Continuous;
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false)
        {
            Interlocked.Increment(ref Reads);
            if (Continuous) Interlocked.Increment(ref Value);
            var width = bounds?.Width ?? 240;
            var height = bounds?.Height ?? 240;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)Volatile.Read(ref Value); pixels[i + 3] = 255; }
            return new(width, height, pixels) { SourceQpcTicks = Stopwatch.GetTimestamp() };
        }
        public void ReleaseReadbackBuffers() { }
        public void Dispose() { }
    }
}
