using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Xunit.Abstractions;

namespace GlassDesk.Dock.Tests;

public sealed class DockHoverSchedulingTests(ITestOutputHelper output)
{
    [Fact]
    public void RapidReversalsKeepHitTargetsAlignedAndExitReturnsToRest()
    {
        StaTest.Run(() =>
        {
            const double size = 48;
            const double peak = 1.35;
            var strip = new DockTileStrip();
            strip.SetMetrics(size, 8, peak, 8, 0);
            strip.SetTiles(Enumerable.Range(0, 12)
                .Select(index => new DockTileModel { EntryId = $"sweep-{index}" }).ToArray());
            var step = typeof(DockTileStrip).GetMethod("AnimateFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<double?, double, bool>>(strip);
            for (var frame = 0; frame < 180; frame++)
            {
                var pointer = DockMagnification.SlotCenter(frame % 2 == 0 ? 0 : 11, size, 8, peak);
                step(pointer, 1.0 / 144);
                var states = strip.PaintedTiles;
                foreach (var tile in states) Assert.InRange(tile.Scale, 1, peak);
                // Read actual transforms/Canvas positions/ZIndex, then probe each overlap boundary.
                for (var index = 1; index < states.Count; index++)
                {
                    var probe = states[index].Left;
                    var visible = Enumerable.Range(0, states.Count).Where(candidate =>
                    {
                        var tile = states[candidate];
                        var center = tile.Left + size / 2;
                        return probe >= center - size * tile.Scale / 2 && probe < center + size * tile.Scale / 2;
                    }).MaxBy(candidate => states[candidate].ZIndex);
                    Assert.Equal(visible, strip.ResolveHit(probe));
                }
            }
            var settled = false;
            for (var frame = 0; frame < 144; frame++) settled = step(null, 1.0 / 144);
            Assert.True(settled);
            Assert.All(strip.PaintedTiles, tile => Assert.Equal(1, tile.Scale));
        });
    }

    [Fact]
    public void HoverReturnContinuesWhileInputWorkIsQueued()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(48, 8, 1.15, 8, 0);
            strip.SetTiles(Enumerable.Range(0, 12)
                .Select(index => new DockTileModel { EntryId = $"hover-{index}", DisplayName = $"App {index}" }).ToArray());
            var host = new Window { Content = strip, SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            var dispatcher = host.Dispatcher;
            var stopInput = false;
            DispatcherTimer? observer = null;
            try
            {
                host.Show();
                host.UpdateLayout();
                MeasureRapidSweep(strip);
                strip.PaintFrame(DockMagnification.SlotCenter(0, 48, 8, 1.15));
                strip.NotifyPointerLeft();
                var elapsed = Stopwatch.StartNew();
                var frame = new DispatcherFrame();
                var sampledScale = double.NaN;
                observer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
                { Interval = TimeSpan.FromMilliseconds(120) };
                observer.Tick += (_, _) =>
                {
                    sampledScale = strip.PaintedTiles[0].Scale;
                    observer.Stop();
                    frame.Continue = false;
                };
                void InputBurst()
                {
                    if (stopInput || elapsed.ElapsedMilliseconds >= 250) return;
                    var started = Stopwatch.GetTimestamp();
                    while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < .3) Thread.SpinWait(32);
                    dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)InputBurst);
                }
                observer.Start();
                dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)InputBurst);
                Dispatcher.PushFrame(frame);
                output.WriteLine($"Input-load sample at {elapsed.ElapsedMilliseconds} ms: scale {sampledScale:F6}");
                Assert.True(sampledScale < 1.14,
                    $"Hover animation froze while input remained queued: scale {sampledScale:F6}");
            }
            finally
            {
                stopInput = true;
                observer?.Stop();
                host.Close();
            }
        });
    }

    private void MeasureRapidSweep(DockTileStrip strip)
    {
        var step = typeof(DockTileStrip).GetMethod("AnimateFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<double?, double, bool>>(strip);
        for (var i = 0; i < 100; i++) step(SweepPosition(i), 1.0 / 144);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++) step(SweepPosition(i), 1.0 / 144);
        elapsed.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"Rapid sweep CPU work: {elapsed.Elapsed.TotalMilliseconds / 2000:F4} ms/frame; {allocated / 2000} bytes/frame (not displayed FPS)");
        // Keep a sustained 144 Hz sweep from producing the former geometry/sort garbage every frame.
        Assert.True(allocated / 2000 < 1400, $"Rapid sweep allocated {allocated / 2000} bytes/frame");

        static double SweepPosition(int frame)
        {
            var phase = frame % 44;
            return 27.6 + (phase <= 22 ? phase : 44 - phase) * 28;
        }
    }
}
