using System.Windows;
using System.Windows.Media;

namespace GlassDesk.UI.Controls;

public static class ContinuousGlassGeometry
{
    // Each corner has coincident control points at the outer vertex. The curvature
    // at either straight join is zero, unlike a circular rounded rectangle.
    public static Geometry Create(Rect bounds, double radius)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return Geometry.Empty;
        var r = Radius(bounds, radius);
        var x = bounds.Left; var y = bounds.Top; var right = bounds.Right; var bottom = bounds.Bottom;
        var shape = new StreamGeometry();
        using (var c = shape.Open())
        {
            c.BeginFigure(new Point(x + r, y), true, true);
            c.LineTo(new Point(right - r, y), true, false);
            c.BezierTo(new Point(right, y), new Point(right, y), new Point(right, y + r), true, false);
            c.LineTo(new Point(right, bottom - r), true, false);
            c.BezierTo(new Point(right, bottom), new Point(right, bottom), new Point(right - r, bottom), true, false);
            c.LineTo(new Point(x + r, bottom), true, false);
            c.BezierTo(new Point(x, bottom), new Point(x, bottom), new Point(x, bottom - r), true, false);
            c.LineTo(new Point(x, y + r), true, false);
            c.BezierTo(new Point(x, y), new Point(x, y), new Point(x + r, y), true, false);
        }
        shape.Freeze();
        return shape;
    }

    public static bool Contains(Rect bounds, double radius, Point point)
    {
        if (bounds.IsEmpty || !bounds.Contains(point)) return false;
        var r = Radius(bounds, radius);
        var x = Math.Min(point.X - bounds.Left, bounds.Right - point.X);
        var y = Math.Min(point.Y - bounds.Top, bounds.Bottom - point.Y);
        return r == 0 || x >= r || y >= r || Math.Cbrt(x / r) + Math.Cbrt(y / r) >= 1 - 1e-10;
    }
    internal static bool TryGetEdge(Rect bounds, double radius, Point point, out double depth, out Vector inwardNormal)
    {
        depth = 0; inwardNormal = default;
        if (!Contains(bounds, radius, point)) return false;
        var left = point.X - bounds.Left; var top = point.Y - bounds.Top;
        var right = bounds.Right - point.X; var bottom = bounds.Bottom - point.Y;
        var x = Math.Min(left, right); var y = Math.Min(top, bottom);
        var sx = left <= right ? 1 : -1; var sy = top <= bottom ? 1 : -1;
        var r = Radius(bounds, radius);
        if (x >= r || y >= r || r <= 0)
        {
            depth = Math.Min(x, y);
            inwardNormal = x < y ? new Vector(sx, 0) : new Vector(0, sy);
            return true;
        }
        // Nearest point on the monotone convex cubic; only computed when a map changes.
        double Distance(double t)
        {
            var cx = r * Math.Pow(1 - t, 3); var cy = r * t * t * t;
            return (cx - x) * (cx - x) + (cy - y) * (cy - y);
        }
        double lo = 0, hi = 1;
        for (var i = 0; i < 28; i++)
        {
            var a = (2 * lo + hi) / 3; var b = (lo + 2 * hi) / 3;
            if (Distance(a) < Distance(b)) hi = b; else lo = a;
        }
        var t = (lo + hi) / 2;
        depth = Math.Sqrt(Distance(t));
        inwardNormal = new Vector(t * t * sx, (1 - t) * (1 - t) * sy);
        inwardNormal.Normalize();
        return true;
    }

    private static double Radius(Rect bounds, double radius) =>
        double.IsFinite(radius) ? Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2) : 0;
}
