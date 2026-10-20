using System.Reflection;
using System.Windows;
using System.Windows.Media;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Dock;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class PhoneCornerOutlineTests
{
    [Fact]
    public void OpticalAlphaAndSharedGeometryAgreeAcrossAnAnimatedSize()
    {
        const int width = 180, height = 100;
        var source = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var bounds = new Rect(0, 0, width, height);
        var result = new LiquidGlassOptics().Render(source, width, height, bounds.Size, 32, squircle: true);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Assert.Equal(SquircleGlassGeometry.Contains(bounds, 32, new Point(x + .5, y + .5)),
                result[(y * width + x) * 4 + 3] != 0);
    }

    [Fact]
    public void LauncherAnchorIsTheActualGlassTileRatherThanTheWholeButtonSlot()
    {
        StaTest.Run(() =>
        {
            var dock = new DockWindow();
            try
            {
                dock.Show(); dock.UpdateLayout();
                var tile = (PhoneBorder)dock.FindName("LaunchpadTile");
                var top = tile.PointToScreen(new Point());
                var bottom = tile.PointToScreen(new Point(tile.ActualWidth, tile.ActualHeight));
                var actual = dock.GetLauncherButtonPixelBounds();
                Assert.NotNull(actual);
                Assert.InRange(Math.Abs(actual.Value.X - top.X), 0, 1);
                Assert.InRange(Math.Abs(actual.Value.Y - top.Y), 0, 1);
                Assert.InRange(Math.Abs(actual.Value.Height - (bottom.Y - top.Y)), 0, 1);
                Assert.Equal(actual.Value.Width, actual.Value.Height);
            }
            finally { dock.Close(); }
        });
    }
    [Fact]
    public void CornerHasTheBroadSmoothShoulderSeenInThePhoneReference()
    {
        var bounds = new Rect(0, 0, 200, 100);
        var outline = SquircleGlassGeometry.Create(bounds, 40);

        Assert.True(SquircleGlassGeometry.Contains(bounds, 40, new Point(30, 20)));
        Assert.True(outline.FillContains(new Point(30, 20), .001, ToleranceType.Absolute));
        Assert.False(SquircleGlassGeometry.Contains(bounds, 40, new Point(9, 9)));
        Assert.False(outline.FillContains(new Point(9, 9), .001, ToleranceType.Absolute));
    }

    [Fact]
    public void VisibleContourAndPointerBoundaryAgreeAtAllFourCorners()
    {
        var bounds = new Rect(10, 20, 180, 100);
        var outline = SquircleGlassGeometry.Create(bounds, 40);
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 40; x++)
        {
            var u = 1 - (x + .37) / 40;
            var v = 1 - (y + .23) / 40;
            const double referenceExponent = 2.6;
            var gradient = referenceExponent / 40 * Math.Sqrt(
                Math.Pow(u, 2 * (referenceExponent - 1)) + Math.Pow(v, 2 * (referenceExponent - 1)));
            var distanceToEdge = Math.Abs(1 - Math.Pow(u, referenceExponent) -
                Math.Pow(v, referenceExponent)) / gradient;
            if (distanceToEdge < .1) continue; // The drawn polygon is allowed subpixel flattening.
            var cornerSamples = new[]
            {
                new Point(bounds.Left + x + .37, bounds.Top + y + .23),
                new Point(bounds.Right - x - .37, bounds.Top + y + .23),
                new Point(bounds.Left + x + .37, bounds.Bottom - y - .23),
                new Point(bounds.Right - x - .37, bounds.Bottom - y - .23),
            };
            foreach (var point in cornerSamples)
                Assert.True(SquircleGlassGeometry.Contains(bounds, 40, point) ==
                    outline.FillContains(point, .001, ToleranceType.Absolute),
                    $"Visible contour and hit test disagree at {point}.");
        }
    }

    [Fact]
    public void DockAndSettingsUseTheSameOuterContour()
    {
        StaTest.Run(() =>
        {
            var dock = new DockWindow();
            var settings = new SettingsWindow();
            try
            {
                var bounds = new Rect(0, 0, 200, 100);
                var sample = new Point(36, 27);
                Assert.Equal(dock.CreateGlassPanelGeometry(bounds, 40).FillContains(sample),
                    settings.CreateGlassPanelGeometry(bounds, 40).FillContains(sample));
                Assert.True(settings.UsesSquirclePanelGeometry);
            }
            finally
            {
                settings.Close();
                dock.Close();
            }
        });
    }

    [Fact]
    public void LargePanelsKeepAVisiblePhoneSizeShoulderWithSavedLegacySettings()
    {
        StaTest.Run(() =>
        {
            var settings = new SettingsWindow();
            try
            {
                var resolver = typeof(SettingsWindow).GetMethod("ResolveCornerRadius",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var radius = (double)resolver.Invoke(settings,
                    [new AppearanceSettings { LauncherCornerRadius = 36 }])!;
                Assert.Equal(80, radius);
            }
            finally
            {
                settings.Close();
            }
        });
    }

}
