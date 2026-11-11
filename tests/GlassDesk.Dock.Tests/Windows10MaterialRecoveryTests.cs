using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class Windows10MaterialRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WallpaperRecoveryDiscardsPreviouslyVerifiedLivePixels(bool launchpad)
    {
        StaTest.Run(() =>
        {
            var rejected = 0;
            using var feed = new SharedBackdropFeed((state, stop) =>
            {
                long sequence = 0;
                while (!stop.IsCancellationRequested)
                {
                    if (state.CurrentRequest is { } request)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var pixels = new byte[request.Width * request.Height * 4];
                        for (var index = 0; index < pixels.Length; index += 4)
                        {
                            pixels[index + 1] = 180;
                            pixels[index + 3] = 255;
                        }
                        state.Publish(BackdropFrame.FromOwnedPixels(request, ++sequence, now, now, pixels,
                            source: Volatile.Read(ref rejected) == 0
                                ? GlassBackdropSource.CompatibleScene : GlassBackdropSource.Wallpaper));
                    }
                    if (stop.WaitHandle.WaitOne(8)) return;
                }
            });
            using var surface = new LiquidGlassSurface { UseLaunchpadOptics = launchpad,
                IsBackdropActive = () => true, ReadBackdropScene = () => new GlassBackdropScene(),
                CaptureRevealBackdrop = (_, _, _) => null };
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!;
            ((GlassBackgroundLayer)background.GetValue(surface)!).Dispose();
            background.SetValue(surface, new GlassBackgroundLayer(19045, feed));
            var owner = new TestGlassWindow { Content = surface, Width = 200, Height = 100,
                Left = 50, Top = 50, ShowActivated = false, ShowInTaskbar = false };
            var frame = typeof(LiquidGlassSurface).GetField("_frame", Private)!;
            try
            {
                owner.Show();
                surface.UpdateLayout();
                surface.SetPanelShape(new(0, 0, 200, 100), 20);
                var timer = Stopwatch.StartNew();
                while (frame.GetValue(surface) is null && timer.Elapsed < TimeSpan.FromSeconds(4))
                    global::Program.Pump(TimeSpan.FromMilliseconds(20));
                Assert.NotNull(frame.GetValue(surface));
                Volatile.Write(ref rejected, 1);
                global::Program.Pump(TimeSpan.FromMilliseconds(400));
                Assert.Null(frame.GetValue(surface));
                Assert.Null(typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.GetValue(surface));
                var rendered = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(surface);
                var pixel = new byte[4];
                rendered.CopyPixels(new Int32Rect(100, 50, 1, 1), pixel, 4, 0);
                Assert.InRange(pixel[3], 1, 180);
            }
            finally { owner.Close(); }
        });
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed class TestGlassWindow : GlassWindow;
}
