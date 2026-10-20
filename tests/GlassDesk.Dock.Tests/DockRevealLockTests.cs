using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class DockRevealLockTests
{
    [Fact]
    public void WakingBeforeSleepAnimationCompletesPreservesTheClippedSlidePosition()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                window.BindPrimaryDisplay(new GlassDesk.Platform.Displays.PrimaryDisplaySession(GlassDesk.Platform.Displays.DisplayService.Primary()!));
                window.Show(); window.UpdateLayout();
                var resting = (GlassDesk.Platform.Displays.PixelRect)typeof(DockWindow).GetField("_restingRect", flags)!.GetValue(window)!;
                var offset = resting.Height - .25;
                typeof(DockWindow).GetField("_slideOffsetPixels", flags)!.SetValue(window, offset);
                typeof(DockWindow).GetField("_performanceSleepRequested", flags)!.SetValue(window, true);
                typeof(DockWindow).GetMethod("ApplySlide", flags)!.Invoke(window, [resting, offset]);
                window.SetPerformanceSleeping(false);
                Assert.Equal(offset, (double)typeof(DockWindow).GetField("_slideOffsetPixels", flags)!.GetValue(window)!);
            }
            finally { window.Close(); }
        });
    }
    [Fact]
    public void WakingFromPerformanceSleepDoesNotBypassPreparationByResettingTheSlideToZero()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var surface = (GlassDesk.UI.Controls.LiquidGlassSurface)window.FindName("GlassSurface")!;
            surface.CaptureRevealBackdrop = (_, _, _) => null;
            surface.ReadBackdropScene = () => new();
            using var feed = new SharedBackdropFeed((_, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(GlassDesk.UI.Controls.LiquidGlassSurface).GetField("_background", flags)!.GetValue(surface)!;
            typeof(GlassDesk.UI.Controls.GlassBackgroundLayer).GetField("_compatible", flags)!.SetValue(background, feed);
            try
            {
                window.BindPrimaryDisplay(new GlassDesk.Platform.Displays.PrimaryDisplaySession(GlassDesk.Platform.Displays.DisplayService.Primary()!));
                window.Show(); window.UpdateLayout();
                window.SetPerformanceSleeping(true);
                Assert.True(window.IsPerformanceSleeping);
                window.SetPerformanceSleeping(false);
                var resting = (GlassDesk.Platform.Displays.PixelRect)typeof(DockWindow).GetField("_restingRect", flags)!.GetValue(window)!;
                var slide = (double)typeof(DockWindow).GetField("_slideOffsetPixels", flags)!.GetValue(window)!;
                Assert.True(slide >= resting.Height, "A zero slide exposed the fallback before either source had prepared current material.");
            }
            finally { window.Close(); }
        });
    }
    [Fact]
    public void FirstRevealWaitsForMaterialButCannotKeepTheDockHiddenPastTheRecoveryDeadline()
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            var window = new DockWindow();
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(DockWindow).GetField("_restingRect", flags)!.SetValue(window, new GlassDesk.Platform.Displays.PixelRect(100, 100, 200, 100));
                typeof(DockWindow).GetField("_slideOffsetPixels", flags)!.SetValue(window, 200d);
                typeof(DockWindow).GetField("_panelWasConcealed", flags)!.SetValue(window, true);
                var method = typeof(DockWindow).GetMethod("CanStartPreparedReveal", flags);
                Assert.NotNull(method);
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                Assert.False((bool)method.Invoke(window, [now])!);
                Assert.False((bool)method.Invoke(window, [now + System.Diagnostics.Stopwatch.Frequency * 2])!);
                Assert.True((bool)method.Invoke(window, [now + System.Diagnostics.Stopwatch.Frequency * 3])!);
            }
            finally { window.Close(); }
        });
    }
    [Fact]
    public void LockDoesNotHideAnUncoveredDock()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                var controller = InstallController(window);
                window.ToggleRevealLock();
                Assert.Equal(AutoHideState.Shown, controller.State);
                Update(window, covered: false, pointerAtPosition: false);
                Assert.Equal(AutoHideState.Shown, controller.State);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoveredDockCanRevealAtItsRestingPositionOnlyWhenUnlocked(bool fullscreen)
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                var controller = InstallController(window);
                window.ToggleRevealLock();
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Hidden, controller.State);
                window.ToggleRevealLock();
                Update(window, covered: true, pointerAtPosition: false, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Hidden, controller.State);
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Shown, controller.State);
                // Once revealed, hovering the visible Dock keeps it available.
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen, pointerOverVisibleDock: true);
                Assert.Equal(AutoHideState.Shown, controller.State);
                window.ToggleRevealLock();
                Update(window, covered: true, pointerAtPosition: true, fullscreen: fullscreen);
                Assert.Equal(AutoHideState.Hidden, controller.State);
                Update(window, covered: false, pointerAtPosition: false);
                Assert.Equal(AutoHideState.Shown, controller.State);
            }
            finally { window.Close(); }
        });
    }

    private static AutoHideController InstallController(DockWindow window)
    {
        var controller = new AutoHideController(300);
        typeof(DockWindow).GetField("_autoHide", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, controller);
        return controller;
    }

    private static void Update(DockWindow window, bool covered, bool pointerAtPosition,
        bool fullscreen = false, bool pointerOverVisibleDock = false)
    {
        var method = typeof(DockWindow).GetMethod("UpdateAutoHideState", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(window, [DateTime.UtcNow, fullscreen, covered, pointerAtPosition, pointerOverVisibleDock]);
    }
}
