using System.Numerics;
using System.Windows;

namespace GlassDesk.UI.Controls;

public readonly record struct GlassSurfaceSample(double SignedDistanceDip, double HeightDip, Vector3 Normal);
public enum GlassFieldShape { RoundedRectangle, Squircle, Continuous }

/// <summary>Flat-centered height and finite-difference normal from the unfiltered shape SDF.</summary>
public static class GlassHeightField
{
    public static GlassSurfaceSample Sample(Rect boundsDip, double radiusDip, bool squircle,
        Point pointDip, double normalTransitionDip, double glassThicknessDip) =>
        SampleCore(boundsDip, radiusDip, squircle ? GlassFieldShape.Squircle : GlassFieldShape.RoundedRectangle,
            pointDip, normalTransitionDip, glassThicknessDip, 1);

    /// <summary>Maps physical capture coordinates to panel DIPs before sampling the shape.</summary>
    public static GlassSurfaceSample SamplePhysical(Rect boundsDip, double radiusDip, bool squircle,
        Point physicalPixel, Size physicalPixelSize, double normalTransitionDip, double glassThicknessDip)
        => SamplePhysical(boundsDip, radiusDip,
            squircle ? GlassFieldShape.Squircle : GlassFieldShape.RoundedRectangle,
            physicalPixel, physicalPixelSize, normalTransitionDip, glassThicknessDip);

    internal static GlassSurfaceSample SamplePhysical(Rect boundsDip, double radiusDip, GlassFieldShape shape,
        Point physicalPixel, Size physicalPixelSize, double normalTransitionDip, double glassThicknessDip)
        => SamplePhysical(boundsDip, radiusDip, shape, physicalPixel, physicalPixelSize,
            new Rect(boundsDip.X, boundsDip.Y, boundsDip.Width, boundsDip.Height),
            normalTransitionDip, glassThicknessDip);

    /// <summary>Samples a glass shape embedded in a larger captured desktop rectangle.</summary>
    internal static GlassSurfaceSample SamplePhysical(Rect boundsDip, double radiusDip, GlassFieldShape shape,
        Point physicalPixel, Size physicalPixelSize, Rect sampleBoundsDip,
        double normalTransitionDip, double glassThicknessDip)
    {
        if (!double.IsFinite(physicalPixelSize.Width) || !double.IsFinite(physicalPixelSize.Height)
            || physicalPixelSize.Width <= 0 || physicalPixelSize.Height <= 0
            || sampleBoundsDip.IsEmpty || !double.IsFinite(sampleBoundsDip.Width)
            || !double.IsFinite(sampleBoundsDip.Height) || sampleBoundsDip.Width <= 0 || sampleBoundsDip.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalPixelSize));
        var pointDip = new Point(sampleBoundsDip.X + physicalPixel.X * sampleBoundsDip.Width / physicalPixelSize.Width,
            sampleBoundsDip.Y + physicalPixel.Y * sampleBoundsDip.Height / physicalPixelSize.Height);
        // Height uses the Y physical scale. Convert X slope by the ratio of the
        // two pixel densities; ordinary Windows DPI has equal X and Y scales.
        var xSlopeScale = (physicalPixelSize.Height / sampleBoundsDip.Height)
            / (physicalPixelSize.Width / sampleBoundsDip.Width);
        return SampleCore(boundsDip, radiusDip, shape, pointDip, normalTransitionDip,
            glassThicknessDip, xSlopeScale);
    }

    private static GlassSurfaceSample SampleCore(Rect bounds, double radius, GlassFieldShape shape, Point point,
        double transition, double thickness, double xSlopeScale)
    {
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(radius)
            || !double.IsFinite(transition) || transition <= 0 || !double.IsFinite(thickness) || thickness < 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        var sdf = Distance(point);
        var height = Height(point);
        // The spec's normalized step is based on panel height. The X offset is
        // step / aspect in UV, so both finite differences span equal DIP distances.
        var step = Math.Clamp(transition / bounds.Height * .13, .003, .012) * bounds.Height;
        var gx = (Height(new Point(point.X + step, point.Y))
            - Height(new Point(point.X - step, point.Y))) / (2 * step);
        var gy = (Height(new Point(point.X, point.Y + step))
            - Height(new Point(point.X, point.Y - step))) / (2 * step);
        var normal = Vector3.Normalize(new Vector3((float)(-gx * xSlopeScale), (float)-gy, 1));
        return new GlassSurfaceSample(sdf, height, normal);

        double Distance(Point at) => shape switch
        {
            GlassFieldShape.Squircle => GlassSignedDistance.Squircle(bounds, radius, at),
            GlassFieldShape.Continuous => GlassSignedDistance.Continuous(bounds, radius, at),
            _ => GlassSignedDistance.RoundedRectangle(bounds, radius, at)
        };

        double Height(Point at) => thickness
            * HeightFractionFromInsideDistance(Math.Max(-Distance(at), 0), transition);
    }

    /// <summary>Normalized existing bevel-height profile shared with the SDF weight field.</summary>
    internal static double HeightFractionFromInsideDistance(double interiorDistanceDip, double transitionDip)
    {
        var t = Math.Clamp(Math.Max(0, interiorDistanceDip) / (transitionDip * .82), 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
}
