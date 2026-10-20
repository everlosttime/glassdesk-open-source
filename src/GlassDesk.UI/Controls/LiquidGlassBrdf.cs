using System.Numerics;
using System.Windows;

namespace GlassDesk.UI.Controls;

/// <summary>Chapter 09 additive microfacet reflection over the existing refracted frame.</summary>
internal static class LiquidGlassBrdf
{
    private const double MaximumAddedLinearRadiance = .035;
    private static readonly Vector3 ViewDirection = Vector3.UnitZ;
    private static readonly AreaLight[] AreaLights =
    [
        new(Vector3.Normalize(new Vector3(-.58f, -.72f, .91f)), .34, 1.00, .985, .96),
        new(Vector3.Normalize(new Vector3(.71f, -.43f, .88f)), .29, .96, .99, 1.00),
        new(Vector3.Normalize(new Vector3(-.44f, .39f, 1.00f)), .21, .98, 1.00, .99),
        new(Vector3.Normalize(new Vector3(.53f, .55f, .94f)), .16, 1.00, .99, .97),
    ];

    internal static byte[] Apply(byte[] opticalBgra, LiquidGlassOptics.Sample[] samples,
        int pixelWidth, int pixelHeight, Size sampleSizeDip,
        LiquidGlassWeightParameters weightParameters, LiquidGlassBrdfParameters parameters,
        double indexOfRefraction, LiquidGlassDebugView debugView)
    {
        ArgumentNullException.ThrowIfNull(opticalBgra);
        ArgumentNullException.ThrowIfNull(samples);
        if (pixelWidth <= 0 || pixelHeight <= 0 || opticalBgra.Length != pixelWidth * pixelHeight * 4
            || samples.Length < pixelWidth * pixelHeight || !double.IsFinite(sampleSizeDip.Width)
            || !double.IsFinite(sampleSizeDip.Height) || sampleSizeDip.Width <= 0 || sampleSizeDip.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));

        if (!Enum.IsDefined(debugView)) throw new ArgumentOutOfRangeException(nameof(debugView));
        if (debugView == LiquidGlassDebugView.BrdfOff) return opticalBgra;

        parameters = parameters.Normalize();
        weightParameters = weightParameters.Normalize();
        var pixelDipX = sampleSizeDip.Width / pixelWidth;
        var pixelDipY = sampleSizeDip.Height / pixelHeight;
        if (debugView == LiquidGlassDebugView.BrdfSupportWeight)
            return RenderSupportWeight(opticalBgra, samples, pixelWidth, pixelHeight,
                pixelDipX, pixelDipY, weightParameters, parameters);

        var isFinalComposition = debugView is LiquidGlassDebugView.None
            or LiquidGlassDebugView.TintAndShadowFinal
            or LiquidGlassDebugView.DispersionOff
            or LiquidGlassDebugView.InnerDispersionOnly or LiquidGlassDebugView.OuterDispersionOnly
            or LiquidGlassDebugView.FinalWithDispersion;
        if (debugView is not LiquidGlassDebugView.FresnelOnly
            and not LiquidGlassDebugView.GgxSpecularOnly
            and not LiquidGlassDebugView.FinalWithBrdf
            && !isFinalComposition)
            return opticalBgra;
        var isolatedComponent = debugView is LiquidGlassDebugView.FresnelOnly
            or LiquidGlassDebugView.GgxSpecularOnly;
        if (parameters.SpecularStrength <= 0 || parameters.FresnelStrength <= 0
            || parameters.LightIntensity <= 0 || parameters.BevelSpecularSupport <= 0)
            return isolatedComponent ? EmptyComponent(opticalBgra) : opticalBgra;

