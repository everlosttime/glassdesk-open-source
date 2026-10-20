namespace GlassDesk.UI.Controls;

/// <summary>Live-only Chapter 09 microfacet reflection controls.</summary>
public readonly record struct LiquidGlassBrdfParameters(
    double Roughness,
    double SpecularStrength,
    double FresnelStrength,
    double LightIntensity,
    double BevelSpecularSupport)
{
    public const double MinimumRoughness = .35;
    public const double MaximumRoughness = .95;
    public const double RoughnessStep = .02;
    public const double DefaultRoughness = .5;

    public const double MinimumSpecularStrength = 0;
    public const double MaximumSpecularStrength = 1;
    public const double SpecularStrengthStep = .02;
    public const double DefaultSpecularStrength = 1;

    public const double MinimumFresnelStrength = 0;
    public const double MaximumFresnelStrength = 1;
    public const double FresnelStrengthStep = .02;
    public const double DefaultFresnelStrength = 1;

    public const double MinimumLightIntensity = 0;
    public const double MaximumLightIntensity = 2;
    public const double LightIntensityStep = .02;
    public const double DefaultLightIntensity = 1.65;

    public const double MinimumBevelSpecularSupport = 0;
    public const double MaximumBevelSpecularSupport = .8;
    public const double BevelSpecularSupportStep = .02;
    public const double DefaultBevelSpecularSupport = .6;

    public static LiquidGlassBrdfParameters Default { get; } = new(
        DefaultRoughness,
        DefaultSpecularStrength,
        DefaultFresnelStrength,
        DefaultLightIntensity,
        DefaultBevelSpecularSupport);

    public LiquidGlassBrdfParameters Normalize()
    {
        var defaults = Default;
        return this with
        {
            Roughness = Clamp(Roughness, MinimumRoughness, MaximumRoughness, defaults.Roughness),
            SpecularStrength = Clamp(SpecularStrength, MinimumSpecularStrength, MaximumSpecularStrength,
                defaults.SpecularStrength),
            FresnelStrength = Clamp(FresnelStrength, MinimumFresnelStrength, MaximumFresnelStrength,
                defaults.FresnelStrength),
            LightIntensity = Clamp(LightIntensity, MinimumLightIntensity, MaximumLightIntensity,
                defaults.LightIntensity),
            BevelSpecularSupport = Clamp(BevelSpecularSupport, MinimumBevelSpecularSupport,
                MaximumBevelSpecularSupport, defaults.BevelSpecularSupport),
        };
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
