namespace GlassDesk.Dock;

/// <summary>Identical scale response for application tiles and the fixed Dock entries.</summary>
internal static class DockHoverScale
{
    internal static double Advance(double current, double target, double deltaSeconds)
    {
        var next = deltaSeconds <= 0 ? current
            : current + (target - current) * (1 - Math.Exp(-deltaSeconds / .075));
        return Math.Abs(target - next) <= .001 ? target : next;
    }
}
