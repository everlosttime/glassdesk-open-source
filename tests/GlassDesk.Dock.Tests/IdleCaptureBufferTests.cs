using System.Collections.Concurrent;
using System.Diagnostics;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class IdleCaptureBufferTests
{
    [Fact]
    public void OrdinaryHideStaysWarmPastFiveSecondsWithoutCaptureWorkButGameSleepRetiresImmediately()
    {
        IdleCapture? capture = null;
        using var feed = new SharedBackdropFeed((bounds, _, _) => capture = new IdleCapture(bounds));
        using (Read(feed, new PixelRect(50, 50, 320, 240))) { }
        feed.Suspend();
        Thread.Sleep(100);
        var reads = capture!.Reads;
        Thread.Sleep(6000);
        Assert.False(capture.Disposed);
        Assert.Equal(reads, capture.Reads);
        feed.SetPerformanceSleeping(true);
        Assert.True(SpinWait.SpinUntil(() => capture.Disposed, 2000));
    }

    [Fact]
    public void ShortHidesStayWarmAndLongHidesRetireTheHelperBeforeResume()
    {
        var traces = new ConcurrentQueue<CaptureTrace>();
        var carriers = new ConcurrentQueue<nint>();
        using var feed = new SharedBackdropFeed((bounds,_,trace)=>new RecoveringDesktopSceneCapture(bounds,trace,
            CompatibilityProbePath.File("GlassCaptureProbe.exe")), TimeSpan.FromSeconds(1)) { TraceObserved = traces.Enqueue, CaptureStarting = carriers.Enqueue };
        var region = new PixelRect(50, 50, 320, 240);
        var baselineFamily=CaptureProcessFamily.Snapshot();
        using(var initial=Read(feed, region)) { }
        var firstFamily=CaptureProcessFamily.KnownProcesses.Except(baselineFamily).ToArray();
        feed.Suspend();
        Thread.Sleep(200);
        Assert.DoesNotContain(traces, trace => trace.Stage == "CaptureBuffers" && trace.Reason == "Released");
        using(var warm=Read(feed, region)) { }
        Assert.Equal(firstFamily,CaptureProcessFamily.KnownProcesses.Except(baselineFamily).ToArray());
        feed.Suspend();
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(7)
            && !traces.Any(trace => trace.Stage == "CaptureBuffers" && trace.Reason == "Released"))
        {
            // Repeated hidden UI polls must not restart the retention deadline.
            feed.Suspend();
            Thread.Sleep(20);
        }
        Assert.Single(traces, trace => trace.Stage == "CaptureBuffers" && trace.Reason == "Released");
        Assert.All(firstFamily,pid=>Assert.DoesNotContain(pid,CaptureProcessFamily.KnownProcesses));
        using var fresh = Read(feed, region);
        Assert.Equal(160 * 120 * 4, fresh.Pixels.Length);
        Assert.DoesNotContain(CaptureProcessFamily.KnownProcesses.Except([Environment.ProcessId]),pid=>firstFamily.Contains(pid));
        Assert.Single(traces,trace=>trace.Stage=="CaptureOwner" && trace.Reason=="Retired");
    }

    private static BackdropFrame Read(SharedBackdropFeed feed, PixelRect region)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(4))
        {
            if (feed.TryRead(region, 160, 120, out var frame) && frame is not null) return frame;
            Thread.Sleep(10);
        }
        throw new TimeoutException("Compatible native capture did not resume: " + feed.Failure);
    }

    private sealed class IdleCapture(ScreenRect bounds) : IDesktopSceneCapture
    {
        internal volatile bool Disposed;
        internal int Reads;
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false)
        {
            Interlocked.Increment(ref Reads);
            return new(bounds.Width, bounds.Height, new byte[bounds.Width * bounds.Height * 4])
                { SourceQpcTicks = Stopwatch.GetTimestamp() };
        }
        public void ReleaseReadbackBuffers() { }
        public void Dispose() => Disposed = true;
    }
}
