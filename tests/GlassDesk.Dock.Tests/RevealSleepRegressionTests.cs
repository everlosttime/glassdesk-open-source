using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class RevealSleepRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmConcealedDockReacquiresContentEvenWhenTheWindowSceneIsUnchanged(bool previousSnapshot)
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.CaptureRevealBackdrop = (_, width, height) => SolidPixels(width, height, 20, 30, 220);
            try
            {
                window.Show(); window.UpdateLayout();
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                var offset = typeof(DockWindow).GetField("_slideOffsetPixels", Private)!;
                var apply = typeof(DockWindow).GetMethod("ApplySlide", Private)!;
                void Conceal() { offset.SetValue(window, resting.Height * 2d); apply.Invoke(window, [resting, resting.Height * 2d]); }
                Conceal();
                surface.PrepareRevealMaterial(resting, resting, DisplayService.Primary()!.Dpi);
                if (!previousSnapshot)
                {
                    var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, SolidPixels(2, 2, 20, 30, 220), 8);
                    bitmap.Freeze();
                    typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.SetValue(surface, bitmap);
                    typeof(LiquidGlassSurface).GetField("_transitionBackdropScene", Private)!.SetValue(surface, new GlassBackdropScene());
                }
                offset.SetValue(window, 0d); apply.Invoke(window, [resting, 0d]);
                Conceal();
                var acquired = false;
                surface.CaptureRevealBackdrop = (_, width, height) => { acquired = true; return SolidPixels(width, height, 220, 60, 20); };
                surface.PrepareRevealMaterial(resting, resting, DisplayService.Primary()!.Dpi);
                Assert.True(acquired, "Warm reveal replayed old pixels although the same external window can repaint while hidden.");
                Assert.Null(typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.GetValue(surface));
                Assert.True((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!);
            }
            finally { window.Close(); }
        });
    }

    private static byte[] SolidPixels(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255; }
        return pixels;
    }

    [Fact]
    public void DockRetiresContentOnlyAfterTheEntireHideAnimationIsClipped()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.SetPerformanceSleeping(true);
            try
            {
                window.Show(); window.UpdateLayout();
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, SolidPixels(2, 2, 220, 60, 20), 8);
                var texture = typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!;
                texture.SetValue(surface, bitmap);
                var offset = typeof(DockWindow).GetField("_slideOffsetPixels", Private)!;
                var apply = typeof(DockWindow).GetMethod("ApplySlide", Private)!;
                offset.SetValue(window, resting.Height - 1d); apply.Invoke(window, [resting, resting.Height - 1d]);
                Assert.Same(bitmap, texture.GetValue(surface));
                offset.SetValue(window, (double)resting.Height); apply.Invoke(window, [resting, (double)resting.Height]);
                Assert.Null(texture.GetValue(surface));
                // Remaining concealed ticks must not cancel the next prepared texture.
                texture.SetValue(surface, bitmap);
                offset.SetValue(window, resting.Height + 20d); apply.Invoke(window, [resting, resting.Height + 20d]);
                Assert.Same(bitmap, texture.GetValue(surface));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ConcealedDockRetiresItsTransparentShadowAndCanSampleTheDesktopBeforeReveal()
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            try
            {
                window.Show(); window.UpdateLayout();
                var shadow = (GlassShadow)typeof(GlassWindow).GetField("_shadow", Private)!.GetValue(window)!;
                Assert.True(shadow.IsVisible);
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                var offset = typeof(DockWindow).GetField("_slideOffsetPixels", Private)!;
                var slide = typeof(DockWindow).GetMethod("ApplySlide", Private)!;
                window.SetBackgroundCaptureSleeping(true);
                offset.SetValue(window, resting.Height * 2d);
                slide.Invoke(window, [resting, resting.Height * 2d]);
                Assert.False(shadow.IsVisible);
                window.SetBackgroundCaptureSleeping(false);
                Assert.True((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!);
                offset.SetValue(window, 0d); slide.Invoke(window, [resting, 0d]);
                Assert.True(shadow.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DeepSleepingLauncherRetiresItsLargeLayeredWindowAndRestoresItsPlacementOnWake()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            surface.SetPerformanceSleeping(true);
            surface.CaptureRevealBackdrop = (_, _, _) => null;
            try
            {
                window.ShowWithTransition(null);
                var handle = new WindowInteropHelper(window).Handle;
                var before = DisplayService.GetWindowPixelBounds(handle)!.Value;
                window.Hide(); window.SetPerformanceSleeping(true);
                var sleeping = DisplayService.GetWindowPixelBounds(handle)!.Value;
                Assert.InRange(sleeping.Width, 1, 2);
                Assert.InRange(sleeping.Height, 1, 2);
                window.SetPerformanceSleeping(false);
                window.ShowWithTransition(null);
                Assert.Equal(before, DisplayService.GetWindowPixelBounds(handle)!.Value);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AConcealedDockPreparesTheCurrentDesktopBeforeItsFirstSlidePixel()
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            try
            {
                window.Show(); window.UpdateLayout();
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                var concealed = resting.Height * 2d;
                window.SetBackgroundCaptureSleeping(true);
                typeof(DockWindow).GetField("_slideOffsetPixels", Private)!.SetValue(window, concealed);
                typeof(DockWindow).GetMethod("ApplySlide", Private)!.Invoke(window, [resting, concealed]);
                var captured = false;
                surface.CaptureRevealBackdrop = (_, width, height) =>
                {
                    Assert.Equal(concealed, typeof(DockWindow).GetField("_slideOffsetPixels", Private)!.GetValue(window));
                    captured = true;
                    return null;
                };
                window.SetBackgroundCaptureSleeping(false);
                Assert.True(captured, $"Dock began waking without preparing the current desktop. rect={resting}, offset={typeof(DockWindow).GetField("_slideOffsetPixels", Private)!.GetValue(window)}, active={surface.IsBackdropActive?.Invoke()}, basic={GlassDesk.UI.BackdropRecovery.RequiresBasicRendering}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void LiveCaptureStartsBeforeTheHiddenDesktopSnapshotIsProcessed()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.UseLaunchpadOptics = true;
            var called = false;
            surface.CaptureRevealBackdrop = (_, width, height) =>
            {
                called = true;
                var state = (LatestBackdropState?)typeof(SharedBackdropFeed).GetField("_state", Private)!.GetValue(feed);
                Assert.NotNull(state?.CurrentRequest);
                Assert.Equal(new PixelBounds(52, 52, 348, 248), state.CurrentRequest.Region);
                Assert.False(window.IsVisible);
                return null;
            };
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                surface.PrepareRevealMaterial(new(100, 100, 200, 100), new(100, 100, 200, 100), 96);
                Assert.True(called);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DockSettlingDoesNotChangeTheSamplingRectangleOrReplaceItsPreparedMaterial()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            surface.SetPerformanceSleeping(true);
            try
            {
                window.Show(); window.UpdateLayout();
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                var apply = typeof(DockWindow).GetMethod("ApplySlide", Private)!;
                apply.Invoke(window, [resting, resting.Height / 3d]);
                var moving = typeof(LiquidGlassSurface).GetField("_transitionCaptureBounds", Private)!.GetValue(surface);
                apply.Invoke(window, [resting, 0d]);
                Assert.Equal(moving, typeof(LiquidGlassSurface).GetField("_transitionCaptureBounds", Private)!.GetValue(surface));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PrimedDockCaptureSurvivesTheConcealedTickBeforeTheFirstVisiblePixel()
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.CaptureRevealBackdrop = (_, _, _) => null;
            try
            {
                window.Show(); window.UpdateLayout();
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                var offset = typeof(DockWindow).GetField("_slideOffsetPixels", Private)!;
                var apply = typeof(DockWindow).GetMethod("ApplySlide", Private)!;
                window.SetBackgroundCaptureSleeping(true);
                offset.SetValue(window, resting.Height * 2d); apply.Invoke(window, [resting, resting.Height * 2d]);
                window.SetBackgroundCaptureSleeping(false);
                var state = (LatestBackdropState)typeof(SharedBackdropFeed).GetField("_state", Private)!.GetValue(feed)!;
                var prepared = state.CurrentRequest;
                Assert.NotNull(prepared);
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                Assert.Equal(prepared, state.CurrentRequest);
                window.SetBackgroundCaptureSleeping(true);
                Assert.Null(state.CurrentRequest);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DockRevealPrimingUsesTheFirstVisibleFramesFullSlideSamplingGeometry()
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.CaptureRevealBackdrop = (_, _, _) => null;
            try
            {
                window.Show();
                window.SizeToContent = SizeToContent.Manual; window.Width = 900; window.Height = 100;
                window.UpdateLayout(); window.PositionOnDisplay();
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                var offset = typeof(DockWindow).GetField("_slideOffsetPixels", Private)!;
                var apply = typeof(DockWindow).GetMethod("ApplySlide", Private)!;
                window.SetBackgroundCaptureSleeping(true);
                offset.SetValue(window, resting.Height * 2d); apply.Invoke(window, [resting, resting.Height * 2d]);
                window.SetBackgroundCaptureSleeping(false);
                var state = (LatestBackdropState)typeof(SharedBackdropFeed).GetField("_state", Private)!.GetValue(feed)!;
                var prepared = state.CurrentRequest;
                Assert.NotNull(prepared);
                offset.SetValue(window, 0d); apply.Invoke(window, [resting, 0d]);
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                Assert.Equal(prepared.Region, state.CurrentRequest!.Region);
                Assert.Equal(prepared.Width, state.CurrentRequest.Width);
                Assert.Equal(prepared.Height, state.CurrentRequest.Height);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DeepSleepReleasesFullSizeMaterialAndGpuLensInsteadOfOnlyStoppingItsClock()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new GlassBackdropScene() };
            surface.SetPanelShape(new(0, 0, 200, 100), 20);
            surface.Measure(new(200, 100)); surface.Arrange(new Rect(0, 0, 200, 100));
            var bitmap = new WriteableBitmap(800, 400, 96, 96, PixelFormats.Bgra32, null);
            typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.SetValue(surface, bitmap);
            typeof(LiquidGlassSurface).GetField("_transitionBackdropBounds", Private)!.SetValue(surface, new Rect(0, 0, 200, 100));
            surface.SetContainerTransitionBounds(new Rect(0, 0, 200, 100));
            new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32).Render(surface);
            surface.SetPerformanceSleeping(true);
            Assert.Null(typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.GetValue(surface));
            Assert.Null(((FrameworkElement)typeof(LiquidGlassSurface).GetField("_transitionLayer", Private)!.GetValue(surface)!).Effect);
        });
    }

    [Fact]
    public void CancelledConcealedRevealExpiresItsPrimedCaptureAndWatchdog()
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.CaptureRevealBackdrop = (_, _, _) => null;
            try
            {
                window.Show(); window.UpdateLayout();
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                window.SetBackgroundCaptureSleeping(true);
                typeof(DockWindow).GetField("_slideOffsetPixels", Private)!.SetValue(window, resting.Height * 2d);
                typeof(DockWindow).GetMethod("ApplySlide", Private)!.Invoke(window, [resting, resting.Height * 2d]);
                window.SetBackgroundCaptureSleeping(false);
                var state = (LatestBackdropState)typeof(SharedBackdropFeed).GetField("_state", Private)!.GetValue(feed)!;
                Assert.NotNull(state.CurrentRequest);
                global::Program.Pump(TimeSpan.FromMilliseconds(1300));
                Assert.Null(state.CurrentRequest);
                Assert.False(((System.Windows.Threading.DispatcherTimer)typeof(LiquidGlassSurface).GetField("_timer", Private)!.GetValue(surface)!).IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RevealIntentRequestsCaptureBeforeOpticalVisibilityButDoesNotOverrideGameSleep()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                window.Show(); window.UpdateLayout();
                window.SetBackgroundCaptureSleeping(true);
                var resting = (PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(window)!;
                typeof(DockWindow).GetField("_slideOffsetPixels", Private)!.SetValue(window, resting.Height * 2d);
                typeof(DockWindow).GetMethod("ApplySlide", Private)!.Invoke(window, [resting, resting.Height * 2d]);
                var controller = (AutoHideController)typeof(DockWindow).GetField("_autoHide", Private)!.GetValue(window)!;
                controller.ForceHide(DateTime.UtcNow);
                Assert.False(window.IsPerformanceSurfaceVisible);
                Assert.False(window.IsPerformanceCaptureRequested);
                controller.ForceShown(DateTime.UtcNow);
                Assert.False(window.IsPerformanceSurfaceVisible);
                Assert.True(window.IsPerformanceCaptureRequested);
                window.SetPerformanceSleeping(true);
                Assert.False(window.IsPerformanceCaptureRequested);
            }
            finally { window.Close(); }
        });
    }
}
