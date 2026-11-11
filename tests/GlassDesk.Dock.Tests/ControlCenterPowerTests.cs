using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Reflection;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class ControlCenterPowerTests
{
    [Theory]
    [InlineData("ShutdownMenuItem", SystemPowerAction.Shutdown)]
    [InlineData("RestartMenuItem", SystemPowerAction.Restart)]
    public void SelectingMenuItemConfirmsTheMatchingActionAndCancelLeavesControlsUsable(
        string menuItemName, SystemPowerAction expectedAction)
    {
        StaTest.Run(() =>
        {
            var center = new ControlCenterWindow();
            try
            {
                var confirmations = new List<SystemPowerAction>();
                var controller = new SystemPowerController(action =>
                {
                    confirmations.Add(action);
                    return false;
                }, _ => throw new Xunit.Sdk.XunitException("Canceled menu action started a process."));
                typeof(ControlCenterWindow).GetField("_powerActions", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(center, controller);

                Assert.IsType<MenuItem>(center.FindName(menuItemName))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal(new[] { expectedAction }, confirmations);
                Assert.True(Assert.IsType<Button>(center.FindName("PowerButton")).IsEnabled);
            }
            finally { center.Close(); }
        });
    }

    [Fact]
    public void PowerButtonFitsImmediatelyBeforeSettingsAndOffersBothActions()
    {
        StaTest.Run(() =>
        {
            var center = new ControlCenterWindow();
            try
            {
                var power = Assert.IsType<Button>(center.FindName("PowerButton"));
                var settings = Assert.IsType<Button>(center.FindName("SettingsButton"));
                var content = (FrameworkElement)center.Content;
                content.Measure(new Size(center.Width, center.Height));
                content.Arrange(new Rect(0, 0, center.Width, center.Height));
                var right = power.TranslatePoint(new Point(power.ActualWidth, 0), content).X;
                var settingsLeft = settings.TranslatePoint(new Point(0, 0), content).X;
                Assert.InRange(settingsLeft - right, 4, 12);
                Assert.Equal(settings.ActualWidth, power.ActualWidth);
                Assert.Equal(settings.ActualHeight, power.ActualHeight);
                Assert.NotNull(power.ContextMenu);
                Assert.Collection(power.ContextMenu.Items.Cast<MenuItem>(),
                    item => Assert.Equal("关机", item.Header),
                    item => Assert.Equal("重启", item.Header));
            }
            finally { center.Close(); }
        });
    }

    [Fact]
    public void PowerMenuOpensWithoutHidingControlCenterAndClosesWhenCenterHides()
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var center = new ControlCenterWindow();
            center.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                center.ShowNear(new PixelRect(1400, 40, 500, 54));
                var power = Assert.IsType<Button>(center.FindName("PowerButton"));
                power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(power.ContextMenu.IsOpen);
                Assert.True(center.IsVisible);
                center.Hide();
                Assert.False(power.ContextMenu.IsOpen);
            }
            finally { center.Close(); }
        });
    }
}
