namespace GlassDesk.UI.Controls;

internal readonly record struct LiquidGlassWeights(double Face, double Inner, double Outer)
{
    internal static LiquidGlassWeights FromSdf(in LiquidGlassOptics.Sample sample,
        double pixelDipX, double pixelDipY, LiquidGlassWeightParameters parameters)
    {
        if (!sample.Inside) return default;

        var sdfFootprintDip = Math.Abs(sample.InwardX) * pixelDipX
            + Math.Abs(sample.InwardY) * pixelDipY;
        var halfAaWidthDip = Math.Max(.0001, sdfFootprintDip * .5 * parameters.AntiAliasScale);
        var depth = Math.Max(0, sample.Depth);
        var outerCoverage = 1 - SmoothStep(-halfAaWidthDip,
            parameters.OuterBandWidthDip + halfAaWidthDip, depth);
        var faceCoverage = SmoothStep(parameters.FaceTransitionStartDip - halfAaWidthDip,
            parameters.FaceTransitionStartDip + parameters.FaceTransitionWidthDip + halfAaWidthDip, depth);
        var height = Math.Clamp(sample.HeightFraction, 0, 1);
        var heightBlend = 1 - parameters.HeightBevelInfluence
            + parameters.HeightBevelInfluence * height;
        var outerBevel = 1 - parameters.HeightBevelInfluence * height;

        var face = faceCoverage * heightBlend * parameters.FaceWeightGain;
        var inner = (1 - faceCoverage) * (1 - outerCoverage)
            * heightBlend * parameters.InnerWeightGain;
        var outer = outerCoverage * outerBevel * parameters.OuterWeightGain;
        var total = face + inner + outer;
        if (!double.IsFinite(total) || total <= 1e-9)
            return depth >= parameters.FaceTransitionStartDip
                ? new LiquidGlassWeights(1, 0, 0)
                : new LiquidGlassWeights(0, 1, 0);

        return new LiquidGlassWeights(face / total, inner / total, outer / total);
    }

    private static double SmoothStep(double minimum, double maximum, double value)
    {
        var amount = Math.Clamp((value - minimum) / Math.Max(.0001, maximum - minimum), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }
}
