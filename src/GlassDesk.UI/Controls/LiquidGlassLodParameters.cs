namespace GlassDesk.UI.Controls;

/// <summary>Per-region low-pass sampling footprints, expressed in device-independent pixels.</summary>
public readonly record struct LiquidGlassLodParameters(
    double FaceFootprintDip,
    double InnerFootprintDip,
    double OuterFootprintDip)
{
    public const double MinimumFootprintDip = 0;
    public const double MaximumFootprintDip = 1.5;
    public const double FootprintDipStep = .05;
    public const double DefaultFaceFootprintDip = .7;
    public const double DefaultInnerFootprintDip = .1;
    public const double DefaultOuterFootprintDip = .95;

    public static LiquidGlassLodParameters Default { get; } = new(
        FaceFootprintDip: DefaultFaceFootprintDip,
        InnerFootprintDip: DefaultInnerFootprintDip,
        OuterFootprintDip: DefaultOuterFootprintDip);

    public double MaximumActiveFootprintDip
    {
        get
        {
            var normalized = Normalize();
            return Math.Max(normalized.FaceFootprintDip,
                Math.Max(normalized.InnerFootprintDip, normalized.OuterFootprintDip));
        }
    }

    public LiquidGlassLodParameters Normalize() => new(
        Clamp(FaceFootprintDip, DefaultFaceFootprintDip),
        Clamp(InnerFootprintDip, DefaultInnerFootprintDip),
        Clamp(OuterFootprintDip, DefaultOuterFootprintDip));

    private static double Clamp(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, MinimumFootprintDip, MaximumFootprintDip) : fallback;
}
