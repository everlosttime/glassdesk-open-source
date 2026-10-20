using System.Windows;
using System.Windows.Media;

namespace GlassDesk.UI.Controls;

/// <summary>Chapter 08 post-optics tint and external shadow controls; kept live and never persisted.</summary>
public readonly record struct LiquidGlassFinishingParameters(
    Color TintColor,
    double TintOpacity,
    double TintLuminanceInfluence,
    double AmbientShadowOpacity,
    double AmbientShadowExtentDip,
    double AmbientShadowOffsetDip,
    double ContactShadowOpacity,
    double ContactShadowExtentDip,
    double ContactShadowOffsetDip)
{
    public const double MinimumTintOpacity = 0;
    public const double MaximumTintOpacity = .08;
    public const double TintOpacityStep = .002;
    public const double DefaultTintOpacity = .02;
    public const double MinimumTintLuminanceInfluence = 0;
    public const double MaximumTintLuminanceInfluence = 1;
    public const double TintLuminanceInfluenceStep = .05;
    public const double DefaultTintLuminanceInfluence = .9;

    public const double MinimumAmbientShadowOpacity = 0;
    public const double MaximumAmbientShadowOpacity = .12;
    public const double AmbientShadowOpacityStep = .005;
    public const double DefaultAmbientShadowOpacity = .06;
    public const double MinimumAmbientShadowExtentDip = 1;
    public const double MaximumAmbientShadowExtentDip = 34;
    public const double AmbientShadowExtentDipStep = .5;
    public const double DefaultAmbientShadowExtentDip = 32;
    public const double MinimumAmbientShadowOffsetDip = 0;
    public const double MaximumAmbientShadowOffsetDip = 8;
    public const double AmbientShadowOffsetDipStep = .25;
    public const double DefaultAmbientShadowOffsetDip = 6;

    public const double MinimumContactShadowOpacity = 0;
    public const double MaximumContactShadowOpacity = .1;
    public const double ContactShadowOpacityStep = .005;
    public const double DefaultContactShadowOpacity = .045;
    public const double MinimumContactShadowExtentDip = .5;
    public const double MaximumContactShadowExtentDip = 10;
    public const double ContactShadowExtentDipStep = .5;
    public const double DefaultContactShadowExtentDip = 6;
    public const double MinimumContactShadowOffsetDip = 0;
    public const double MaximumContactShadowOffsetDip = 3;
    public const double ContactShadowOffsetDipStep = .25;
    public const double DefaultContactShadowOffsetDip = 1;

    public static LiquidGlassFinishingParameters Default { get; } = new(
        TintColor: Color.FromRgb(232, 239, 249),
        TintOpacity: DefaultTintOpacity,
        TintLuminanceInfluence: DefaultTintLuminanceInfluence,
        AmbientShadowOpacity: DefaultAmbientShadowOpacity,
        AmbientShadowExtentDip: DefaultAmbientShadowExtentDip,
        AmbientShadowOffsetDip: DefaultAmbientShadowOffsetDip,
        ContactShadowOpacity: DefaultContactShadowOpacity,
        ContactShadowExtentDip: DefaultContactShadowExtentDip,
        ContactShadowOffsetDip: DefaultContactShadowOffsetDip);

    public LiquidGlassFinishingParameters Normalize()
    {
        var defaults = Default;
        return this with
        {
            TintColor = Color.FromRgb(TintColor.R, TintColor.G, TintColor.B),
            TintOpacity = Clamp(TintOpacity, MinimumTintOpacity, MaximumTintOpacity, defaults.TintOpacity),
            TintLuminanceInfluence = Clamp(TintLuminanceInfluence, MinimumTintLuminanceInfluence,
                MaximumTintLuminanceInfluence, defaults.TintLuminanceInfluence),
            AmbientShadowOpacity = Clamp(AmbientShadowOpacity, MinimumAmbientShadowOpacity,
                MaximumAmbientShadowOpacity, defaults.AmbientShadowOpacity),
            AmbientShadowExtentDip = Clamp(AmbientShadowExtentDip, MinimumAmbientShadowExtentDip,
                MaximumAmbientShadowExtentDip, defaults.AmbientShadowExtentDip),
            AmbientShadowOffsetDip = Clamp(AmbientShadowOffsetDip, MinimumAmbientShadowOffsetDip,
                MaximumAmbientShadowOffsetDip, defaults.AmbientShadowOffsetDip),
            ContactShadowOpacity = Clamp(ContactShadowOpacity, MinimumContactShadowOpacity,
                MaximumContactShadowOpacity, defaults.ContactShadowOpacity),
            ContactShadowExtentDip = Clamp(ContactShadowExtentDip, MinimumContactShadowExtentDip,
                MaximumContactShadowExtentDip, defaults.ContactShadowExtentDip),
            ContactShadowOffsetDip = Clamp(ContactShadowOffsetDip, MinimumContactShadowOffsetDip,
                MaximumContactShadowOffsetDip, defaults.ContactShadowOffsetDip),
        };
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

/// <summary>Post-optics Chapter 08 composition. It never participates in background or refraction sampling.</summary>
internal static class LiquidGlassFinishComposer
{
    internal static bool IsComparisonView(LiquidGlassDebugView view) => view is
        LiquidGlassDebugView.TintOff or LiquidGlassDebugView.TintOnly
        or LiquidGlassDebugView.AmbientShadowOnly or LiquidGlassDebugView.ContactShadowOnly
        or LiquidGlassDebugView.TintAndShadowFinal
        or LiquidGlassDebugView.BrdfOff or LiquidGlassDebugView.FresnelOnly
        or LiquidGlassDebugView.GgxSpecularOnly or LiquidGlassDebugView.BrdfSupportWeight
        or LiquidGlassDebugView.FinalWithBrdf
        or LiquidGlassDebugView.DispersionOff or LiquidGlassDebugView.InnerDispersionOnly
        or LiquidGlassDebugView.OuterDispersionOnly or LiquidGlassDebugView.FinalWithDispersion;

    internal static LiquidGlassDebugView OpticsView(LiquidGlassDebugView view) =>
        view is LiquidGlassDebugView.DispersionOff or LiquidGlassDebugView.RRayUv
            or LiquidGlassDebugView.GRayUv or LiquidGlassDebugView.BRayUv
            or LiquidGlassDebugView.InnerDispersionOnly or LiquidGlassDebugView.OuterDispersionOnly
            or LiquidGlassDebugView.FinalWithDispersion or LiquidGlassDebugView.DispersionTimesTenDebug
            ? view
            : IsComparisonView(view) ? LiquidGlassDebugView.None : view;

    internal static bool UsesTint(LiquidGlassDebugView view) => view is
        LiquidGlassDebugView.None or LiquidGlassDebugView.TintOnly or LiquidGlassDebugView.TintAndShadowFinal
        or LiquidGlassDebugView.BrdfOff or LiquidGlassDebugView.FinalWithBrdf
        or LiquidGlassDebugView.DispersionOff or LiquidGlassDebugView.InnerDispersionOnly
        or LiquidGlassDebugView.OuterDispersionOnly or LiquidGlassDebugView.FinalWithDispersion;

    internal static bool UsesBrdf(LiquidGlassDebugView view) => view is
        LiquidGlassDebugView.None or LiquidGlassDebugView.TintAndShadowFinal
        or LiquidGlassDebugView.FresnelOnly or LiquidGlassDebugView.GgxSpecularOnly
        or LiquidGlassDebugView.BrdfSupportWeight or LiquidGlassDebugView.FinalWithBrdf
        or LiquidGlassDebugView.DispersionOff or LiquidGlassDebugView.InnerDispersionOnly
        or LiquidGlassDebugView.OuterDispersionOnly or LiquidGlassDebugView.FinalWithDispersion;

    internal static bool UsesDispersion(LiquidGlassDebugView view) => view is
        LiquidGlassDebugView.None or LiquidGlassDebugView.TintOff or LiquidGlassDebugView.TintOnly
        or LiquidGlassDebugView.AmbientShadowOnly or LiquidGlassDebugView.ContactShadowOnly
        or LiquidGlassDebugView.TintAndShadowFinal or LiquidGlassDebugView.BrdfOff
        or LiquidGlassDebugView.FinalWithBrdf or LiquidGlassDebugView.InnerDispersionOnly
        or LiquidGlassDebugView.OuterDispersionOnly or LiquidGlassDebugView.FinalWithDispersion
        or LiquidGlassDebugView.DispersionTimesTenDebug;

    internal static bool IsBrdfComponentView(LiquidGlassDebugView view) => view is
        LiquidGlassDebugView.FresnelOnly or LiquidGlassDebugView.GgxSpecularOnly
        or LiquidGlassDebugView.BrdfSupportWeight;

    internal static bool IncludesBaselineShadow(LiquidGlassDebugView view) => view is not
        LiquidGlassDebugView.AmbientShadowOnly and not LiquidGlassDebugView.ContactShadowOnly
        and not LiquidGlassDebugView.BrdfSupportWeight
        and not LiquidGlassDebugView.FresnelOnly and not LiquidGlassDebugView.GgxSpecularOnly
        and not LiquidGlassDebugView.RRayUv and not LiquidGlassDebugView.GRayUv
        and not LiquidGlassDebugView.BRayUv and not LiquidGlassDebugView.DispersionTimesTenDebug;

    internal static (bool Ambient, bool Contact) ShadowSelection(LiquidGlassDebugView view) => view switch
    {
        LiquidGlassDebugView.TintOff or LiquidGlassDebugView.TintOnly => (false, false),
        LiquidGlassDebugView.AmbientShadowOnly => (true, false),
        LiquidGlassDebugView.ContactShadowOnly => (false, true),
        LiquidGlassDebugView.None or LiquidGlassDebugView.TintAndShadowFinal => (true, true),
        LiquidGlassDebugView.BrdfOff or LiquidGlassDebugView.FinalWithBrdf => (true, true),
        LiquidGlassDebugView.DispersionOff or LiquidGlassDebugView.InnerDispersionOnly
            or LiquidGlassDebugView.OuterDispersionOnly or LiquidGlassDebugView.FinalWithDispersion => (true, true),
        LiquidGlassDebugView.BrdfSupportWeight => (false, false),
        LiquidGlassDebugView.FresnelOnly or LiquidGlassDebugView.GgxSpecularOnly => (false, false),
        _ => (false, false),
    };

    /// <summary>Apply a luminance-adaptive, sRGB tint after optics. Alpha and the source frame are unchanged.</summary>
    internal static byte[] ApplyTint(byte[] opticalBgra, LiquidGlassFinishingParameters parameters, double bodyTintScale = 1)
    {
        ArgumentNullException.ThrowIfNull(opticalBgra);
        if (opticalBgra.Length % 4 != 0)
            throw new ArgumentException("Optical pixels must contain complete BGRA pixels.", nameof(opticalBgra));
        parameters = parameters.Normalize();
        if (parameters.TintOpacity <= 0) return opticalBgra;

        var result = (byte[])opticalBgra.Clone();
        for (var index = 0; index < result.Length; index += 4)
        {
            var alpha = opticalBgra[index + 3];
            if (alpha == 0) continue;

            var blue = opticalBgra[index];
            var green = opticalBgra[index + 1];
            var red = opticalBgra[index + 2];
            var luminance = (.2126 * red + .7152 * green + .0722 * blue) / 255;
            var opacity = Math.Clamp(parameters.TintOpacity * bodyTintScale, 0, 1)
                * (1 - parameters.TintLuminanceInfluence * luminance);
            result[index] = Mix(blue, parameters.TintColor.B, opacity);
            result[index + 1] = Mix(green, parameters.TintColor.G, opacity);
            result[index + 2] = Mix(red, parameters.TintColor.R, opacity);
        }
        return result;
    }

    private static byte Mix(byte source, byte tint, double opacity) =>
        (byte)Math.Clamp((int)Math.Round(source * (1 - opacity) + tint * opacity), 0, 255);
}

/// <summary>Production order for the completed optical frame: optics, tint, BRDF/Fresnel, dispersion.</summary>
internal static class LiquidGlassFrameComposer
{
    internal static byte[] Compose(LiquidGlassOptics optics, byte[] sourceBgra,
        int pixelWidth, int pixelHeight, Size sampleSizeDip, double cornerRadiusDip,
        bool squircle, double refractionStrength, double detailSoftnessDip,
        LiquidGlassDebugView debugView, LiquidGlassSamplingParameters samplingParameters,
        LiquidGlassWeightParameters weightParameters, Rect glassBoundsDip,
        LiquidGlassLodParameters lodParameters, LiquidGlassFinishingParameters finishingParameters,
        LiquidGlassBrdfParameters brdfParameters, LiquidGlassDispersionParameters dispersionParameters,
        Action<byte[]>? afterOpticsBeforeFinishing = null,
        GlassDesk.Core.Models.GlassMaterialMode material = GlassDesk.Core.Models.GlassMaterialMode.PhysicalGlass,
        bool dark = true, AndroidLiquidGlassRenderer? androidRenderer = null,
        double tintOpacity = AndroidLiquidGlassRenderer.BodyOpacity, double backgroundClarity = .5, bool launchpadOptics = false)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(sourceBgra);

        if (material == GlassDesk.Core.Models.GlassMaterialMode.FrostedGlass
            && debugView == LiquidGlassDebugView.None)
            return FrostedGlassRenderer.Render(sourceBgra, pixelWidth, pixelHeight, sampleSizeDip,
                glassBoundsDip, cornerRadiusDip, squircle, dark, refractionStrength, backgroundClarity);

        if (material == GlassDesk.Core.Models.GlassMaterialMode.AndroidLiquidGlass
            && debugView == LiquidGlassDebugView.None)
            return (androidRenderer ?? new AndroidLiquidGlassRenderer()).Render(sourceBgra,
                pixelWidth, pixelHeight, sampleSizeDip, glassBoundsDip, cornerRadiusDip,
                squircle, dark, refractionStrength, tintOpacity, backgroundClarity, dispersionParameters, launchpadOptics);

        var opticsView = LiquidGlassFinishComposer.OpticsView(debugView);
        var applyDispersionAfterFinishing = LiquidGlassFinishComposer.UsesDispersion(debugView);
        var renderView = applyDispersionAfterFinishing
            ? LiquidGlassDebugView.DispersionOff
            : opticsView;
        var frame = optics.Render(sourceBgra, pixelWidth, pixelHeight, sampleSizeDip,
            cornerRadiusDip, squircle, refractionStrength, detailSoftnessDip,
            debugView: renderView, samplingParameters: samplingParameters,
            weightParameters: weightParameters, glassBoundsDip: glassBoundsDip,
            lodParameters: lodParameters, dispersionParameters: dispersionParameters,
            deferDispersion: applyDispersionAfterFinishing);

        afterOpticsBeforeFinishing?.Invoke(frame);
        if (LiquidGlassFinishComposer.UsesTint(debugView))
            frame = LiquidGlassFinishComposer.ApplyTint(frame, finishingParameters,
                AndroidLiquidGlassRenderer.NormalizeTintOpacity(tintOpacity) / AndroidLiquidGlassRenderer.BodyOpacity);
        if (LiquidGlassFinishComposer.UsesBrdf(debugView))
            frame = optics.ApplyBrdf(frame, sampleSizeDip, weightParameters, brdfParameters, debugView);
        if (applyDispersionAfterFinishing)
            frame = optics.ApplyDispersion(sourceBgra, frame, samplingParameters, weightParameters,
                lodParameters, dispersionParameters, debugView);

        return frame;
    }
}
