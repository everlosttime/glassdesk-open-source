using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Data;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class PopupRevealTests
{
    [Fact]
    public void InitialShowDoesNotExposeAnOpaqueFrameBeforeStartingTheFade()
    {
        StaTest.Run(() =>
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var (window, show) = Create("center");
            var observer = new Border();
            BindingOperations.SetBinding(observer, FrameworkElement.TagProperty, new Binding(nameof(Window.Opacity)) { Source = window });
            var descriptor = DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(Border))!;
            var values = new List<double>();
            EventHandler changed = (_, _) => { if (window.IsVisible) values.Add((double)observer.Tag); };
            descriptor.AddValueChanged(observer, changed);
            try
            {
                show();
                Assert.DoesNotContain(values.Zip(values.Skip(1)), pair => pair.Second < pair.First - .001);
            }
            finally { descriptor.RemoveValueChanged(observer, changed); window.Close(); }
        });
    }

    [Fact]
    public void EnablingReducedMotionDuringRevealSettlesWithoutAFlash()
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var (window, show) = Create("center");
            try
            {
                show();
                theme.Update(new AppearanceSettings { ReduceMotion = true });
                Assert.Equal(1, window.Opacity);
                Assert.False(window.HasAnimatedProperties);
                var content = ((Grid)window.Content).Children.OfType<Grid>().Single();
                Assert.Equal(0, ((TranslateTransform)content.RenderTransform).Y);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("center")]
    [InlineData("devices")]
    [InlineData("calendar")]
    public void PopoverFadesAndSlidesToItsRestingState(string kind)
    {
        StaTest.Run(() =>
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { ReduceMotion = false });
            var (window, show) = Create(kind);
            try
            {
                show();
                Assert.InRange(window.Opacity, 0, .99);
                var content = ((Grid)window.Content).Children.OfType<Grid>().Single();
                var shift = Assert.IsType<TranslateTransform>(content.RenderTransform);
                Assert.True(shift.Y < 0);
                var previous = window.Opacity;
                PumpUntil(() =>
                {
                    Assert.True(window.Opacity >= previous - .001, "Popup opacity reversed during reveal.");
                    previous = window.Opacity;
                    return window.Opacity >= .999 && !shift.HasAnimatedProperties;
                });
                Assert.Equal(1, window.Opacity);
                Assert.Equal(0, shift.Y);
                Assert.False(window.HasAnimatedProperties);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("center")]
    [InlineData("devices")]
    [InlineData("calendar")]
    public void HideDuringRevealCancelsTheOldClockAndReopenStartsCleanly(string kind)
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var (window, show) = Create(kind);
            try
            {
                show();
                window.Hide();
                Assert.False(window.IsVisible);
                Assert.Equal(1, window.Opacity);
                Assert.False(window.HasAnimatedProperties);
                show();
                PumpUntil(() => window.Opacity >= .999 && !window.HasAnimatedProperties);
                Assert.True(window.IsVisible);
                window.Hide();
                Pump(TimeSpan.FromMilliseconds(300));
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("center")]
    [InlineData("devices")]
    [InlineData("calendar")]
    public void ReducedMotionShowsTheCompletePopoverImmediately(string kind)
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { ReduceMotion = true });
            var (window, show) = Create(kind);
            try
            {
                show();
                Assert.Equal(1, window.Opacity);
                Assert.False(window.HasAnimatedProperties);
                var content = ((Grid)window.Content).Children.OfType<Grid>().Single();
                Assert.True(content.RenderTransform is not TranslateTransform shift || shift.Y == 0);
            }
            finally { window.Close(); }
        });
    }

    private static (GlassWindow Window, Action Show) Create(string kind)
    {
        var session = new PrimaryDisplaySession(DisplayService.Primary()!);
        var anchor = new PixelRect(1400, 40, 500, 54);
        GlassWindow window = kind switch { "center" => new ControlCenterWindow(), "devices" => new DevicePickerWindow(), _ => new DateNotificationWindow() };
        window.BindPrimaryDisplay(session);
        return (window, () =>
        {
            if (window is ControlCenterWindow center) center.ShowNear(anchor);
            else if (window is DevicePickerWindow devices) devices.ShowNear(DevicePickerKind.AudioOutput, anchor);
            else ((DateNotificationWindow)window).ShowNear(anchor);
        });
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(ready(), "Popover animation did not settle.");
    }
    private static void Pump(TimeSpan delay)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