        var f0 = DielectricF0(indexOfRefraction);
        var result = isolatedComponent ? EmptyComponent(opticalBgra) : (byte[])opticalBgra.Clone();
        for (var pixel = 0; pixel < pixelWidth * pixelHeight; pixel++)
        {
            var sourceIndex = pixel * 4;
            if (opticalBgra[sourceIndex + 3] == 0) continue;
            var sample = samples[pixel];
            var support = CalculateSupportWeight(sample, pixelDipX, pixelDipY, weightParameters, parameters);
            if (support <= 0) continue;

            var normal = NormalizeSurfaceNormal(sample.SurfaceNormal);
            var nDotV = Math.Clamp(Vector3.Dot(normal, ViewDirection), 0, 1);
            if (nDotV <= 0) continue;
            var incident = EvaluateEnvironment(normal, nDotV, f0, parameters,
                debugView == LiquidGlassDebugView.FresnelOnly);
            var scale = Math.Clamp(support * parameters.SpecularStrength * parameters.FresnelStrength, 0, 1);
            var blue = Math.Min(MaximumAddedLinearRadiance, incident.Blue * scale);
            var green = Math.Min(MaximumAddedLinearRadiance, incident.Green * scale);
            var red = Math.Min(MaximumAddedLinearRadiance, incident.Red * scale);
            var baseBlue = isolatedComponent ? (byte)0 : opticalBgra[sourceIndex];
            var baseGreen = isolatedComponent ? (byte)0 : opticalBgra[sourceIndex + 1];
            var baseRed = isolatedComponent ? (byte)0 : opticalBgra[sourceIndex + 2];
            result[sourceIndex] = AddLinearRadiance(baseBlue, blue);
            result[sourceIndex + 1] = AddLinearRadiance(baseGreen, green);
            result[sourceIndex + 2] = AddLinearRadiance(baseRed, red);
        }
        return result;
    }

    internal static double CalculateSupportWeight(LiquidGlassOptics.Sample sample,
        double pixelDipX, double pixelDipY,
        LiquidGlassWeightParameters weightParameters, LiquidGlassBrdfParameters parameters)
    {
        if (!sample.Inside || !double.IsFinite(sample.Depth) || !double.IsFinite(sample.HeightFraction)
            || !float.IsFinite(sample.SurfaceNormal.X) || !float.IsFinite(sample.SurfaceNormal.Y)
            || !float.IsFinite(sample.SurfaceNormal.Z))
            return 0;

        parameters = parameters.Normalize();
        var normal = NormalizeSurfaceNormal(sample.SurfaceNormal);
        var normalTilt = Math.Sqrt(Math.Max(0, normal.X * normal.X + normal.Y * normal.Y));
        var bevelAngleSupport = SmoothStep(.015, .55, normalTilt);
        if (bevelAngleSupport <= 0 || parameters.BevelSpecularSupport <= 0) return 0;

        var height = Math.Clamp(sample.HeightFraction, 0, 1);
        var normalizedWeightParameters = weightParameters.Normalize();
        var fadeStartDip = Math.Max(normalizedWeightParameters.OuterBandWidthDip,
            normalizedWeightParameters.FaceTransitionStartDip * .5);
        var fadeEndDip = normalizedWeightParameters.FaceTransitionStartDip
            + normalizedWeightParameters.FaceTransitionWidthDip + normalizedWeightParameters.OuterBandWidthDip;
        var bevelDepthSupport = 1 - SmoothStep(fadeStartDip, fadeEndDip, sample.Depth);
        var curvatureSupport = .55 + .45 * SmoothStep(.02, .45,
            Math.Clamp(sample.SurfaceCurvature, 0, 1));
        var weights = LiquidGlassWeights.FromSdf(sample, pixelDipX, pixelDipY, normalizedWeightParameters);

        // The normalized outer SDF channel establishes outer-bevel support. The inner
        // SDF channel carries the raised curvature band; the flat Face channel contributes none.
        var outerBevelSupport = weights.Outer * (1 - height) * bevelAngleSupport;
        var innerBevelSupport = weights.Inner * bevelDepthSupport
            * bevelAngleSupport * curvatureSupport;
        return Math.Clamp(Math.Max(outerBevelSupport, innerBevelSupport)
            * parameters.BevelSpecularSupport, 0, 1);
    }

    internal static double DielectricF0(double indexOfRefraction)
    {
        if (!double.IsFinite(indexOfRefraction) || indexOfRefraction < 1)
            indexOfRefraction = LiquidGlassOptics.DefaultIndexOfRefraction;
        var ratio = (indexOfRefraction - 1) / (indexOfRefraction + 1);
        return ratio * ratio;
    }

    internal static double TrowbridgeReitzDistribution(double nDotH, double roughness)
    {
        roughness = double.IsFinite(roughness) ? Math.Clamp(roughness, .05, 1) : .72;
        nDotH = double.IsFinite(nDotH) ? Math.Clamp(nDotH, 0, 1) : 0;
        var alpha = roughness * roughness;
        var alphaSquared = alpha * alpha;
        var nDotHSquared = nDotH * nDotH;
        var denominator = nDotHSquared * (alphaSquared - 1) + 1;
        return alphaSquared / Math.Max(Math.PI * denominator * denominator, 1e-12);
    }

    internal static double SmithHeightCorrelatedVisibility(double nDotV, double nDotL, double roughness)
    {
        if (!double.IsFinite(nDotV) || !double.IsFinite(nDotL)) return 0;
        nDotV = Math.Clamp(nDotV, 0, 1);
        nDotL = Math.Clamp(nDotL, 0, 1);
        if (nDotV <= 0 || nDotL <= 0) return 0;

        roughness = double.IsFinite(roughness) ? Math.Clamp(roughness, .05, 1) : .72;
        var alpha = roughness * roughness;
        var alphaSquared = alpha * alpha;
        var lambdaView = nDotL * Math.Sqrt(nDotV * nDotV * (1 - alphaSquared) + alphaSquared);
        var lambdaLight = nDotV * Math.Sqrt(nDotL * nDotL * (1 - alphaSquared) + alphaSquared);
        return .5 / Math.Max(lambdaView + lambdaLight, 1e-12);
    }

    private static byte[] RenderSupportWeight(byte[] opticalBgra, LiquidGlassOptics.Sample[] samples,
        int pixelWidth, int pixelHeight, double pixelDipX, double pixelDipY,
        LiquidGlassWeightParameters weights, LiquidGlassBrdfParameters parameters)
    {
        var result = new byte[opticalBgra.Length];
        for (var pixel = 0; pixel < pixelWidth * pixelHeight; pixel++)
        {
            var index = pixel * 4;
            result[index + 3] = opticalBgra[index + 3];
            if (opticalBgra[index + 3] == 0) continue;
            var weight = Encode(CalculateSupportWeight(samples[pixel], pixelDipX, pixelDipY, weights, parameters));
            result[index] = result[index + 1] = result[index + 2] = weight;
        }
        return result;
    }

    private static byte[] EmptyComponent(byte[] opticalBgra)
    {
        var result = new byte[opticalBgra.Length];
        for (var index = 3; index < opticalBgra.Length; index += 4)
            result[index] = opticalBgra[index];
        return result;
    }

    private static (double Blue, double Green, double Red) EvaluateEnvironment(
        Vector3 normal, double nDotV, double f0, LiquidGlassBrdfParameters parameters, bool fresnelOnly)
    {
        double blue = 0, green = 0, red = 0;
        foreach (var light in AreaLights)
        {
            var nDotL = Math.Max(0, Vector3.Dot(normal, light.Direction));
            if (nDotL <= 0) continue;
            var halfVector = Vector3.Normalize(ViewDirection + light.Direction);
            var vDotH = Math.Clamp(Vector3.Dot(ViewDirection, halfVector), 0, 1);
            var fresnel = f0 + (1 - f0) * Math.Pow(1 - vDotH, 5);
            var response = fresnelOnly
                ? fresnel * nDotL
                : TrowbridgeReitzDistribution(Math.Max(0, Vector3.Dot(normal, halfVector)), parameters.Roughness)
                    * SmithHeightCorrelatedVisibility(nDotV, nDotL, parameters.Roughness)
                    * fresnel * nDotL;
            var energy = response * light.Intensity * parameters.LightIntensity;
            blue += energy * light.Blue;
            green += energy * light.Green;
            red += energy * light.Red;
        }
        return (blue, green, red);
    }

    private static Vector3 NormalizeSurfaceNormal(Vector3 normal)
    {
        if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z)
            || normal.LengthSquared() < 1e-8f)
            return Vector3.UnitZ;
        return Vector3.Normalize(normal);
    }

    private static double SmoothStep(double minimum, double maximum, double value)
    {
        var amount = Math.Clamp((value - minimum) / Math.Max(.0001, maximum - minimum), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static byte AddLinearRadiance(byte srgb, double radiance)
    {
        var sourceLinear = ToLinear(srgb / 255d);
        var resultLinear = Math.Clamp(sourceLinear + radiance, 0, 1);
        var resultSrgb = resultLinear <= .0031308
            ? resultLinear * 12.92
            : 1.055 * Math.Pow(resultLinear, 1 / 2.4) - .055;
        return (byte)Math.Clamp((int)Math.Round(resultSrgb * 255), 0, 255);
    }

    private static double ToLinear(double srgb) => srgb <= .04045
        ? srgb / 12.92
        : Math.Pow((srgb + .055) / 1.055, 2.4);

    private static byte Encode(double weight) =>
        (byte)Math.Clamp((int)Math.Round(Math.Clamp(weight, 0, 1) * 255), 0, 255);

    private readonly record struct AreaLight(Vector3 Direction, double Intensity,
        double Blue, double Green, double Red);
}
