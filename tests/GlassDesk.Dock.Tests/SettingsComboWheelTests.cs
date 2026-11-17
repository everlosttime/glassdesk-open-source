using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public class SettingsComboWheelTests
{
    [Theory]
    [InlineData("ControlCenterMaterialCombo", false)]
    [InlineData("ControlCenterMaterialCombo", true)]
    [InlineData("DockMaterialCombo", false)]
    [InlineData("LaunchpadIconStyleCombo", false)]
    public void WheelDoesNotChangeSelection(string name, bool open)
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow { ShowActivated = false };
            try
            {
                window.Show();
                var combo = Assert.IsType<ComboBox>(window.FindName(name));
                combo.SelectedIndex = 1;
                combo.Focus();
                combo.IsDropDownOpen = open;
                foreach (var delta in new[] { -120, 120 })
                {
                    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    combo.RaiseEvent(wheel);
                    if (!wheel.Handled)
                        combo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                        { RoutedEvent = Mouse.MouseWheelEvent });
                    Assert.Equal(1, combo.SelectedIndex);
                    Assert.True(wheel.Handled);
                }
                combo.IsDropDownOpen = false;
                // Explicit selection still works.
                combo.SelectedIndex = 0;
                Assert.Equal(0, combo.SelectedIndex);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ClosedComboForwardsWheelToSettingsPage()
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow { ShowActivated = false };
            try
            {
                window.Show();
                var combo = Assert.IsType<ComboBox>(window.FindName("ControlCenterMaterialCombo"));
                var scroll = Assert.IsType<ScrollViewer>(window.FindName("SettingsScroll"));
                var forwarded = 0;
                scroll.AddHandler(Mouse.MouseWheelEvent, new MouseWheelEventHandler((_, e) => forwarded = e.Delta), true);
                combo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Assert.Equal(-120, forwarded);
            }
            finally { window.Close(); }
        });
    }
}
