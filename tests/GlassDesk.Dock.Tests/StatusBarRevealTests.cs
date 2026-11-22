using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class StatusBarRevealTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletingASlideNeverRestoresItsOldPosition(bool visible)
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var progress = (DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var observer = new System.Windows.Controls.Border();
            System.Windows.Data.BindingOperations.SetBinding(observer, FrameworkElement.TagProperty,
                new System.Windows.Data.Binding { Source = window, Path = new PropertyPath(progress) });
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(System.Windows.Controls.Border));
            var values = new List<double>();
            EventHandler changed = (_, _) => values.Add((double)observer.Tag);
            try
            {
                window.Show(); window.UpdateLayout();
                window.SetValue(progress, visible ? 0d : 1d);
                // A finished clock still overrides the previous endpoint's base value.
                window.BeginAnimation(progress, new System.Windows.Media.Animation.DoubleAnimation(visible ? 1d : 0d, TimeSpan.Zero));
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Assert.Equal(visible ? 1d : 0d, window.GetValue(progress));
                descriptor.AddValueChanged(observer, changed);
                typeof(StatusBarWindow).GetMethod("FinishVisibility", Private)!.Invoke(window, [visible]);
                Assert.DoesNotContain(values, value => Math.Abs(value - (visible ? 1d : 0d)) > .001);
                Assert.Equal(visible, window.IsVisible);
            }
            finally { descriptor.RemoveValueChanged(observer, changed); window.Close(); }
        });
    }

    [Theory]
    [InlineData(GlassDesk.Core.Models.Settings.StatusBarEdge.Top, .75, -1)]
    [InlineData(GlassDesk.Core.Models.Settings.StatusBarEdge.Bottom, 1.5, 1)]
    public void InFlightRevealMovesTowardItsOwnEdgeWithTheScaledRadius(
        GlassDesk.Core.Models.Settings.StatusBarEdge edge, double scale, int direction)
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            try
            {
                window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
                window.ConfigurePlacement(() => (0, edge));
                window.ConfigureSizeScale(() => scale);
                window.RefreshDisplayPlacement();
                window.Show();
                window.UpdateLayout();

                var progress = (DependencyProperty)typeof(StatusBarWindow)
                    .GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                window.SetValue(progress, .5);
                var slide = Assert.IsType<System.Windows.Media.TranslateTransform>(window.FindName("StatusSlideTransform"));
                Assert.Equal(direction * window.ActualHeight * .5, slide.Y);
                Assert.Equal(25 * scale, window.PanelCornerRadius.TopLeft);
                var surface = Assert.IsType<LiquidGlassSurface>(window.FindName("GlassSurface"));
                var materialRadius = (double)typeof(LiquidGlassSurface).GetField("_panelRadius", Private)!.GetValue(surface)!;
                Assert.Equal(25 * scale, materialRadius);
                window.SetValue(progress, 1d);
                Assert.Equal(0, slide.Y);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ClockTickDoesNotQueueSystemWorkWithoutAMetricOrRefreshDue()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            try
            {
                var settings = GlassDesk.UI.Settings.DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                settings.Update(new GlassDesk.Core.Models.Settings.DockSettings
                {
                    ShowStatusBar = true,
                    ShowStatusBarNetworkSpeed = false,
                    StatusBarSecondaryItems = ["music"]
                });
                window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
                window.AttachSettings(settings);

                // Settle only the visibility and sampling state. Tick synchronously so
                // the dispatcher cannot complete a background query behind the assertion.
                typeof(StatusBarWindow).GetField("_started", Private)!.SetValue(window, true);
                typeof(StatusBarWindow).GetField("_targetVisible", Private)!.SetValue(window, true);
                typeof(StatusBarWindow).GetField("_lastSystemRefresh", Private)!.SetValue(window, DateTimeOffset.UtcNow);
                typeof(StatusBarWindow).GetMethod("RefreshClockAndStatus", Private)!.Invoke(window, null);

                var clock = Assert.IsType<System.Windows.Controls.TextBlock>(window.FindName("TimeText"));
                Assert.NotEmpty(clock.Text);
                Assert.False((bool)typeof(StatusBarWindow).GetField("_sampling", Private)!.GetValue(window)!);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CaptureWorkerCanCompleteGuiStartupWithoutWaitingForCapturePipeInput()
    {
        var complete = typeof(GlassDesk.UI.BackdropCaptureWorker).GetMethod("CompleteStartupFeedback", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(complete);
        StaTest.Run(() =>
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(true, complete.Invoke(null, null));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Headless startup must retrieve its posted message without blocking on the parent pipe.");
        });
    }

    [Fact]
    public void ColdRevealPreparesDesktopBeforeExposingAnActiveWindow()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            var acquired = false;
            surface.ReadBackdropScene = () => new GlassBackdropScene();
            surface.CaptureRevealBackdrop = (_, width, height) =>
            {
                acquired = true;
                var pixels = new byte[width * height * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                { pixels[i] = 80; pixels[i + 1] = 160; pixels[i + 2] = 210; pixels[i + 3] = 255; }
                return pixels;
            };
            try
            {
                typeof(StatusBarWindow).GetField("_started", Private)!.SetValue(window, true);
                window.RefreshDisplayPlacement();
                typeof(StatusBarWindow).GetMethod("SetTargetVisible", Private)!.Invoke(window, [true]);
                Assert.True(acquired, "Showing the active HWND first makes PrepareRevealMaterial return without preparing any pixels.");
                Assert.True(surface.IsCurrentRevealMaterialReady, "Reveal must not depend on a multi-second native worker cold start.");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OrdinaryHideKeepsBackdropActiveUntilItsLastVisiblePixel()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                typeof(StatusBarWindow).GetField("_started", Private)!.SetValue(window, true);
                window.Show(); window.UpdateLayout();
                typeof(StatusBarWindow).GetMethod("SetTargetVisible", Private)!.Invoke(window, [true]);
                var progress = (DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                window.BeginAnimation(progress, null); window.SetValue(progress, 1d);
                typeof(StatusBarWindow).GetMethod("SetTargetVisible", Private)!.Invoke(window, [false]);
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                Assert.True(surface.IsBackdropActive!(), "Clearing the active flag at hide intent discards the glass before the slide finishes.");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ReenterDuringTheSlideKeepsItsCurrentProgressAndSleepCancelsIt()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                typeof(StatusBarWindow).GetField("_started", Private)!.SetValue(window, true);
                var target = typeof(StatusBarWindow).GetMethod("SetTargetVisible", Private)!;
                var progress = (DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                target.Invoke(window, [true]);
                target.Invoke(window, [false]);
                window.BeginAnimation(progress, null); window.SetValue(progress, .5d);
                target.Invoke(window, [true]);
                Assert.InRange((double)window.GetValue(progress), .49, .51);
                window.SetPerformanceSleeping(true);
                Assert.False(window.IsVisible);
                Assert.Equal(0d, window.GetValue(progress));
                Assert.False(window.IsPerformanceCaptureRequested);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ReversingAVisibleSlideDoesNotPauseForAnotherDesktopPreparation()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene();
            var captures = 0;
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            surface.ReadBackdropScene = () => scene;
            surface.CaptureRevealBackdrop = (_, width, height) =>
            {
                captures++;
                var pixels = new byte[width * height * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                { pixels[i] = 70; pixels[i + 1] = 140; pixels[i + 2] = 210; pixels[i + 3] = 255; }
                return pixels;
            };
            try
            {
                typeof(StatusBarWindow).GetField("_started", Private)!.SetValue(window, true);
                var target = typeof(StatusBarWindow).GetMethod("SetTargetVisible", Private)!;
                var progress = (DependencyProperty)typeof(StatusBarWindow).GetField("RevealProgressProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                target.Invoke(window, [true]);
                var prepared = captures;
                Assert.True(prepared > 0);
                target.Invoke(window, [false]);
                window.BeginAnimation(progress, null); window.SetValue(progress, .5d);
                scene = new GlassBackdropScene(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
                target.Invoke(window, [true]);
                Assert.Equal(prepared, captures);
                Assert.InRange((double)window.GetValue(progress), .49, .51);
                Assert.Equal(true, typeof(StatusBarWindow).GetField("_animationTarget", Private)!.GetValue(window));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OrdinaryConcealSuspendsWarmCaptureButGameSleepRetiresIt()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                typeof(StatusBarWindow).GetField("_started", Private)!.SetValue(window, true);
                typeof(StatusBarWindow).GetMethod("FinishVisibility", Private)!.Invoke(window, [false]);
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                bool Sleeping() => (bool)surface.InspectPerformance().GetType().GetProperty("sleeping")!.GetValue(surface.InspectPerformance())!;
                Assert.False(Sleeping(), "Ordinary pointer leave must not destroy and recreate the native capture process.");
                window.SetPerformanceSleeping(true);
                Assert.True(Sleeping());
            }
            finally { window.Close(); }
        });
    }
}
