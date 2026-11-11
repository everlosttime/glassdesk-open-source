using System.Reflection;
using System.Text.Json;
using GlassDesk.UI.Controls;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using System.Windows.Interop;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherDisplayCadenceTests
{
    [Theory]
    [InlineData(240, 4.1666666667)]
    [InlineData(144, 6.9444444444)]
    [InlineData(60, 16.6666666667)]
    [InlineData(23.976, 41.7083750417)]
    [InlineData(double.NaN, 8.3333333333)]
    [InlineData(0, 8.3333333333)]
    public void ActiveBackgroundUsesRequestedRateAndSleepRestoresIdle(double hertz, double expected)
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            var method = typeof(LiquidGlassSurface).GetMethod("SetContainerAnimationCadence", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(surface, [true, (double?)hertz]);
            Assert.InRange(Math.Abs(Interval(surface) - expected), 0, .00001);
            method.Invoke(surface, [false, (double?)hertz]);
            Assert.Equal(16, Interval(surface));
            method.Invoke(surface, [true, (double?)hertz]);
            surface.SetPerformanceSleeping(true);
            Assert.Equal(16, Interval(surface));
            method.Invoke(surface, [true, (double?)hertz]);
            surface.SetPerformanceSleeping(false);
            Assert.Equal(16, Interval(surface));
        });
    }

    private static double Interval(LiquidGlassSurface surface) => JsonSerializer.SerializeToElement(surface.InspectPerformance())
        .GetProperty("capture").GetProperty("live").GetProperty("captureIntervalMilliseconds").GetDouble();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LauncherUsesOwnHwndRateOnlyWhenTheSavedOptionIsEnabled(bool enabled)
    {
        StaTest.Run(() =>
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_DISPLAY_CADENCE");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_DISPLAY_CADENCE", "1");
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((_, cancellation) => cancellation.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
            var field = background.GetType().GetField("_compatible", fields)!;
            ((IDisposable)field.GetValue(background)!).Dispose(); field.SetValue(background, feed);
            try
            {
                window.ApplySettings(new() { HighRefreshAnimationsEnabled = enabled });
                var display = DisplayService.Primary()!;
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48));
                var refresh = DisplayService.RefreshForWindow(new WindowInteropHelper(window).Handle)?.Hertz ?? 120;
                Assert.InRange(Math.Abs(Interval(surface) - (enabled ? 1000 / refresh : 16)), 0, .00001);
                window.Hide();
                Assert.Equal(16, Interval(surface));
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_DISPLAY_CADENCE", previous); }
        });
    }
}
