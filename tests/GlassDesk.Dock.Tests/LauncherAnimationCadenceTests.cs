using System.Reflection;
using System.Text.Json;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherAnimationCadenceTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void OpeningAndClosingUseTheRegularBackgroundCadence(double hertz)
    {
        StaTest.Run(() =>
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", fields)!.GetValue(surface)!;
            var captureField = typeof(GlassBackgroundLayer).GetField("_compatible", fields)!;
            using var feed = new SharedBackdropFeed((_, stop) => stop.WaitHandle.WaitOne());
            ((IDisposable)captureField.GetValue(background)!).Dispose();
            captureField.SetValue(background, feed);
            background.SetDisplayCadence(hertz);
            try
            {
                var display = DisplayService.Primary()!;
                var source = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
                window.ShowWithTransition(source);
                Assert.True(window.IsVisible);
                Assert.False(window.CanNavigatePages);
                AssertInterval();
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", fields)!.Invoke(window, [1d]);
                Assert.True(window.CanNavigatePages);
                AssertInterval();
                window.CloseWithTransition();
                Assert.True(window.IsVisible);
                Assert.False(window.CanNavigatePages);
                AssertInterval();
                window.Hide();
                AssertInterval();
            }
            finally { window.Close(); }

            void AssertInterval()
            {
                var live = JsonSerializer.SerializeToElement(surface.InspectPerformance()).GetProperty("capture").GetProperty("live");
                Assert.Equal(1000d / hertz, live.GetProperty("captureIntervalMilliseconds").GetDouble());
            }
        });
    }
}
