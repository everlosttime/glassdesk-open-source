using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherColdRevealTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;


    [Fact]
    public void SleepingLauncherReplaysOpeningFromTheDockInsteadOfJumpingToThePanel()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.AndroidLiquidGlass, UseSystemBackdrop = true });
            var window = new LauncherWindow();
            typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", Private)!.SetValue(window, 1);
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.CaptureRevealBackdrop = (_, w, h) =>
            {
                var pixels = new byte[w * h * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                { pixels[i] = 80; pixels[i + 1] = 100; pixels[i + 2] = 120; pixels[i + 3] = 255; }
                return pixels;
            };
            var display = DisplayService.Primary()!;
            window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
            var anchor = new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48);
            try
            {
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    var timer = Stopwatch.StartNew();
                    window.ShowWithTransition(anchor);
                    var state = JsonSerializer.Serialize(typeof(LauncherWindow).GetMethod("InspectUnifiedTransition", Private)!.Invoke(window, null));
                    Assert.True((bool)typeof(LauncherWindow).GetField("_transformActive", Private)!.GetValue(window)!, $"cycle={cycle}, show={timer.ElapsedMilliseconds}ms, {state}");
                    var motion = (ContainerMorphMotion)typeof(LauncherWindow).GetField("_containerMotion", Private)!.GetValue(window)!;
                    Assert.True(motion.CurrentFrame.Bounds.Width < display.WorkArea.Width / 2, $"cycle={cycle}: {motion.CurrentFrame}");
                    Pump(80);
                    Assert.True((bool)typeof(LauncherWindow).GetField("_transformActive", Private)!.GetValue(window)!, $"cycle={cycle}, animation disappeared after first dispatch, {state}");
                    Pump(1000);
                    window.CloseWithTransition(); Pump(1000);
                    window.SetPerformanceSleeping(true); window.SetBackgroundWorkSuspended(true);
                    var presenter = (GlassDesk.Rendering.NativeDirectPresenter?)typeof(LauncherWindow).GetField("_unifiedPresenter", Private)!.GetValue(window);
                    Assert.NotNull(presenter);
                    Assert.Equal(1u, presenter.Statistics.Width);
                    Assert.Equal(1u, presenter.Statistics.Height);
                    Assert.Equal(0u, presenter.ImageStatistics.ImageCount);
                    Assert.Equal(0ul, presenter.GroupStatistics.ColorBytes);
                    Assert.Equal(0ul, presenter.GroupStatistics.MaskBytes);
                    var shadow = typeof(GlassWindow).GetField("_shadow", Private)!.GetValue(window)!;
                    Assert.False((bool)shadow.GetType().GetField("_suppressed", Private)!.GetValue(shadow)!);
                    if (cycle == 1)
                    {
                        typeof(LauncherWindow).GetMethod("FailUnifiedTransition", Private)!.Invoke(window,
                            [new System.Runtime.InteropServices.COMException("Device reset during game sleep", unchecked((int)0x887A0007))]);
                        Assert.False((bool)typeof(LauncherWindow).GetField("_unifiedFailed", Private)!.GetValue(window)!);
                    }
                    Pump(80);
                    window.SetPerformanceSleeping(false); window.SetBackgroundWorkSuspended(false);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OpeningDoesNotAdvanceWhileItsFreshMaterialIsStillTransparentPending()
    {
        StaTest.Run(() =>
        {
            GlassDesk.UI.BackdropRecovery.RetryBackground();
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.AndroidLiquidGlass, UseSystemBackdrop = true });
            var window = new LauncherWindow();
            var display = DisplayService.Primary()!;
            window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
            typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", Private)!.SetValue(window, 1);
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var pending = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, pending);
            var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
            surface.CaptureRevealBackdrop = (_, _, _) => null;
            void Tick(double seconds)
            {
                typeof(LauncherWindow).GetField("_transformStartedAt", Private)!.SetValue(window, Stopwatch.GetTimestamp() - (long)(seconds * Stopwatch.Frequency));
                typeof(LauncherWindow).GetMethod("OnContainerTransformRendering", Private)!.Invoke(window, [null, EventArgs.Empty]);
            }
            try
            {
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48));
                var motion = (ContainerMorphMotion)typeof(LauncherWindow).GetField("_containerMotion", Private)!.GetValue(window)!;
                Assert.False(surface.IsCurrentRevealMaterialReady);
                for (var i = 0; i < 4; i++) Tick(.25);
                Assert.InRange(motion.Openness, 0, .05);
                Assert.True((bool)typeof(LauncherWindow).GetField("_transformActive", Private)!.GetValue(window)!);
                window.CloseWithTransition(); Tick(.01); Tick(1);
                Assert.False(window.IsVisible);
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48));
                motion = (ContainerMorphMotion)typeof(LauncherWindow).GetField("_containerMotion", Private)!.GetValue(window)!;
                surface.CaptureRevealBackdrop = (_, w, h) => Enumerable.Repeat((byte)100, w * h * 4).ToArray();
                surface.IsBackdropActive = () => false;
                var host = (PixelRect)typeof(LauncherWindow).GetField("_transformHostBounds", Private)!.GetValue(window)!;
                var panel = (PixelRect)typeof(LauncherWindow).GetField("_panelPixelBounds", Private)!.GetValue(window)!;
                surface.PrepareRevealMaterial(host, panel, display.Dpi);
                Assert.True(surface.IsCurrentRevealMaterialReady);
                Tick(.25);
                Assert.InRange(motion.Openness, 0, .05);
                Assert.True((bool)typeof(LauncherWindow).GetField("_unifiedActive", Private)!.GetValue(window)!);
                Assert.True((bool)typeof(LauncherWindow).GetField("_unifiedClockDriving", Private)!.GetValue(window)!);
                Tick(.10);
                Assert.True(motion.Openness > .1);
                var activePresenter = typeof(LauncherWindow).GetField("_unifiedPresenter", Private)!.GetValue(window);
                surface.SetPerformanceSleeping(true);
                Tick(.01); Thread.Sleep(300); Tick(.01);
                Assert.Same(activePresenter, typeof(LauncherWindow).GetField("_unifiedPresenter", Private)!.GetValue(window));
                surface.SetPerformanceSleeping(false);
                surface.PrepareRevealMaterial(host, panel, display.Dpi); Tick(.01);
                window.CloseWithTransition(); Pump(1000);
                Assert.False(window.IsVisible);
                window.SetPerformanceSleeping(true);
                var retainedPresenter = typeof(LauncherWindow).GetField("_unifiedPresenter", Private)!.GetValue(window);
                Assert.NotNull(retainedPresenter);
                surface.CaptureRevealBackdrop = (_, _, _) => null;
                window.SetPerformanceSleeping(false);
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48));
                Assert.False(surface.IsCurrentRevealMaterialReady);
                Assert.Same(retainedPresenter, typeof(LauncherWindow).GetField("_unifiedPresenter", Private)!.GetValue(window));
                typeof(LauncherWindow).GetField("_transformMaterialWaitAt", Private)!.SetValue(window, Stopwatch.GetTimestamp() - 4 * Stopwatch.Frequency);
                Tick(.01); Tick(1);
                Assert.False((bool)typeof(LauncherWindow).GetField("_transformActive", Private)!.GetValue(window)!);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void FirstAnimationCallbackDoesNotConsumeTimeSpentWaitingForTheDispatcher()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            var display = DisplayService.Primary()!;
            window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
            typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", Private)!.SetValue(window, 1);
            try
            {
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 120, display.WorkArea.Bottom - 84, 48, 48));
                typeof(LauncherWindow).GetField("_transformStartedAt", Private)!.SetValue(window, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
                typeof(LauncherWindow).GetMethod("OnContainerTransformRendering", Private)!.Invoke(window, [null, EventArgs.Empty]);
                var motion = (ContainerMorphMotion)typeof(LauncherWindow).GetField("_containerMotion", Private)!.GetValue(window)!;
                Assert.InRange(motion.Openness, 0, .05);
                Assert.True((bool)typeof(LauncherWindow).GetField("_transformActive", Private)!.GetValue(window)!);
            }
            finally { window.Close(); }
        });
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
