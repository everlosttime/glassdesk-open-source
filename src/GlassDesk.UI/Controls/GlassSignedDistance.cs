using System.Windows;

namespace GlassDesk.UI.Controls;

/// <summary>Low-noise signed distances in panel DIPs: negative inside, positive outside.</summary>
public static class GlassSignedDistance
{
    public static double Circle(Point point, Point center, double radius) =>
        (point - center).Length - radius;

    public static double RoundedRectangle(Rect bounds, double radius, Point point)
    {
        var r = Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2);
        var qx = Math.Abs(point.X - (bounds.Left + bounds.Right) / 2) - bounds.Width / 2 + r;
        var qy = Math.Abs(point.Y - (bounds.Top + bounds.Bottom) / 2) - bounds.Height / 2 + r;
        return Math.Min(Math.Max(qx, qy), 0) + Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0)
            + Math.Max(qy, 0) * Math.Max(qy, 0)) - r;
    }

    /// <summary>Distance to the same superellipse and straight segments used by the visible outline.</summary>
    public static double Squircle(Rect bounds, double radius, Point point)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        var r = double.IsFinite(radius) ? Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2) : 0;
        if (r == 0) return RoundedRectangle(bounds, 0, point);

        // Mirror into the nearest corner. The straight sides have an exact distance.
        var x = Math.Min(point.X - bounds.Left, bounds.Right - point.X);
        var y = Math.Min(point.Y - bounds.Top, bounds.Bottom - point.Y);
        if (x >= r || y >= r) return -Math.Min(x, y);

        // Minimize distance to the quarter superellipse. This avoids the singular
        // gradient and center noise of dividing its implicit equation by |grad|.
        var lo = 0.0;
        var hi = 1.0;
        const double phi = .6180339887498948482;
        var a = hi - (hi - lo) * phi;
        var b = lo + (hi - lo) * phi;
        var da = DistanceSquared(a);
        var db = DistanceSquared(b);
        for (var i = 0; i < 28; i++)
        {
            if (da < db)
            {
                hi = b; b = a; db = da;
                a = hi - (hi - lo) * phi; da = DistanceSquared(a);
            }
            else
            {
                lo = a; a = b; da = db;
                b = lo + (hi - lo) * phi; db = DistanceSquared(b);
            }
        }
        var distance = Math.Sqrt(Math.Min(Math.Min(da, db),
            Math.Min(DistanceSquared(0), DistanceSquared(1))));
        return SquircleGlassGeometry.Contains(bounds, r, point) ? -distance : distance;

        double DistanceSquared(double t)
        {
            var cx = r * (1 - Math.Pow(t, 1 / SquircleGlassGeometry.Exponent));
            var cy = r * (1 - Math.Pow(1 - t, 1 / SquircleGlassGeometry.Exponent));
            var dx = cx - x; var dy = cy - y;
            return dx * dx + dy * dy;
        }
    }

    /// <summary>Distance to the older continuous cubic outline used by non-Squircle panels.</summary>
    public static double Continuous(Rect bounds, double radius, Point point)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        var r = double.IsFinite(radius) ? Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2) : 0;
        if (r == 0) return RoundedRectangle(bounds, 0, point);
        var x = Math.Min(point.X - bounds.Left, bounds.Right - point.X);
        var y = Math.Min(point.Y - bounds.Top, bounds.Bottom - point.Y);
        if (x >= r || y >= r) return -Math.Min(x, y);

        var lo = 0.0; var hi = 1.0;
        const double phi = .6180339887498948482;
        var a = hi - phi; var b = phi;
        var da = DistanceSquared(a); var db = DistanceSquared(b);
        for (var i = 0; i < 28; i++)
        {
            if (da < db)
            {
                hi = b; b = a; db = da;
                a = hi - (hi - lo) * phi; da = DistanceSquared(a);
            }
            else
            {
                lo = a; a = b; da = db;
                b = lo + (hi - lo) * phi; db = DistanceSquared(b);
            }
        }
        var distance = Math.Sqrt(Math.Min(Math.Min(da, db),
            Math.Min(DistanceSquared(0), DistanceSquared(1))));
        return ContinuousGlassGeometry.Contains(bounds, r, point) ? -distance : distance;

        double DistanceSquared(double t)
        {
            var oneMinusT = 1 - t;
            var cx = r * oneMinusT * oneMinusT * oneMinusT;
            var cy = r * t * t * t;
            var dx = cx - x; var dy = cy - y;
            return dx * dx + dy * dy;
        }
    }

    /// <summary>Polynomial smooth minimum for blending signed shape fields.</summary>
    public static double SmoothUnion(double a, double b, double radius)
    {
        if (radius <= 0) return Math.Min(a, b);
        var h = Math.Clamp(.5 + .5 * (b - a) / radius, 0, 1);
        return b + (a - b) * h - radius * h * (1 - h);
    }

}
