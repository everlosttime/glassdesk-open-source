using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassFieldTests
{
    [Fact]
    public void SignedShapesAndSmoothUnionUseNegativeInside()
    {
        var bounds = new Rect(0, 0, 200, 100);
        Assert.Equal(-20, GlassSignedDistance.Circle(new Point(40, 40), new Point(40, 40), 20), 6);
        Assert.Equal(10, GlassSignedDistance.Circle(new Point(70, 40), new Point(40, 40), 20), 6);
        Assert.Equal(-50, GlassSignedDistance.RoundedRectangle(bounds, 20, new Point(100, 50)), 6);
        Assert.Equal(10, GlassSignedDistance.RoundedRectangle(bounds, 20, new Point(100, -10)), 6);
        Assert.Equal(-2.5, GlassSignedDistance.SmoothUnion(0, 0, 10), 6);
    }

    [Theory]
    [InlineData(200, 100, 40)]
    [InlineData(100, 200, 40)]
    [InlineData(320, 90, 35)]
    public void SquircleSdfHasTheSameBoundaryAsVisibleAndHitTestGeometry(double width, double height, double radius)
    {
        var bounds = new Rect(7, 11, width, height);
        var outline = SquircleGlassGeometry.Create(bounds, radius);
        for (var y = -3; y <= height + 3; y += 3)
        for (var x = -3; x <= width + 3; x += 3)
        {
            var point = new Point(bounds.X + x + .37, bounds.Y + y + .23);
            var sdf = GlassSignedDistance.Squircle(bounds, radius, point);
            if (Math.Abs(sdf) < .12) continue; // Polygon flattening is subpixel near the edge.
            Assert.Equal(SquircleGlassGeometry.Contains(bounds, radius, point), sdf < 0);
            Assert.Equal(outline.FillContains(point, .001, System.Windows.Media.ToleranceType.Absolute), sdf < 0);
        }
    }

    [Fact]
    public void HeightHasAFlatCenterAndNormalPointsOutwardAtTopBevel()
    {
        var bounds = new Rect(0, 0, 240, 120);
        var center = GlassHeightField.Sample(bounds, 32, true, new Point(120, 60), 12, 4);
        var top = GlassHeightField.Sample(bounds, 32, true, new Point(120, 3), 12, 4);
        Assert.Equal(4, center.HeightDip, 8);
        Assert.Equal(0, center.Normal.X);
        Assert.Equal(0, center.Normal.Y);
        Assert.Equal(1, center.Normal.Z);
        Assert.InRange(top.HeightDip, 0, 4);
        Assert.True(top.Normal.Y < 0);
        Assert.InRange(top.Normal.Length(), .9999f, 1.0001f);
    }

    [Fact]
    public void PhysicalPixelMappingPreservesShapeAcrossAspectAndDpi()
    {
        var bounds = new Rect(10, 20, 240, 120);
        var reference = GlassHeightField.SamplePhysical(bounds, 32, true,
            new Point(120, 3), new Size(240, 120), 12, 4);
        foreach (var scale in new[] { 1.25, 1.5, 2.0 })
        {
            var actual = GlassHeightField.SamplePhysical(bounds, 32, true,
                new Point(120 * scale, 3 * scale), new Size(240 * scale, 120 * scale), 12, 4);
            Assert.Equal(reference.SignedDistanceDip, actual.SignedDistanceDip, 6);
            Assert.Equal(reference.HeightDip, actual.HeightDip, 6);
            Assert.InRange((reference.Normal - actual.Normal).Length(), 0, 1e-5f);
        }
    }

    [Fact]
    public void HorizontalNormalUsesPhysicalPixelAspectRatherThanStretchedUv()
    {
        var bounds = new Rect(0, 0, 240, 120);
        var uniform = GlassHeightField.SamplePhysical(bounds, 32, true,
            new Point(3, 60), new Size(240, 120), 12, 4);
        var twiceAsDenseInX = GlassHeightField.SamplePhysical(bounds, 32, true,
            new Point(6, 60), new Size(480, 120), 12, 4);

        Assert.Equal(uniform.HeightDip, twiceAsDenseInX.HeightDip, 6);
        Assert.True(uniform.Normal.X < 0);
        Assert.InRange(Math.Abs(twiceAsDenseInX.Normal.X / uniform.Normal.X), .49, .56);
        Assert.InRange(Math.Abs(uniform.Normal.Y), 0, 1e-5f);
    }

    [Fact]
    public void LegacyContinuousFieldAgreesWithItsVisibleContour()
    {
        var bounds = new Rect(0, 0, 200, 100);
        foreach (var point in new[] { new Point(2, 2), new Point(8, 10),
                     new Point(30, 3), new Point(100, 3), new Point(100, 50) })
        {
            var sdf = GlassSignedDistance.Continuous(bounds, 30, point);
            Assert.Equal(ContinuousGlassGeometry.Contains(bounds, 30, point), sdf < 0);
        }
        var top = GlassHeightField.SamplePhysical(bounds, 30, GlassFieldShape.Continuous,
            new Point(100, 3), new Size(200, 100), 12, 4);
        Assert.True(top.Normal.Y < 0);
    }
}
