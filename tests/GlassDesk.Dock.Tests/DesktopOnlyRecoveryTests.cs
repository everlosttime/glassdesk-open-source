using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopOnlyRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(19045, false)]
    [InlineData(19045, true)]
    [InlineData(26100, false)]
    [InlineData(26100, true)]
    public void BothPinnedBarsRenderTranslucentlyAfterHiddenWallpaperFallback(int build, bool statusBar)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { PinBarsToDesktop = true, ReduceMotion = true });
            GlassWindow owner = statusBar ? new StatusBarWindow() : new DockWindow();
            owner.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                _ = new WindowInteropHelper(owner).EnsureHandle();
                var surface = (LiquidGlassSurface)owner.FindName("GlassSurface");
                var reads = 0;
                using var feed = new SharedBackdropFeed((region, _, _) => new Source(region,
                    () => GlassBackdropSource.Wallpaper, true, () => Interlocked.Increment(ref reads)));
                using var background = new GlassBackgroundLayer(build);
                typeof(GlassBackgroundLayer).GetField("_desktopOnlyFeed", Private)!.SetValue(background, feed);
                var field = typeof(LiquidGlassSurface).GetField("_background", Private)!;
                ((GlassBackgroundLayer)field.GetValue(surface)!).Dispose();
                field.SetValue(surface, background);
                surface.IsBackdropActive = () => true;
                surface.SetContainerTransitionBounds(new Rect(0, -100, 200, 200));
                owner.Show();
                // Complete the reveal endpoint; this test exercises the material,
                // independently of the status bar's three-second reveal admission.
                if (owner is StatusBarWindow status)
                    status.SetValue((DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty",
                        BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!, 1d);
                surface.Measure(new Size(200, 100));
                surface.Arrange(new Rect(0, 0, 200, 100));
                surface.UpdateLayout();
                var width = Math.Max(1, (int)surface.ActualWidth);
                var height = Math.Max(1, (int)surface.ActualHeight);
                surface.SetPanelShape(new Rect(0, 0, width, height), Math.Min(40, height / 2));
                var onFrame = typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!;
                onFrame.Invoke(surface, [null, EventArgs.Empty]);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) > 0, TimeSpan.FromSeconds(2)));
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    onFrame.Invoke(surface, [null, EventArgs.Empty]);
                    return background.MustDiscardUnverifiedMaterial;
                }, TimeSpan.FromSeconds(2)));
                var stale = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
                stale.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 0, 0, 0, 255 }, 4, 0);
                typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.SetValue(surface, stale);
                onFrame.Invoke(surface, [null, EventArgs.Empty]);
                Assert.Null(typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.GetValue(surface));
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(surface);
                var pixel = new byte[4];
                image.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), pixel, 4, 0);
                Assert.InRange(pixel[3], (byte)1, (byte)80);
            }
            finally { owner.Close(); }
        });
    }

    [Theory]
    [InlineData(19045, false)]
    [InlineData(19045, true)]
    [InlineData(26100, false)]
    [InlineData(26100, true)]
    public void FailedPinnedCaptureRejectsHiddenWallpaperAndRecoversToFreshDesktop(int build, bool black)
    {
        var sourceKind = (int)GlassBackdropSource.Wallpaper;
        var reads = 0;
        using var feed = new SharedBackdropFeed((region, _, _) => new Source(region,
            () => (GlassBackdropSource)Volatile.Read(ref sourceKind), black, () => Interlocked.Increment(ref reads)));
        using var background = new GlassBackgroundLayer(build);
        typeof(GlassBackgroundLayer).GetField("_desktopOnlyFeed", Private)!.SetValue(background, feed);
        var region = new PixelRect(0, 0, 16, 16);
        using var initial = background.Capture(region, 16, 16, 0, true, desktopOnly: true);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) > 0, TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var result = background.Capture(region, 16, 16, 0, true, desktopOnly: true);
            Assert.Null(result);
            return background.MustDiscardUnverifiedMaterial;
        }, TimeSpan.FromSeconds(2)), "A hidden wallpaper fallback must retire the unverified pinned material.");
        Assert.True(background.UsesTranslucentCaptureFallback);
        Assert.False(background.RequiresBasicBackground);
        typeof(GlassBackgroundLayer).GetField("_pendingSince", Private)!.SetValue(background,
            Stopwatch.GetTimestamp() - Stopwatch.Frequency * 4);
        using var expired = background.Capture(region, 16, 16, 0, true, desktopOnly: true);
        Assert.Null(expired);
        Assert.False(background.RequiresBasicBackground);

        Volatile.Write(ref sourceKind, (int)GlassBackdropSource.CompatibleScene);
        GlassBackgroundFrame? recovered = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            recovered = background.Capture(region, 16, 16, 0, true, desktopOnly: true);
            return recovered is not null;
        }, TimeSpan.FromSeconds(2)));
        using (recovered)
        {
            Assert.NotNull(recovered);
            Assert.Equal(GlassBackdropSource.CompatibleScene, recovered.Source);
            Assert.Equal(new byte[] { 0, black ? (byte)0 : (byte)180, 0, 255 }, recovered.Pixels[..4]);
            Assert.False(background.MustDiscardUnverifiedMaterial);
        }
    }

    [Theory]
    [InlineData(19045)]
    [InlineData(26100)]
    public void FailedPinnedWorkerRequestsTranslucentMaterialInsteadOfRetainingAnOpaqueFrame(int build)
    {
        using var feed = new SharedBackdropFeed((_, _) => throw new InvalidOperationException("Readback unavailable"));
        using var background = new GlassBackgroundLayer(build);
        typeof(GlassBackgroundLayer).GetField("_desktopOnlyFeed", Private)!.SetValue(background, feed);
        using var initial = background.Capture(new(0, 0, 16, 16), 16, 16, 0, true, desktopOnly: true);
        Assert.True(SpinWait.SpinUntil(() => feed.Failure is not null, TimeSpan.FromSeconds(2)));
        using var failed = background.Capture(new(0, 0, 16, 16), 16, 16, 0, true, desktopOnly: true);
        Assert.Null(failed);
        Assert.True(background.MustDiscardUnverifiedMaterial);
        Assert.True(background.UsesTranslucentCaptureFallback);
        Assert.False(background.RequiresBasicBackground);
    }

    private sealed class Source(ScreenRect region, Func<GlassBackdropSource> kind, bool black, Action read) : IDesktopSceneCapture
    {
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false)
        {
            var pixels = new byte[region.Width * region.Height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i + 1] = black ? (byte)0 : (byte)180; pixels[i + 3] = 255; }
            read();
            return new(region.Width, region.Height, pixels) { SourceQpcTicks = Stopwatch.GetTimestamp(), Source = kind() };
        }
        public void ReleaseReadbackBuffers() { }
        public void Dispose() { }
    }
}
