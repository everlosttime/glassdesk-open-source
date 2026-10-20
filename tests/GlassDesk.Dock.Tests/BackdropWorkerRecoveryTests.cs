using System.Diagnostics;

namespace GlassDesk.Dock.Tests;

public sealed class BackdropWorkerRecoveryTests
{
    [Fact]
    public void FailedFreshDocumentedSceneIsRecreatedByTheProductionFeed()
    {
        long now = 0; var attempts = 0;
        var stalled = new SceneSource(64, () => now += Stopwatch.Frequency, stall: true);
        using var feed = new SharedBackdropFeed((region, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                stalled.Region = region;
                return new RecoveringDesktopSceneCapture(region,
                    () => throw new InvalidOperationException("unsupported native source"), () => stalled, () => now);
            }
            Assert.True(stalled.Disposed);
            return new SceneSource(192, () => { }) { Region = region };
        });
        var timer = Stopwatch.StartNew(); BackdropFrame? restored = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (feed.TryRead(new GlassDesk.Platform.Displays.PixelRect(0, 0, 2, 2), 2, 2, out var frame)
                && frame?.Pixels.Span[0] == 192) { restored = frame; break; }
            frame?.Dispose();
            Thread.Sleep(5);
        }
        Assert.NotNull(restored);
        Assert.Equal(2, attempts);
        Assert.Null(feed.Failure);
        restored?.Dispose();
    }

    private sealed class SceneSource(byte color, Action tick, bool stall = false) : IDesktopSceneCapture
    {
        private bool delivered, changed;
        internal bool Disposed;
        internal ScreenRect Region = new(0, 0, 2, 2);
        public nint CarrierHandle => 1;
        public bool UpdateExclusions()
        {
            if (!stall || !delivered || changed) return false;
            changed = true; return true;
        }
        public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false)
        {
            tick(); if (stall && delivered) return null;
            delivered = true;
            return new(Region.Width, Region.Height, Enumerable.Repeat(color, Region.Width * Region.Height * 4).ToArray()) { SourceQpcTicks = Stopwatch.GetTimestamp() };
        }
        public void ReleaseReadbackBuffers() { }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void UnexpectedWorkerFailureDoesNotLeaveTheVisibleConsumerPermanentlyStopped()
    {
        var attempts = 0;
        using var feed = new SharedBackdropFeed((state, cancel) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("transient driver failure");
            while (!cancel.IsCancellationRequested)
            {
                if (state.CurrentRequest is { } request)
                {
                    var now = Stopwatch.GetTimestamp();
                    state.Publish(new BackdropFrame(request, now, now, now,
                        Enumerable.Repeat((byte)120, request.Width * request.Height * 4).ToArray()));
                }
                cancel.WaitHandle.WaitOne(5);
            }
        });
        var timer = Stopwatch.StartNew();
        BackdropFrame? frame = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (feed.TryRead(new GlassDesk.Platform.Displays.PixelRect(0, 0, 2, 2), 2, 2, out frame)) break;
            Thread.Sleep(5);
        }
        Assert.NotNull(frame);
        Assert.Equal(2, attempts);
        Assert.Null(feed.Failure);
        frame?.Dispose();
    }
}
