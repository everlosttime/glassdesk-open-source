using System.Reflection;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherRevealGridTests
{
    [Fact]
    public void NormalCloseKeepsTheSamplingGridAndExpiredHiddenSnapshotsAreReacquired()
    {
        StaTest.Run(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_RETAIN_REVEAL_GRID");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_RETAIN_REVEAL_GRID", "1");
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((_, cancellation) => cancellation.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", flags)!.GetValue(surface)!;
            var field = background.GetType().GetField("_compatible", flags)!;
            ((IDisposable)field.GetValue(background)!).Dispose(); field.SetValue(background, feed);
            var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
            var captures = 0;
            surface.CaptureRevealBackdrop = (_, width, height) => { captures++; return Enumerable.Repeat((byte)80, width * height * 4).ToArray(); };
            try
            {
                var display = DisplayService.Primary()!;
                var source = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
                window.ShowWithTransition(source);
                var grid = typeof(LiquidGlassSurface).GetField("_transitionCaptureBounds", flags)!;
                var expected = grid.GetValue(surface); Assert.NotNull(expected);
                var complete = typeof(LauncherWindow).GetMethod("CompleteContainerTransform", flags)!;
                complete.Invoke(window, [1d]);
                window.CloseWithTransition(); complete.Invoke(window, [0d]);
                Assert.Equal(expected, grid.GetValue(surface));
                var captured = captures;
                Thread.Sleep(150);
                window.ShowWithTransition(source);
                Assert.True(captures > captured, "A hidden snapshot older than the safe source age must be refreshed.");
                Assert.Equal(expected, grid.GetValue(surface));
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_RETAIN_REVEAL_GRID", previous); }
        });
    }
}
