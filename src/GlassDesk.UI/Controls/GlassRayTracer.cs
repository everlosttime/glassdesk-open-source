using System.Numerics;
using System.Windows;

namespace GlassDesk.UI.Controls;

/// <summary>Air-to-glass front interface, flat glass-to-air rear interface, then background plane.</summary>
public static class GlassRayTracer
{
    public static bool TryTrace(Point frontPoint, Vector3 frontNormal, double height,
        double backgroundDistance, double indexOfRefraction, out Point backgroundPoint)
    {
        backgroundPoint = frontPoint;
        if (!double.IsFinite(frontPoint.X) || !double.IsFinite(frontPoint.Y)
            || !double.IsFinite(height) || height < 0
            || !double.IsFinite(backgroundDistance) || backgroundDistance < 0
            || !double.IsFinite(indexOfRefraction) || indexOfRefraction < 1)
            return false;

        if (!TryRefract(-Vector3.UnitZ, frontNormal, 1 / indexOfRefraction, out var inGlass)
            || inGlass.Z >= -1e-5f)
            return false;
        var rearTravel = -height / inGlass.Z;
        var rearX = frontPoint.X + inGlass.X * rearTravel;
        var rearY = frontPoint.Y + inGlass.Y * rearTravel;

        if (!TryRefract(inGlass, -Vector3.UnitZ, indexOfRefraction, out var inAir)
            || inAir.Z >= -1e-5f)
            return false;
        var backgroundTravel = -backgroundDistance / inAir.Z;
        var sampleX = rearX + inAir.X * backgroundTravel;
        var sampleY = rearY + inAir.Y * backgroundTravel;
        if (!double.IsFinite(sampleX) || !double.IsFinite(sampleY)) return false;
        backgroundPoint = new Point(sampleX, sampleY);
        return true;
    }

    /// <summary>Snell refraction with eta = incident IOR / transmitted IOR.</summary>
    internal static bool TryRefract(Vector3 incoming, Vector3 normal, double eta, out Vector3 transmitted)
    {
        transmitted = default;
        if (!float.IsFinite(incoming.X) || !float.IsFinite(incoming.Y) || !float.IsFinite(incoming.Z)
            || !float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z)
            || incoming.LengthSquared() < 1e-10f || normal.LengthSquared() < 1e-10f
            || !double.IsFinite(eta) || eta <= 0)
            return false;

        var incident = Vector3.Normalize(incoming);
        if (eta == 1) { transmitted = incident; return true; }
        var facing = Vector3.Normalize(normal);
        var cosine = Vector3.Dot(incident, facing);
        if (cosine > 0) { facing = -facing; cosine = -cosine; }
        var k = 1 - eta * eta * (1 - cosine * cosine);
        if (k < 0) return false; // Total internal reflection: caller keeps the original sample.
        var ray = (float)eta * incident - (float)(eta * cosine + Math.Sqrt(k)) * facing;
        if (ray.LengthSquared() < 1e-10f) return false;
        transmitted = Vector3.Normalize(ray);
        return float.IsFinite(transmitted.X) && float.IsFinite(transmitted.Y) && float.IsFinite(transmitted.Z);
    }
}
