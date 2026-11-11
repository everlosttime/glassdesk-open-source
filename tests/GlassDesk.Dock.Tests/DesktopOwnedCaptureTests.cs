using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopOwnedCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopOwnershipExcludesItsSurfaceWithoutReplacingTheBackgroundWithBlack(bool desktopOnly)
    {
        if (WindowsCapabilities.BuildNumber < 22000) return;
        StaTest.Run(() =>
        {
            var bounds = DisplayService.Primary()!.Bounds;
            var region = new ScreenRect(bounds.X + bounds.Width / 2 - 80, bounds.Y + bounds.Height / 2 - 60, 160, 120);
            var executable = CompatibilityProbePath.File("GlassCaptureProbe.exe");
            using var reference = new IsolatedDesktopSceneCapture(region, GlassBackdropSource.LiveDesktop,
                null, executable, desktopOnly: true);
            var referencePixels = Read(reference, region);
            var brightness = Brightness(referencePixels);
            reference.Dispose();
            var own = new Window { Width = 160, Height = 120, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Background = Brushes.Magenta, AllowsTransparency = true,
                ShowActivated = false, ShowInTaskbar = false };
            var helper = new WindowInteropHelper(own);
            var handle = helper.EnsureHandle();
            using var attachment = new DesktopWindowAttachment(handle, owner => helper.Owner = owner);
            try
            {
                Assert.True(attachment.TryAttach());
                using var capture = Create(region, executable, desktopOnly);
                own.Show();
                DisplayService.MoveWindowToPixelRect(handle, new(region.X, region.Y, region.Width, region.Height));
                foreach (var color in new[] { Colors.Magenta, Colors.Cyan })
                {
                    own.Background = new SolidColorBrush(color); Pump(300);
                    var pixels = Read(capture, region);
                    var center = (15 * 40 + 20) * 4;
                    Assert.False(Math.Abs(pixels[center] - color.B) < 35 && Math.Abs(pixels[center + 1] - color.G) < 35
                        && Math.Abs(pixels[center + 2] - color.R) < 35,
                        $"Desktop-owned surface leaked into its background: desktopOnly={desktopOnly}, BGRA={string.Join(',', pixels.Skip(center).Take(4))}");
                    if (brightness > 40 * 30 * 3 * 8)
                        Assert.True(Brightness(pixels) > brightness / 10, "Desktop ownership must not replace the wallpaper with a black mask.");
                }
            }
            finally { own.Close(); }
        });
    }

    private static IDesktopSceneCapture Create(ScreenRect region, string executable, bool desktopOnly)
    {
        if (desktopOnly) return DesktopSceneCaptureFactory.CreateDesktopOnly(region, null, executable);
        return DesktopSceneCaptureFactory.Create(region, false, null, executable);
    }

    private static byte[] Read(IDesktopSceneCapture capture, ScreenRect region)
    {
        var pixels = new byte[40 * 30 * 4];
        var barrier = Stopwatch.GetTimestamp(); var watch = Stopwatch.StartNew();
        CapturePixels? frame = null;
        while (frame is null && watch.Elapsed.TotalSeconds < 6)
        {
            frame = capture.TryReadSampled(new(0, 0, region.Width, region.Height), 40, 30, pixels,
                TimeSpan.FromMilliseconds(30), sourceNotBeforeTicks: barrier);
            Pump(10);
        }
        Assert.NotNull(frame); return pixels;
    }

    private static long Brightness(byte[] pixels) => pixels.Where((_, index) => index % 4 != 3).Sum(value => (long)value);
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
