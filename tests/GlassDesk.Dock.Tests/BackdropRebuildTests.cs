using System.Reflection;
using System.Diagnostics;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class BackdropRebuildTests
{
    [Fact]
    public void RebuildSignalRetiresTheOldOwnerBeforePublishingReplacementPixels()
    {
        var signal = typeof(SharedBackdropFeed).Assembly.GetType("GlassDesk.UI.BackdropRecovery");
        Assert.NotNull(signal);
        var first = new Source(64);
        var attempts = 0;
        using var feed = new SharedBackdropFeed((region, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { first.Region = region; return first; }
            Assert.True(first.Disposed); return new Source(192) { Region = region };
        });
        bool Read(byte expected)
        {
            if(!feed.TryRead(new PixelRect(0,0,2,2),2,2,out var frame)) return false;
            using(frame) return frame!.Pixels.Span[0]==expected;
        }
        Assert.True(SpinWait.SpinUntil(() => Read(64), TimeSpan.FromSeconds(3)));
        signal.GetMethod("RetryBackground",Type.EmptyTypes)!.Invoke(null, null);
        Assert.True(SpinWait.SpinUntil(() => Read(192), TimeSpan.FromSeconds(3)));
        Assert.Equal(2, attempts);
    }
    private sealed class Source(byte color) : IDesktopSceneCapture
    {
        internal bool Disposed;
        internal ScreenRect Region;
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false)
            => new(Region.Width, Region.Height, Enumerable.Repeat(color, Region.Width * Region.Height * 4).ToArray())
                { SourceQpcTicks = Stopwatch.GetTimestamp() };
        public void ReleaseReadbackBuffers() { }
        public void Dispose() => Disposed = true;
    }
}
