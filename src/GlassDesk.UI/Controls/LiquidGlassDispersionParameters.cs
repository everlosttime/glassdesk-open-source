namespace GlassDesk.UI.Controls;

/// <summary>Live-only IOR spread and region gains for Chapter 10 optical dispersion.</summary>
public readonly record struct LiquidGlassDispersionParameters(
    double DispersionStrength,
    double InnerDispersionGain,
    double OuterDispersionGain,
    double FaceDispersionGain)
{
    public const double MinimumDispersionStrength = 0;
    public const double MaximumDispersionStrength = .012;
    public const double DefaultDispersionStrength = .003;
    public const double DispersionStrengthStep = .0001;

    public const double MinimumInnerDispersionGain = 0;
    public const double MaximumInnerDispersionGain = 1;
    public const double DefaultInnerDispersionGain = .25;
    public const double InnerDispersionGainStep = .025;

    public const double MinimumOuterDispersionGain = 0;
    public const double MaximumOuterDispersionGain = 1;
    public const double DefaultOuterDispersionGain = .4;
    public const double OuterDispersionGainStep = .025;

    public const double MinimumFaceDispersionGain = 0;
    public const double MaximumFaceDispersionGain = .25;
    public const double DefaultFaceDispersionGain = 0;
    public const double FaceDispersionGainStep = .01;

    public static LiquidGlassDispersionParameters Default { get; } = new(
        DefaultDispersionStrength,
        DefaultInnerDispersionGain,
        DefaultOuterDispersionGain,
        DefaultFaceDispersionGain);

    public LiquidGlassDispersionParameters Normalize()
    {
        var defaults = Default;
        return this with
        {
            DispersionStrength = Clamp(DispersionStrength, MinimumDispersionStrength,
                MaximumDispersionStrength, defaults.DispersionStrength),
            InnerDispersionGain = Clamp(InnerDispersionGain, MinimumInnerDispersionGain,
                MaximumInnerDispersionGain, defaults.InnerDispersionGain),
            OuterDispersionGain = Clamp(OuterDispersionGain, MinimumOuterDispersionGain,
                MaximumOuterDispersionGain, defaults.OuterDispersionGain),
            FaceDispersionGain = Clamp(FaceDispersionGain, MinimumFaceDispersionGain,
                MaximumFaceDispersionGain, defaults.FaceDispersionGain),
        };
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
