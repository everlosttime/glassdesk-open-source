using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Dock;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassGeometryContractTests
{
    [Fact]
    public void ProductionDockClipHitTestOpticalAlphaBorderAndShadowShareTheSquircleBoundary()
    {
        StaTest.Run(() =>
        {
            const int width = 200;
            const int height = 100;
            const double radius = 40;
            var bounds = new Rect(0, 0, width, height);
            var size = new Size(width, height);
            var dock = new DockWindow
            {
                Width = width,
                Height = height,
                SizeToContent = SizeToContent.Manual,
            };
            try
            {
                dock.Measure(size);
                dock.Arrange(new Rect(size));
                dock.UpdateLayout();
                typeof(GlassWindow).GetMethod("SetSharedPanelClip", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dock, [bounds, radius]);

                var expected = dock.CreateGlassPanelGeometry(bounds, radius);
                var clip = dock.Clip;
                Assert.NotNull(clip);
                var hitTest = typeof(GlassWindow).GetMethod("ContainsPanelGeometryCore",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;

                for (var y = 0; y < height; y += 2)
                for (var x = 0; x < width; x += 2)
                {
                    var point = new Point(x + .5, y + .5);
                    var expectedInside = expected.FillContains(point, .001, ToleranceType.Absolute);
                    Assert.Equal(expectedInside, clip!.FillContains(point, .001, ToleranceType.Absolute));
                    Assert.Equal(expectedInside,
                        (bool)hitTest.Invoke(dock, [bounds, radius, point])!);
                }

                var source = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
                var optical = new LiquidGlassOptics().Render(source, width, height, size, radius, squircle: true);
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var point = new Point(x + .5, y + .5);
                    Assert.Equal(expected.FillContains(point, .001, ToleranceType.Absolute),
                        optical[(y * width + x) * 4 + 3] != 0);
                }

                AssertRenderedSurfaceMask(dock, bounds, radius);
                AssertShadowExcludesTheGlassInterior(dock, new Rect(20, 20, 160, 60), 26, size);
                Assert.Same(dock, Window.GetWindow((FrameworkElement)dock.FindName("DockContentLayer")));
                Assert.Equal(expected.FillContains(new Point(100, 50), .001, ToleranceType.Absolute),
                    clip!.FillContains(new Point(100, 50), .001, ToleranceType.Absolute));
            }
            finally
            {
                dock.Close();
            }
        });
    }

    private static void AssertRenderedSurfaceMask(DockWindow dock, Rect bounds, double radius)
    {
        var surface = (LiquidGlassSurface)dock.FindName("GlassSurface");
        surface.Measure(bounds.Size);
        surface.Arrange(bounds);
        var bitmap = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[(int)bounds.Width * (int)bounds.Height * 4];
        bitmap.CopyPixels(pixels, (int)bounds.Width * 4, 0);
        Assert.Equal((byte)0, pixels[(5 * (int)bounds.Width + 5) * 4 + 3]);
                Assert.True(pixels[(50 * (int)bounds.Width + 100) * 4 + 3] > 0,
                    "The production GlassSurface should render inside the shared panel boundary.");
        Assert.True(SquircleGlassGeometry.Contains(bounds, radius, new Point(100, 50)));
    }

    private static void AssertShadowExcludesTheGlassInterior(GlassWindow owner, Rect panel,
        double radius, Size size)
    {
        var drawingType = typeof(GlassShadow).GetNestedType("ShadowDrawing", BindingFlags.NonPublic)!;
        var drawing = (FrameworkElement)Activator.CreateInstance(drawingType, nonPublic: true)!;
        drawingType.GetMethod("SetGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(drawing, [owner, panel, radius, size, 1d]);
        drawingType.GetMethod("SetAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(drawing, [LiquidGlassDebugView.None, LiquidGlassFinishingParameters.Default]);
        drawing.Measure(size);
        drawing.Arrange(new Rect(size));

        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[(int)size.Width * (int)size.Height * 4];
        bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
        Assert.Equal((byte)0, pixels[(50 * (int)size.Width + 100) * 4 + 3]);
        Assert.True(pixels[(50 * (int)size.Width + 16) * 4 + 3] > 0,
            "The external shadow should remain visible just outside the shared panel boundary.");
    }
}
