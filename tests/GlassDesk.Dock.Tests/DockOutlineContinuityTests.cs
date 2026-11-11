using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class DockOutlineContinuityTests
{
    [Fact]
    public void DockShouldersHaveCurvatureContinuousSplinesRatherThanPolygonJoints()
    {
        StaTest.Run(() =>
        {
            var dock = new DockWindow();
            try
            {
                var outline = dock.CreateGlassPanelGeometry(new Rect(0, 0, 460, 96), 40);
                var figure = Assert.Single(PathGeometry.CreateFromGeometry(outline).Figures);
                var curves = new List<(Point Start, Point C1, Point C2, Point End)>();
                var point = figure.StartPoint;
                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line: point = line.Point; break;
                        case PolyLineSegment lines: point = lines.Points[^1]; break;
                        case BezierSegment curve:
                            curves.Add((point, curve.Point1, curve.Point2, curve.Point3)); point = curve.Point3; break;
                        case PolyBezierSegment chain:
                            for (var i = 0; i < chain.Points.Count; i += 3)
                            { curves.Add((point, chain.Points[i], chain.Points[i + 1], chain.Points[i + 2])); point = chain.Points[i + 2]; }
                            break;
                    }
                }
                Assert.InRange(curves.Count, 4, 32);
                for (var i = 1; i < curves.Count; i++)
                {
                    var before = curves[i - 1]; var after = curves[i];
                    if ((before.End - after.Start).Length > 1e-6) continue;
                    var incoming = before.End - before.C2; var outgoing = after.C1 - after.Start;
                    Assert.True(Vector.Multiply(incoming, outgoing) > 0);
                    Assert.InRange(Math.Abs(Vector.CrossProduct(incoming, outgoing)) / (incoming.Length * outgoing.Length), 0, 1e-8);
                    Assert.InRange(Math.Abs(Curvature(before, 1) - Curvature(after, 0)), 0, 1e-7);
                }
                Assert.InRange(Math.Abs(Curvature(curves[0], 0)), 0, 1e-8);
            }
            finally { dock.Close(); }
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ActualDockOutlineKeepsItsFootprintAndSoftPhysicalPixelCoverage(double scale)
    {
        StaTest.Run(() =>
        {
            var dock = new DockWindow();
            try
            {
                var bounds = new Rect(2.25, 2.25, 455.5, 91.5);
                var outline = dock.CreateGlassPanelGeometry(bounds, 40);
                Assert.Equal(bounds, outline.Bounds); Assert.True(outline.IsFrozen);
                // The existing lens and input geometry keep the same analytic outline.
                for (var y = 3d; y < 94; y += 3.7)
                for (var x = 3d; x < 458; x += 6.3)
                {
                    var p = new Point(x, y);
                    if (Math.Abs(GlassSignedDistance.Squircle(bounds, 40, p)) > .15)
                        Assert.Equal(SquircleGlassGeometry.Contains(bounds, 40, p), outline.FillContains(p, .001, ToleranceType.Absolute));
                }
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawGeometry(Brushes.White, null, outline);
                var width = (int)(460 * scale); var height = (int)(96 * scale);
                var bitmap = new RenderTargetBitmap(width, height, scale * 96, scale * 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                Assert.Equal(0, pixels[3]); Assert.Equal(255, pixels[(height / 2 * width + width / 2) * 4 + 3]);
                Assert.True(Enumerable.Range(0, width * height).Count(i => pixels[i * 4 + 3] is > 0 and < 255) > 100);
                var directory = Environment.GetEnvironmentVariable("GLASSDESK_DOCK_OUTLINE_PREVIEW");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, $"dock-outline-{scale}.png")); encoder.Save(output);
                }
            }
            finally { dock.Close(); }
        });
    }
    private static double Curvature((Point Start, Point C1, Point C2, Point End) curve, double t)
    {
        var v = t == 0 ? curve.C1 - curve.Start : curve.End - curve.C2;
        var a = t == 0 ? curve.C2 - curve.C1 - (curve.C1 - curve.Start) : curve.End - curve.C2 - (curve.C2 - curve.C1);
        return 2d / 3 * Vector.CrossProduct(v, a) / Math.Pow(v.Length, 3);
    }
}
