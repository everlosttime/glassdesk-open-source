using System.Numerics;
using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassRayTracerTests
{
    [Fact]
    public void FlatCenterPassesStraightThroughBothInterfaces()
    {
        var field = GlassHeightField.Sample(new Rect(0, 0, 200, 100), 30, true,
            new Point(100, 50), 12, 4);

        Assert.True(GlassRayTracer.TryTrace(new Point(100, 50), field.Normal,
            field.HeightDip, 8, 1.46, out var sample));
        Assert.Equal(100, sample.X, 8);
        Assert.Equal(50, sample.Y, 8);
    }

    [Fact]
    public void TopAndBottomBevelsBendTowardTheirRespectiveInteriors()
    {
        var bounds = new Rect(0, 0, 200, 100);
        var top = GlassHeightField.Sample(bounds, 30, true, new Point(100, 3), 12, 4);
        var bottom = GlassHeightField.Sample(bounds, 30, true, new Point(100, 97), 12, 4);

        Assert.True(GlassRayTracer.TryTrace(new Point(100, 3), top.Normal, top.HeightDip,
            8, 1.46, out var topSample));
        Assert.True(GlassRayTracer.TryTrace(new Point(100, 97), bottom.Normal, bottom.HeightDip,
            8, 1.46, out var bottomSample));
        Assert.True(topSample.Y > 3);
        Assert.True(bottomSample.Y < 97);
        Assert.Equal(topSample.Y - 3, 97 - bottomSample.Y, 5);
    }

    [Fact]
    public void IndexOneIsIdentityAndFartherBackgroundIncreasesDisplacement()
    {
        var field = GlassHeightField.Sample(new Rect(0, 0, 200, 100), 30, true,
            new Point(100, 3), 12, 4);

        Assert.True(GlassRayTracer.TryTrace(new Point(100, 3), field.Normal,
            field.HeightDip, 8, 1, out var identity));
        Assert.Equal(3, identity.Y, 8);
        Assert.True(GlassRayTracer.TryTrace(new Point(100, 3), field.Normal,
            field.HeightDip, 4, 1.46, out var near));
        Assert.True(GlassRayTracer.TryTrace(new Point(100, 3), field.Normal,
            field.HeightDip, 12, 1.46, out var far));
        Assert.True(far.Y > near.Y);
    }

    [Fact]
    public void TiltedFrontSurfaceMatchesSnellsLawAtBothInterfaces()
    {
        const double frontTilt = Math.PI / 6;
        var normal = new Vector3(0, (float)-Math.Sin(frontTilt), (float)Math.Cos(frontTilt));
        Assert.True(GlassRayTracer.TryTrace(new Point(20, 20), normal, 4, 8, 1.46,
            out var sample));

        var internalTilt = frontTilt - Math.Asin(Math.Sin(frontTilt) / 1.46);
        var exitTilt = Math.Asin(1.46 * Math.Sin(internalTilt));
        var expected = 4 * Math.Tan(internalTilt) + 8 * Math.Tan(exitTilt);
        Assert.InRange(Math.Abs(sample.Y - 20 - expected), 0, .001);
        Assert.Equal(20, sample.X, 6);
    }

    [Fact]
    public void TotalInternalReflectionAndInvalidDirectionsFailClosed()
    {
        var incident = Vector3.Normalize(new Vector3(.9f, 0, -.43589f));
        Assert.False(GlassRayTracer.TryRefract(incident, -Vector3.UnitZ,
            1.46, out _));
        Assert.False(GlassRayTracer.TryTrace(new Point(4, 5), Vector3.Zero,
            4, 8, 1.46, out var fallback));
        Assert.Equal(new Point(4, 5), fallback);
    }
}
