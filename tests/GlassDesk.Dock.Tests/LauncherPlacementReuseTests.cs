using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherPlacementReuseTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void RepeatedPlacementRetainsPublishedShapeUntilMorphOrRadiusChanges()
    {
        StaTest.Run(() =>
        {
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_PLACEMENT_REUSE");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_PLACEMENT_REUSE", "1");
            var window = new LauncherWindow();
            try
            {
                _ = new WindowInteropHelper(window).EnsureHandle();
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                window.PositionOnWorkingArea();
                var initial = surface.Clip;
                window.PositionOnWorkingArea();
                Assert.Same(initial, surface.Clip);

                typeof(GlassWindow).GetProperty("PanelCornerRadius")!.SetValue(window, new CornerRadius(100));
                window.PositionOnWorkingArea();
                Assert.NotSame(initial, surface.Clip);
                var updated = surface.Clip;
                var panel = (PixelRect)typeof(LauncherWindow).GetField("_panelPixelBounds", Private)!.GetValue(window)!;
                typeof(LauncherWindow).GetMethod("ApplyContainerTransformFrame", Private)!.Invoke(window,
                    [new ContainerMorphFrame(new Rect(panel.X, panel.Y, 120, 90), 30, .5, 1, .3, 1, .2)]);
                window.PositionOnWorkingArea();
                Assert.NotSame(updated, surface.Clip);
                Assert.True(surface.Clip.Bounds.Width > 120);
                var restored = surface.Clip;
                window.PositionOnWorkingArea();
                Assert.Same(restored, surface.Clip);
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_PLACEMENT_REUSE", previous); }
        });
    }

    [Fact]
    public void ChangedPhysicalPositionIsRestoredInsteadOfReusingStalePlacement()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();
                window.PositionOnWorkingArea();
                var expected = DisplayService.GetWindowPixelBounds(handle)!.Value;
                DisplayService.MoveWindowToPixelRect(handle, expected with { X = expected.X + 12 });
                window.PositionOnWorkingArea();
                Assert.Equal(expected, DisplayService.GetWindowPixelBounds(handle));
            }
            finally { window.Close(); }
        });
    }
}
