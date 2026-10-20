using System.Reflection;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassBackgroundReadinessTests
{
    [Fact]
    public void WallpaperAcquisitionCannotBlockThePresentationThread()
    {
        StaTest.Run(() =>
        {
            using var background = new GlassBackgroundLayer();
            var field = typeof(GlassBackgroundLayer).GetField("_wallpaperFeed", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(background, new SharedBackdropFeed((_, cancellation) => cancellation.WaitHandle.WaitOne()));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var frame = background.Capture(new(100,100,80,40),80,40,10,false);
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(150));
            Assert.NotNull(frame); Assert.Equal(GlassBackdropSource.Basic,frame.Source);
        });
    }
    [Fact]
    public void ATransientLiveCaptureFailureDoesNotReplaceCompletedMaterialWithBasic()
    {
        StaTest.Run(() =>
        {
            using var background = WithBlackWallpaperAndPendingCapture();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var feed = new SharedBackdropFeed((_, _) => throw new InvalidOperationException("driver interrupted"));
            typeof(GlassBackgroundLayer).GetField("_compatible", flags)!.SetValue(background, feed);
            var region = new PixelRect(100, 100, 80, 40);
            background.Capture(region, 80, 40, 10, allowDesktop: true);
            Assert.True(SpinWait.SpinUntil(() => feed.Failure is not null, TimeSpan.FromSeconds(2)));
            var result = background.Capture(region, 80, 40, 10, allowDesktop: true);
            Assert.Null(result);
            Assert.False(background.RequiresBasicBackground);
            // Failure and retries must share the original unavailability deadline.
            typeof(GlassBackgroundLayer).GetField("_pendingSince", flags)!.SetValue(background,
                System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 4);
            var expired = background.Capture(region, 80, 40, 10, allowDesktop: true);
            Assert.NotNull(expired);
            Assert.Equal(GlassBackdropSource.Basic, expired.Source);
            Assert.True(background.RequiresBasicBackground);
        });
    }

    [Fact]
    public void WaitingForLiveDesktopDoesNotPublishAnOpaqueBlackWallpaper()
    {
        StaTest.Run(() =>
        {
            using var background = WithBlackWallpaperAndPendingCapture();
            var result = background.Capture(new PixelRect(100, 100, 80, 40), 80, 40, 10, allowDesktop: true);
            Assert.Null(result);
        });
    }

    [Fact]
    public void ExplicitWallpaperModeStillRendersASolidBlackDesktop()
    {
        StaTest.Run(() =>
        {
            using var background = WithBlackWallpaperAndPendingCapture();
            GlassBackgroundFrame? result = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                result = background.Capture(new(100,100,80,40),80,40,10,false);
                return result?.Source == GlassBackdropSource.Wallpaper;
            },TimeSpan.FromSeconds(2)));
            Assert.NotNull(result);
            Assert.Equal(GlassBackdropSource.Wallpaper, result.Source);
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, result.Pixels[..4]);
        });
    }

    private static GlassBackgroundLayer WithBlackWallpaperAndPendingCapture()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var background = new GlassBackgroundLayer();
        // Control only the asynchronous pixel source. Selection and the real feed/resize
        // remain unchanged; black content must pass without a fault heuristic.
        var pendingFeed = new SharedBackdropFeed((_, cancellation) => cancellation.WaitHandle.WaitOne());
        typeof(GlassBackgroundLayer).GetField("_compatible", flags)!.SetValue(background, pendingFeed);
        typeof(GlassBackgroundLayer).GetField("_wallpaperFeed", flags)!.SetValue(background,
            new SharedBackdropFeed((region, _, _) => new BlackWallpaperSource(region)));
        return background;
    }

    private sealed class BlackWallpaperSource(ScreenRect region) : IDesktopSceneCapture
    {
        private bool _delivered;
        public bool AllowsStaticContent => true;
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels? TryRead(TimeSpan waitForFrame=default, CancellationToken cancellation=default,
            long sourceNotBeforeTicks=0, bool reusePixels=false)
        {
            if (_delivered) return null;
            _delivered = true;
            var pixels = new byte[region.Width*region.Height*4];
            for(var i=3;i<pixels.Length;i+=4) pixels[i]=255;
            return new(region.Width,region.Height,pixels) {SourceQpcTicks=System.Diagnostics.Stopwatch.GetTimestamp(),
                IsStatic=true,Source=GlassBackdropSource.Wallpaper};
        }
        public void ReleaseReadbackBuffers() { }
        public void Dispose() { }
    }
}
