namespace GlassDesk.UI.Controls;

/// <summary>Temporary SDF band and normalization controls for the Chapter 06 sampling blend.</summary>
public readonly record struct LiquidGlassWeightParameters(
    double OuterBandWidthDip,
    double FaceTransitionStartDip,
    double FaceTransitionWidthDip,
    double AntiAliasScale,
    double FaceWeightGain,
    double InnerWeightGain,
    double OuterWeightGain,
    double HeightBevelInfluence)
{
    public const double MinimumOuterBandWidthDip = 1;
    public const double MaximumOuterBandWidthDip = 5;
    public const double DefaultOuterBandWidthDip = 3;
    public const double OuterBandWidthDipStep = .25;
    public const double MinimumFaceTransitionStartDip = 5;
    public const double MaximumFaceTransitionStartDip = 14;
    public const double DefaultFaceTransitionStartDip = 8;
    public const double FaceTransitionStartDipStep = .25;
    public const double MinimumFaceTransitionWidthDip = 2;
    public const double MaximumFaceTransitionWidthDip = 8;
    public const double DefaultFaceTransitionWidthDip = 4;
    public const double FaceTransitionWidthDipStep = .25;
    public const double MinimumAntiAliasScale = .25;
    public const double MaximumAntiAliasScale = 2;
    public const double DefaultAntiAliasScale = 1;
    public const double AntiAliasScaleStep = .05;
    public const double MinimumFaceWeightGain = .25;
    public const double MaximumFaceWeightGain = 2;
    public const double DefaultFaceWeightGain = 1;
    public const double FaceWeightGainStep = .05;
    public const double MinimumInnerWeightGain = .25;
    public const double MaximumInnerWeightGain = 2;
    public const double DefaultInnerWeightGain = 1;
    public const double InnerWeightGainStep = .05;
    public const double MinimumOuterWeightGain = .25;
    public const double MaximumOuterWeightGain = 2;
    public const double DefaultOuterWeightGain = 1;
    public const double OuterWeightGainStep = .05;
    public const double MinimumHeightBevelInfluence = 0;
    public const double MaximumHeightBevelInfluence = 1;
    public const double DefaultHeightBevelInfluence = .5;
    public const double HeightBevelInfluenceStep = .05;

    public static LiquidGlassWeightParameters Default { get; } = new(
        OuterBandWidthDip: DefaultOuterBandWidthDip,
        FaceTransitionStartDip: DefaultFaceTransitionStartDip,
        FaceTransitionWidthDip: DefaultFaceTransitionWidthDip,
        AntiAliasScale: DefaultAntiAliasScale,
        FaceWeightGain: DefaultFaceWeightGain,
        InnerWeightGain: DefaultInnerWeightGain,
        OuterWeightGain: DefaultOuterWeightGain,
        HeightBevelInfluence: DefaultHeightBevelInfluence);

    public LiquidGlassWeightParameters Normalize() => new(
        Clamp(OuterBandWidthDip, Default.OuterBandWidthDip, MinimumOuterBandWidthDip, MaximumOuterBandWidthDip),
        Clamp(FaceTransitionStartDip, Default.FaceTransitionStartDip, MinimumFaceTransitionStartDip, MaximumFaceTransitionStartDip),
        Clamp(FaceTransitionWidthDip, Default.FaceTransitionWidthDip, MinimumFaceTransitionWidthDip, MaximumFaceTransitionWidthDip),
        Clamp(AntiAliasScale, Default.AntiAliasScale, MinimumAntiAliasScale, MaximumAntiAliasScale),
        Clamp(FaceWeightGain, Default.FaceWeightGain, MinimumFaceWeightGain, MaximumFaceWeightGain),
        Clamp(InnerWeightGain, Default.InnerWeightGain, MinimumInnerWeightGain, MaximumInnerWeightGain),
        Clamp(OuterWeightGain, Default.OuterWeightGain, MinimumOuterWeightGain, MaximumOuterWeightGain),
        Clamp(HeightBevelInfluence, Default.HeightBevelInfluence, MinimumHeightBevelInfluence, MaximumHeightBevelInfluence));

    private static double Clamp(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
