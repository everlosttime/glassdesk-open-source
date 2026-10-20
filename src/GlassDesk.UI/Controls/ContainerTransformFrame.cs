using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

/// <summary>One synchronized frame of a shared-element window transition.</summary>
public readonly record struct ContainerTransformFrame(
    PixelRect Bounds,
    double CornerRadius,
    double Opacity,
    double GlassStrength,
    double BlurRadius,
    double ContentOpacity,
    double ContentScale)
{
    public const double DurationMilliseconds = 420;
    private const double ContentRevealStart = .04;

    public static ContainerTransformFrame Create(
        PixelRect source,
        PixelRect destination,
        double progress,
        double sourceRadius,
        double destinationRadius)
    {
        progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        var eased = EaseOutCubic(progress);
        var content = ContentForRow(progress, 0, 1);

        var bounds = progress switch
        {
            <= 0 => source,
            >= 1 => destination,
            _ => new PixelRect(
                Lerp(source.X, destination.X, eased),
                Lerp(source.Y, destination.Y, eased),
                Math.Max(1, Lerp(source.Width, destination.Width, eased)),
                Math.Max(1, Lerp(source.Height, destination.Height, eased))),
        };

        var radiusFrom = double.IsFinite(sourceRadius) ? Math.Max(0, sourceRadius) : 0;
        var radiusTo = double.IsFinite(destinationRadius) ? Math.Max(0, destinationRadius) : 0;
        var radius = progress <= 0 ? radiusFrom : progress >= 1 ? radiusTo : radiusFrom + (radiusTo - radiusFrom) * eased;

        return new ContainerTransformFrame(bounds, radius, 1, eased, .8 * (1 - eased), content.Opacity,
            content.Scale);
    }

    /// <summary>Deterministic row waves: the same samples play backward on close/interruption.</summary>
    public static (double Opacity, double Scale) ContentForRow(double timeline, int row, int rows)
    {
        timeline = double.IsFinite(timeline) ? Math.Clamp(timeline, 0, 1) : 0;
        rows = Math.Clamp(rows, 1, 10);
        row = Math.Clamp(row, 0, rows - 1);
        var wave = row * Math.Min(rows, 5) / rows;
        var elapsed = (timeline - ContentRevealStart) * DurationMilliseconds - wave * 20;
        var t = Math.Clamp(elapsed / 100, 0, 1);
        var reveal = t * t * (3 - 2 * t);
        return (reveal, .92 + .08 * reveal);
    }

    private static double EaseOutCubic(double progress) => 1 - Math.Pow(1 - progress, 3);

    private static int Lerp(int from, int to, double progress) =>
        (int)Math.Round(from + (to - (double)from) * progress, MidpointRounding.AwayFromZero);
}
