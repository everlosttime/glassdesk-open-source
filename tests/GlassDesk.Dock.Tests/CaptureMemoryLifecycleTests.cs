using System.Diagnostics;
using System.Reflection;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class CaptureMemoryLifecycleTests
{
    [Fact]
    public void DiagnosticNativeSnapshotUsesOneSourceFrameAcrossBoundedMappingTiles()
    {
        var start=new ProcessStartInfo(CompatibilityProbePath.File("GlassCaptureProbe.exe"));
        start.ArgumentList.Add("--capture-protocol-fixture");start.ArgumentList.Add("tiled-snapshot");
        using var capture=new IsolatedDesktopSceneCapture(new(0,0,1600,900),start);
        var frame=capture.TryRead();Assert.NotNull(frame);
        Assert.Equal(1600*900*4,frame.Pixels.Length);
        Assert.Equal((byte)33,frame.Pixels[0]);Assert.Equal((byte)33,frame.Pixels[^1]);
    }
    [Fact]
    public void SceneInvalidationRejectsPreviouslyQueuedSourceTimeEvenWithNewGeneration()
    {
        using var state=new LatestBackdropState();state.Request(new(0,0,2,2),2,2);
        var oldSource=Stopwatch.GetTimestamp();
        state.InvalidateScene();var request=state.CurrentRequest!;
        Assert.True(request.SourceNotBeforeTicks>=oldSource);
        var end=Stopwatch.GetTimestamp();
        Assert.False(state.Publish(new BackdropFrame(request,1,oldSource,end,new byte[16],oldSource-1)));
        var fresh=Stopwatch.GetTimestamp();
        Assert.True(state.Publish(new BackdropFrame(request,2,fresh,fresh,new byte[16],fresh)));
    }
    [Fact]
    public void ReplacementWorkerCannotReuseThePreviousSceneGeneration()
    {
        using var first=new LatestBackdropState();using var replacement=new LatestBackdropState();
        var old=first.Request(new(0,0,2,2),2,2);first.Dispose();
        var fresh=replacement.Request(new(0,0,2,2),2,2);
        Assert.NotEqual(old.Generation,fresh.Generation);
        Assert.False(replacement.Publish(new BackdropFrame(old,1,1,2,new byte[16],1)));
    }

    [Fact]
    public void RejectingDuplicatePublicationDoesNotReturnTheCurrentReadersBuffer()
    {
        using var pool=new SamplePixelPool();using var state=new LatestBackdropState();
        var request=state.Request(new(0,0,2,2),2,2);var pixels=pool.TryRent(16)!;pixels.Pixels[0]=73;
        var frame=BackdropFrame.FromLease(request,1,1,2,pixels,1);
        Assert.True(state.Publish(frame));Assert.False(state.Publish(frame));
        Assert.True(state.TryRead(2,10,out var reader));
        using(reader) Assert.Equal((byte)73,reader!.Pixels.Span[0]);
    }

    [Fact]
    public void InvalidPublicationExtentReturnsTheTransferredLease()
    {
        using var pool=new SamplePixelPool();var request=new BackdropRequest(new(0,0,2,2),2,2,1);
        Assert.Throws<ArgumentException>(()=>BackdropFrame.FromLease(request,1,1,2,pool.TryRent(32)!,1));
        using var first=pool.TryRent(32);using var second=pool.TryRent(32);using var third=pool.TryRent(32);
        Assert.NotNull(first);Assert.NotNull(second);Assert.NotNull(third);
    }
    [Fact]
    public void IdenticalSourceFramesKeepContentRevisionSoOpticalCompositionCanStayIdle()
    {
        using var state=new LatestBackdropState();var request=state.Request(new(0,0,2,2),2,2);
        var property=typeof(BackdropFrame).GetProperty("ContentRevision",BindingFlags.Instance|BindingFlags.NonPublic);
        Assert.NotNull(property);
        Assert.True(state.Publish(new BackdropFrame(request,1,1,2,new byte[16],1)));
        Assert.True(state.TryRead(2,10,out var first));
        Assert.True(state.Publish(new BackdropFrame(request,2,2,3,new byte[16],2)));
        Assert.True(state.TryRead(3,10,out var same));
        Assert.Equal(property.GetValue(first),property.GetValue(same));
        var changed=new byte[16];changed[0]=37;
        Assert.True(state.Publish(new BackdropFrame(request,3,3,4,changed,3)));
        Assert.True(state.TryRead(4,10,out var different));
        Assert.NotEqual(property.GetValue(same),property.GetValue(different));
        first?.Dispose();same?.Dispose();different?.Dispose();
    }
    [Fact]
    public void LatestStateRetiresOwnerWithoutOverwritingReaderPixels()
    {
        using var pool=new SamplePixelPool();using var state=new LatestBackdropState();
        var request=state.Request(new(0,0,2,2),2,2);
        var first=pool.TryRent(16)!;first.Pixels[0]=41;var original=first.Pixels;
        Assert.True(state.Publish(BackdropFrame.FromLease(request,1,1,2,first,1)));
        Assert.True(state.TryRead(2,10,out var reader));
        var second=pool.TryRent(16)!;second.Pixels[0]=82;
        Assert.True(state.Publish(BackdropFrame.FromLease(request,2,2,3,second,2)));
        state.Suspend();
        using var recycled=pool.TryRent(16)!;Array.Fill(recycled.Pixels,(byte)199);
        Assert.Equal((byte)41,reader!.Pixels.Span[0]);reader.Dispose();
        using var available=pool.TryRent(16)!;
        Assert.Same(original,available.Pixels);
    }

    [Fact]
    public void DeepSleepGatesReadsAndCancelsWorkerBeforeAReplacement()
    {
        var source=new Source(new(0,0,2,2));
        using var feed=new SharedBackdropFeed((region,_,_)=>source);
        Assert.True(Read(feed));feed.SetPerformanceSleeping(true);
        Assert.False(feed.TryRead(new(0,0,2,2),2,2,out _));
        Assert.True(SpinWait.SpinUntil(()=>source.Disposed,TimeSpan.FromSeconds(2)));
    }
    [Fact]
    public void HiddenPastConfiguredRetentionRetiresNativeOwnerAndResumeCreatesOnlyOneReplacement()
    {
        var owners = new List<Source>();
        using var feed = new SharedBackdropFeed((region, _, _) =>
        {
            lock (owners)
            {
                Assert.All(owners, owner => Assert.True(owner.Disposed));
                var owner = new Source(region); owners.Add(owner); return owner;
            }
        }, TimeSpan.FromMilliseconds(200));
        Assert.True(Read(feed)); feed.Suspend();
        Assert.True(SpinWait.SpinUntil(() => owners[0].Disposed, TimeSpan.FromSeconds(6)),
            "Hidden capture still owns the native session after retention expires.");
        Assert.True(Read(feed)); Assert.Equal(2, owners.Count);
    }

    [Fact]
    public void ThreeOutstandingSampleLeasesDropTheFourthFrameUntilAReaderReleases()
    {
        using var pool=new SamplePixelPool();
        using var first=pool.TryRent(16);using var second=pool.TryRent(16);using var third=pool.TryRent(16);
        Assert.NotNull(first);Assert.NotNull(second);Assert.NotNull(third);
        Assert.Null(pool.TryRent(16));
        first.Dispose();using var replacement=pool.TryRent(16);Assert.NotNull(replacement);
    }

    [Fact]
    public void NativeCropCanBeReadDirectlyIntoBoundedSamplePixels()
    {
        StaTest.Run(() =>
        {
            using var capture = new IsolatedDesktopSceneCapture(new ScreenRect(0, 0, 1600, 900),
                executable: CompatibilityProbePath.File("GlassCaptureProbe.exe"));
            var target = new byte[320 * 180 * 4];
            CapturePixels? frame = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                frame=capture.TryReadSampled(new(0,0,1600,900),320,180,target,TimeSpan.FromMilliseconds(30));
                return frame is not null;
            }, TimeSpan.FromSeconds(6)));
            Assert.Same(target, frame!.Pixels);
            Assert.Equal(320, frame.Width); Assert.Equal(180, frame.Height);
            var mapping = (SharedCapturePixels)typeof(IsolatedDesktopSceneCapture).GetField("_sharedPixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
            Assert.InRange(mapping.Capacity, target.Length, 4_000_000);
        });
    }

    private static bool Read(SharedBackdropFeed feed) => SpinWait.SpinUntil(() =>
    {
        if (!feed.TryRead(new PixelRect(0, 0, 2, 2), 2, 2, out var frame)) return false;
        frame?.Dispose();return true;
    }, TimeSpan.FromSeconds(3));

    private sealed class Source(ScreenRect region) : IDesktopSceneCapture
    {
        internal volatile bool Disposed;
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false) =>
            new(region.Width, region.Height, new byte[region.Width * region.Height * 4]) { SourceQpcTicks = Stopwatch.GetTimestamp() };
        public void ReleaseReadbackBuffers() { }
        public void Dispose() => Disposed = true;
    }
}
