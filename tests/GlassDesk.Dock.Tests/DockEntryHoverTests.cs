using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DockEntryHoverTests
{
    [Theory]
    [InlineData("LauncherButton", "LaunchpadTile")]
    [InlineData("ThisPcButton", "ThisPcTile")]
    [InlineData("DesktopIconsButton", "DesktopIconsTile")]
    public void BuiltinEntryUsesTheSameScaleCurveAsAppTilesAndReturnsToRest(string buttonName, string iconName)
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                var button = Assert.IsType<Button>(window.FindName(buttonName));
                var icon = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(iconName));
                var strip = new DockTileStrip();
                strip.SetMetrics(48, 8, DockSettings.FixedMagnificationPeak, 8, 0);
                strip.SetTiles([new DockTileModel { EntryId = "app", DisplayName = "App" }]);
                button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
                var stepEntry = typeof(DockWindow).GetMethod("AnimateEntryHoverFrame", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(stepEntry);
                var stepApp = typeof(DockTileStrip).GetMethod("AnimateFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var center = DockMagnification.SlotCenter(0, 48, 8, DockSettings.FixedMagnificationPeak);
                foreach (var delta in new[] { .016, .033, .05, .016, .1 })
                {
                    stepEntry.Invoke(window, [delta]);
                    stepApp.Invoke(strip, [center, delta]);
                    var scale = Assert.IsType<ScaleTransform>(icon.RenderTransform);
                    Assert.Equal(strip.PaintedTiles.Single().Scale, scale.ScaleX, 10);
                    Assert.Equal(scale.ScaleX, scale.ScaleY);
                }
                Assert.True(((ScaleTransform)icon.RenderTransform).ScaleX > 1);
                button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
                for (var i = 0; i < 40; i++) stepEntry.Invoke(window, [.016]);
                Assert.Equal(1, ((ScaleTransform)icon.RenderTransform).ScaleX);
            }
            finally { window.Close(); }
        });
    }
}
