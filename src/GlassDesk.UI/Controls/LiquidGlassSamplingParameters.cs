namespace GlassDesk.UI.Controls;

/// <summary>Chapter 05 color-sampling controls; values are temporary and are never persisted.</summary>
public readonly record struct LiquidGlassSamplingParameters(
    double FaceOpticalCenterScale,
    double InnerRimBoost,
    double InnerRimWidthDip,
    double OuterRimStrength,
    double OuterRimWidthDip)
{
    public const double MinimumFaceOpticalCenterScale = 0;
    public const double MaximumFaceOpticalCenterScale = .004;
    public const double DefaultFaceOpticalCenterScale = .0015;
    public const double FaceOpticalCenterScaleStep = .0001;
    public const double MinimumInnerRimBoost = 0;
    public const double MaximumInnerRimBoost = 1.2;
    public const double DefaultInnerRimBoost = .8;
    public const double InnerRimBoostStep = .05;
    public const double MinimumInnerRimWidthDip = 3;
    public const double MaximumInnerRimWidthDip = 10;
    public const double DefaultInnerRimWidthDip = 9;
    public const double InnerRimWidthDipStep = .25;
    public const double MinimumOuterRimStrength = 0;
    public const double MaximumOuterRimStrength = .75;
    public const double DefaultOuterRimStrength = .22;
    public const double OuterRimStrengthStep = .025;
    public const double MinimumOuterRimWidthDip = 1;
    public const double MaximumOuterRimWidthDip = 5;
    public const double DefaultOuterRimWidthDip = 2.5;
    public const double OuterRimWidthDipStep = .25;
    public const double MaximumOuterRimCaptureMarginDip = 1.5;

    public static LiquidGlassSamplingParameters Default { get; } = new(
        FaceOpticalCenterScale: DefaultFaceOpticalCenterScale,
        InnerRimBoost: DefaultInnerRimBoost,
        InnerRimWidthDip: DefaultInnerRimWidthDip,
        OuterRimStrength: DefaultOuterRimStrength,
        OuterRimWidthDip: DefaultOuterRimWidthDip);

    /// <summary>Enough live background beyond the silhouette for the selected reverse displacement.</summary>
    public double OuterRimCaptureMarginDip =>
        MaximumOuterRimCaptureMarginDip * Math.Clamp(
            Normalize().OuterRimStrength / MaximumOuterRimStrength, 0, 1);

    public LiquidGlassSamplingParameters Normalize() => new(
        Clamp(FaceOpticalCenterScale, Default.FaceOpticalCenterScale, MinimumFaceOpticalCenterScale, MaximumFaceOpticalCenterScale),
        Clamp(InnerRimBoost, Default.InnerRimBoost, MinimumInnerRimBoost, MaximumInnerRimBoost),
        Clamp(InnerRimWidthDip, Default.InnerRimWidthDip, MinimumInnerRimWidthDip, MaximumInnerRimWidthDip),
        Clamp(OuterRimStrength, Default.OuterRimStrength, MinimumOuterRimStrength, MaximumOuterRimStrength),
        Clamp(OuterRimWidthDip, Default.OuterRimWidthDip, MinimumOuterRimWidthDip, MaximumOuterRimWidthDip));

    private static double Clamp(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
