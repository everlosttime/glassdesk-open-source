using System.Windows;
using System.Windows.Media;
using System.Collections.Concurrent;

namespace GlassDesk.UI.Controls;

/// <summary>A rounded rectangle whose corners follow the supplied phone outline's soft shoulder.</summary>
public static class SquircleGlassGeometry
{
    public const double Exponent = 2.6;
    private const int MinimumSegmentsPerCorner = 64;
    private const int MaximumSegmentsPerCorner = 256;
    // At most 193 small arrays. The normalized curve is identical for every radius,
    // corner, shadow layer and frame; only its scale and orientation change.
    private static readonly ConcurrentDictionary<int, Point[]> CornerSamples = new();
    private static readonly ConcurrentDictionary<int, CubicCorner[]> CompactCorners = new();

    /// <summary>Builds one frozen, asymmetric-rectangle-safe superellipse outline.</summary>
    public static Geometry Create(Rect bounds, double radius)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return Geometry.Empty;
        var r = Radius(bounds, radius);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (r == 0)
            {
                context.BeginFigure(new Point(bounds.Left, bounds.Top), true, true);
                context.LineTo(new Point(bounds.Right, bounds.Top), true, false);
                context.LineTo(new Point(bounds.Right, bounds.Bottom), true, false);
                context.LineTo(new Point(bounds.Left, bounds.Bottom), true, false);
            }
            else
            {
                context.BeginFigure(new Point(bounds.Left + r, bounds.Top), true, true);
                context.LineTo(new Point(bounds.Right - r, bounds.Top), true, false);
                AddCorner(context, new Point(bounds.Right - r, bounds.Top + r), r, 0);
                context.LineTo(new Point(bounds.Right, bounds.Bottom - r), true, false);
                AddCorner(context, new Point(bounds.Right - r, bounds.Bottom - r), r, 1);
                context.LineTo(new Point(bounds.Left + r, bounds.Bottom), true, false);
                AddCorner(context, new Point(bounds.Left + r, bounds.Bottom - r), r, 2);
                context.LineTo(new Point(bounds.Left, bounds.Top + r), true, false);
                AddCorner(context, new Point(bounds.Left + r, bounds.Top + r), r, 3);
            }
        }
        return Freeze(geometry);

        static Geometry Freeze(StreamGeometry shape) { shape.Freeze(); return shape; }
    }

    /// <summary>
    /// The same exponent-2.6 outline with eight cached cubic segments per corner at
    /// radii up to 256 DIPs. Its measured boundary error stays below 0.1 DIP; larger
    /// radii refine the normalized spline before it is reused by every shadow layer.
    /// </summary>
    public static Geometry CreateCompact(Rect bounds, double radius)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return Geometry.Empty;
        var r = Radius(bounds, radius);
        if (r == 0) return Create(bounds, 0);
        var subdivisions = 1;
        while (r > 256d * subdivisions * subdivisions && subdivisions < 32) subdivisions *= 2;
        var curves = CompactCorners.GetOrAdd(subdivisions, BuildCompactCorner);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(bounds.Left + r, bounds.Top), true, true);
            context.LineTo(new Point(bounds.Right - r, bounds.Top), true, false);
            AddCompactCorner(context, new Point(bounds.Right - r, bounds.Top + r), r, 0, curves);
            context.LineTo(new Point(bounds.Right, bounds.Bottom - r), true, false);
            AddCompactCorner(context, new Point(bounds.Right - r, bounds.Bottom - r), r, 1, curves);
            context.LineTo(new Point(bounds.Left + r, bounds.Bottom), true, false);
            AddCompactCorner(context, new Point(bounds.Left + r, bounds.Bottom - r), r, 2, curves);
            context.LineTo(new Point(bounds.Left, bounds.Top + r), true, false);
            AddCompactCorner(context, new Point(bounds.Left + r, bounds.Top + r), r, 3, curves);
        }
        geometry.Freeze();
        return geometry;
    }

    private readonly record struct CubicCorner(Point Start, Point Control1, Point Control2, Point End);

    private static void AddCompactCorner(StreamGeometryContext context, Point center, double radius,
        int quarterTurn, CubicCorner[] curves)
    {
        foreach (var curve in curves)
            context.BezierTo(Transform(Scale(curve.Control1), center, quarterTurn),
                Transform(Scale(curve.Control2), center, quarterTurn),
                Transform(Scale(curve.End), center, quarterTurn), true, false);
        Point Scale(Point point) => new(point.X * radius, point.Y * radius);
    }

    private static CubicCorner[] BuildCompactCorner(int subdivisions)
    {
        // Fit y(x) on the first half of the exact superellipse, then reflect it.
        // Clamped cubic splines preserve C2 at every knot. Adjusting only the first
        // ordinate also makes curvature zero at the straight-side join, without
        // changing its horizontal tangent or the exact diagonal endpoint/tangent.
        double[] knots = [0, .12, .45, .78, 1];
        var half = Math.Pow(.5, 1 / Exponent);
        var count = 4 * subdivisions;
        var x = new double[count + 1];
        var y = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            var interval = i / subdivisions;
            var fraction = (i % subdivisions) / (double)subdivisions;
            x[i] = half * (knots[interval] + fraction * (knots[interval + 1] - knots[interval]));
            y[i] = -Math.Pow(1 - Math.Pow(x[i], Exponent), 1 / Exponent);
        }
        x[count] = half; y[count] = -half;
        var second = SolveCornerSpline(x, y);
        var perturbed = (double[])y.Clone();
        perturbed[1] += 1;
        var response = SolveCornerSpline(x, perturbed);
        y[1] -= second[0] / (response[0] - second[0]);
        second = SolveCornerSpline(x, y);
        second[0] = 0;

        var curves = new CubicCorner[count * 2];
        for (var i = 0; i < count; i++)
        {
            var h = x[i + 1] - x[i];
            var slope = (y[i + 1] - y[i]) / h;
            var startSlope = slope - h * (2 * second[i] + second[i + 1]) / 6;
            var endSlope = slope + h * (second[i] + 2 * second[i + 1]) / 6;
            var control1 = new Point(x[i] + h / 3, y[i] + h * startSlope / 3);
            var control2 = new Point(x[i + 1] - h / 3, y[i + 1] - h * endSlope / 3);
            if (i == 0) { control1.Y = -1; control2.Y = -1; }
            if (i == count - 1) control2 = new Point(half - h / 3, -half - h / 3);
            curves[i] = new(new Point(x[i], y[i]), control1, control2, new Point(x[i + 1], y[i + 1]));
        }
        for (var i = 0; i < count; i++)
        {
            var curve = curves[count - 1 - i];
            curves[count + i] = new(Reflect(curve.End), Reflect(curve.Control2),
                Reflect(curve.Control1), Reflect(curve.Start));
        }
        return curves;

        static Point Reflect(Point point) => new(-point.Y, -point.X);
    }

    private static double[] SolveCornerSpline(double[] x, double[] y)
    {
        var count = x.Length - 1;
        var diagonal = new double[count + 1];
        var upper = new double[count + 1];
        var right = new double[count + 1];
        var h = x[1] - x[0];
        diagonal[0] = 2 * h; upper[0] = h; right[0] = 6 * (y[1] - y[0]) / h;
        for (var i = 1; i <= count; i++)
        {
            var previous = x[i] - x[i - 1];
            if (i == count)
            {
                diagonal[i] = 2 * previous;
                right[i] = 6 * (1 - (y[i] - y[i - 1]) / previous);
            }
            else
            {
                var next = x[i + 1] - x[i];
                diagonal[i] = 2 * (previous + next); upper[i] = next;
                right[i] = 6 * ((y[i + 1] - y[i]) / next - (y[i] - y[i - 1]) / previous);
            }
            var factor = previous / diagonal[i - 1];
            diagonal[i] -= factor * upper[i - 1]; right[i] -= factor * right[i - 1];
        }
        var second = new double[count + 1];
        second[count] = right[count] / diagonal[count];
        for (var i = count - 1; i >= 0; i--)
            second[i] = (right[i] - upper[i] * second[i + 1]) / diagonal[i];
        return second;
    }

    /// <summary>Exact inside test for the same superellipse profile.</summary>
    public static bool Contains(Rect bounds, double radius, Point point)
    {
        if (bounds.IsEmpty || !bounds.Contains(point)) return false;
        var r = Radius(bounds, radius);
        var x = Math.Min(point.X - bounds.Left, bounds.Right - point.X);
        var y = Math.Min(point.Y - bounds.Top, bounds.Bottom - point.Y);
        return r == 0 || x >= r || y >= r ||
            Math.Pow(1 - x / r, Exponent) + Math.Pow(1 - y / r, Exponent) <= 1 + 1e-12;
    }

    /// <summary>Approximate inward Euclidean distance and exact superellipse normal.</summary>
    public static bool TryGetEdge(Rect bounds, double radius, Point point, out double depth, out Vector inwardNormal)
    {
        depth = 0;
        inwardNormal = default;
        if (!Contains(bounds, radius, point)) return false;

        var left = point.X - bounds.Left;
        var right = bounds.Right - point.X;
        var top = point.Y - bounds.Top;
        var bottom = bounds.Bottom - point.Y;
        var x = Math.Min(left, right);
        var y = Math.Min(top, bottom);
        var sx = left <= right ? 1 : -1;
        var sy = top <= bottom ? 1 : -1;
        var r = Radius(bounds, radius);

        if (r <= 0 || x >= r || y >= r)
        {
            if (x < y) { depth = x; inwardNormal = new Vector(sx, 0); }
            else { depth = y; inwardNormal = new Vector(0, sy); }
            return true;
        }

        var u = 1 - x / r;
        var v = 1 - y / r;
        var nx = Math.Pow(u, Exponent);
        var ny = Math.Pow(v, Exponent);
        var gx = Exponent * Math.Pow(u, Exponent - 1) / r;
        var gy = Exponent * Math.Pow(v, Exponent - 1) / r;
        var gradientLength = Math.Sqrt(gx * gx + gy * gy);
        depth = gradientLength > 0 ? (1 - nx - ny) / gradientLength : 0;
        inwardNormal = new Vector(sx * gx, sy * gy);
        inwardNormal.Normalize();
        return true;
    }

    private static void AddCorner(StreamGeometryContext context, Point center, double radius, int quarterTurn)
    {
        var count = Math.Clamp((int)Math.Ceiling(radius * 3),
            MinimumSegmentsPerCorner, MaximumSegmentsPerCorner);
        var samples = CornerSamples.GetOrAdd(count, static segments =>
        {
            var points = new Point[segments];
            for (var segment = 1; segment <= segments; segment++)
            {
                var t = segment / (double)segments;
                var smooth = t * t * (3 - 2 * t);
                var theta = smooth * Math.PI / 2;
                points[segment - 1] = segment == segments ? new Point(1, 0)
                    : new Point(Math.Pow(Math.Sin(theta), 2 / Exponent),
                        -Math.Pow(Math.Cos(theta), 2 / Exponent));
            }
            return points;
        });
        foreach (var sample in samples)
        {
            var point = new Point(radius * sample.X, radius * sample.Y);
            context.LineTo(Transform(point, center, quarterTurn), true, false);
        }
    }

    private static Point Transform(Point point, Point center, int quarterTurn) =>
        center + TransformVector(new Vector(point.X, point.Y), quarterTurn);

    private static Vector TransformVector(Vector vector, int quarterTurn) => quarterTurn switch
    {
        0 => vector,
        1 => new Vector(-vector.Y, vector.X),
        2 => new Vector(-vector.X, -vector.Y),
        _ => new Vector(vector.Y, -vector.X),
    };

    private static double Radius(Rect bounds, double radius) =>
        double.IsFinite(radius) ? Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2) : 0;
}
