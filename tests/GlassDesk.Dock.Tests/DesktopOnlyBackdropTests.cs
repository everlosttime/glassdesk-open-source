using System.Windows.Interop;
using System.Windows.Threading;
using System.Diagnostics;
using System.Reflection;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopOnlyBackdropTests
{
    [Fact]
    public void DesktopOnlyFactoryReadsTheRealDesktopThroughAnIsolatedHelper()
    {
        var display = DisplayService.Primary()!;
        var area = display.WorkArea;
        var region = new ScreenRect(area.X + 100, area.Y + 100, 120, 80);
        using var capture = DesktopSceneCaptureFactory.CreateDesktopOnly(region, null,
            CompatibilityProbePath.File("GlassCaptureProbe.exe"));
        CapturePixels? frame = null;
        var watch = Stopwatch.StartNew();
        while (frame is null && watch.Elapsed < TimeSpan.FromSeconds(6))
            frame = capture.TryRead(TimeSpan.FromMilliseconds(50));
        Assert.NotNull(frame);
        Assert.Equal(120 * 80 * 4, frame.Pixels.Length);
        Assert.NotEqual(nint.Zero, capture.CarrierHandle);
        Assert.NotEqual(GlassBackdropSource.Wallpaper, frame.Source);
    }

    [Fact]
    public void BackgroundScopeSwitchesBetweenDesktopAndWindowFeedsWithoutReusingTheOtherScope()
    {
        using var desktop = Feed(0, 220, 0);
        using var windows = Feed(220, 0, 0);
        using var background = new GlassBackgroundLayer();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GlassBackgroundLayer).GetField("_desktopOnlyFeed", fields)!.SetValue(background, desktop);
        typeof(GlassBackgroundLayer).GetField("_compatible", fields)!.SetValue(background, windows);
        foreach (var desktopOnly in new[] { true, false, true })
        {
            GlassBackgroundFrame? frame = null;
            var watch = Stopwatch.StartNew();
            while (frame is null && watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                frame = background.Capture(new PixelRect(0, 0, 4, 4), 4, 4, 0, true, desktopOnly);
                if (frame is null) Thread.Sleep(5);
            }
            using (frame)
            {
                Assert.NotNull(frame);
                Assert.Equal(desktopOnly ? (byte)0 : (byte)220, frame.Pixels[0]);
                Assert.Equal(desktopOnly ? (byte)220 : (byte)0, frame.Pixels[1]);
            }
        }
    }

    private static SharedBackdropFeed Feed(byte blue, byte green, byte red) => new((state, stop) =>
    {
        long sequence = 0;
        while (!stop.IsCancellationRequested)
        {
            if (state.CurrentRequest is { } request)
            {
                var pixels = new byte[request.Width * request.Height * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                { pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255; }
                var now = Stopwatch.GetTimestamp();
                state.Publish(new BackdropFrame(request, ++sequence, now, now, pixels, now));
            }
            if (stop.WaitHandle.WaitOne(8)) return;
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedBarsNeverPrepareTheirMaterialFromTheComposedScreen(bool statusBar)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { PinBarsToDesktop = true, ReduceMotion = true });
            GlassWindow owner = statusBar ? new StatusBarWindow() : new DockWindow();
            var display = DisplayService.Primary()!;
            owner.BindPrimaryDisplay(new PrimaryDisplaySession(display));
            try
            {
                _ = new WindowInteropHelper(owner).EnsureHandle();
                var surface = (LiquidGlassSurface)owner.FindName("GlassSurface");
                var screenReads = 0;
                surface.CaptureRevealBackdrop = (_, width, height) =>
                {
                    screenReads++;
                    var pixels = new byte[width * height * 4];
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
                    return pixels;
                };
                var area = display.WorkArea;
                var host = new PixelRect(area.X + 100, area.Y + 100, 200, 100);
                surface.PrepareRevealMaterial(host, host, display.Dpi);
                Assert.Equal(0, screenReads);
                theme.Update(new AppearanceSettings { ReduceMotion = true });
                surface.PrepareRevealMaterial(host, host, display.Dpi);
                Assert.Equal(1, screenReads);
            }
            finally { owner.Close(); }
        });
    }
}
