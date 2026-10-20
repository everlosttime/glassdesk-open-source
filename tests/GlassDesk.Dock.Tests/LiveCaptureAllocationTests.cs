using System.Diagnostics;
using System.Reflection;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using Xunit.Abstractions;

namespace GlassDesk.Dock.Tests;

public sealed class LiveCaptureAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void UnchangedLiveFramesLeaseTheSamePixelsWithoutLargeAllocations()
    {
        using var fixture = new CaptureFixture();
        var pixels = new byte[640 * 500 * 4];
        new Random(1234).NextBytes(pixels);
        fixture.Publish(pixels, 1);
        using var first = fixture.Read();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 12; i++)
        {
            using var read = fixture.Read();
            Assert.Same(first.Pixels, read.Pixels);
            Assert.Equal(first.Sequence, read.Sequence);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"Repeated live snapshot: {allocated / 12} bytes/read");
        Assert.True(allocated < 16_384, $"Unchanged snapshots allocated {allocated} bytes.");

        // New source timestamps with identical pixels are also safe to reuse.
        fixture.Publish((byte[])pixels.Clone(), 2);
        using var later = fixture.Read();
        Assert.Equal(first.Pixels, later.Pixels);
        Assert.Equal(first.Sequence, later.Sequence);
    }

    [Fact]
    public void ChangedLiveFramesTransferOwnedPixelsWithoutCopyingAndKeepReadersStable()
    {
        using var fixture = new CaptureFixture();
        var original = new byte[640 * 500 * 4];
        original[0] = 10;
        fixture.Publish(original, 1);
        using var first = fixture.Read();
        var changed = (byte[])original.Clone();
        changed[0] = 90;
        fixture.Publish(changed, 2);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        using var second = fixture.Read();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"Changed live snapshot: {allocated} bytes/read");
        Assert.True(allocated < changed.Length * 1.1, $"A {changed.Length}-byte capture allocated {allocated} bytes.");
        Assert.NotSame(first, second);
        Assert.Equal((byte)10, first.Pixels[0]);
        Assert.Equal((byte)90, second.Pixels[0]);
        Assert.Same(changed, second.Pixels);
    }

    [Fact]
    public void SuspendingLiveCaptureDoesNotReplayTheOldSnapshotOnReveal()
    {
        using var fixture = new CaptureFixture();
        fixture.Publish(new byte[640 * 500 * 4], 1);
        using var before = fixture.Read();
        fixture.Background.Suspend();
        Assert.Null(fixture.Background.Capture(fixture.Region, 640, 500, 20, true));
        fixture.Publish(new byte[640 * 500 * 4], 2);
        using var after = fixture.Read();
        Assert.NotSame(before, after);
    }

    [Fact]
    public void MovingTheCaptureCannotReusePixelsFromTheOldCoordinates()
    {
        using var fixture = new CaptureFixture();
        fixture.Publish(new byte[640 * 500 * 4], 1);
        using var before = fixture.Read();
        fixture.Region = fixture.Region with { X = 500 };
        Assert.Null(fixture.Background.Capture(fixture.Region, 640, 500, 20, true));
        fixture.Publish(new byte[640 * 500 * 4], 2);
        using var after = fixture.Read();
        Assert.NotSame(before, after);
    }

    private sealed class CaptureFixture : IDisposable
    {
        internal readonly GlassBackgroundLayer Background = new();
        internal PixelRect Region = new(100, 100, 1280, 1000);
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _stopped = new();
        private LatestBackdropState? _state;

        internal CaptureFixture()
        {
            var feed = new SharedBackdropFeed((state, cancellation) =>
            {
                _state = state;
                _ready.Set();
                cancellation.WaitHandle.WaitOne();
                _stopped.Set();
            });
            typeof(GlassBackgroundLayer).GetField("_compatible", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(Background, feed);
            Assert.Null(Background.Capture(Region, 640, 500, 20, true));
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(2)));
        }

        internal void Publish(byte[] pixels, long sequence)
        {
            var now = Stopwatch.GetTimestamp();
            Assert.True(_state!.Publish(BackdropFrame.FromOwnedPixels(_state.CurrentRequest!, sequence, now, now, pixels)));
        }

        internal GlassBackgroundFrame Read() => Assert.IsType<GlassBackgroundFrame>(Background.Capture(Region, 640, 500, 20, true));

        public void Dispose()
        {
            Background.Dispose();
            Assert.True(_stopped.Wait(TimeSpan.FromSeconds(2)));
            _ready.Dispose();
            _stopped.Dispose();
        }
    }
}
