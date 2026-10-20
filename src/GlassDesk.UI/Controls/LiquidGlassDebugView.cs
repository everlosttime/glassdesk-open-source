namespace GlassDesk.UI.Controls;

/// <summary>Temporary visualizations of values produced by the liquid-glass optical field.</summary>
public enum LiquidGlassDebugView
{
    None,
    SignedDistance,
    Height,
    Normal,
    RefractionUvOffset,
    FaceOnly,
    InnerOnly,
    OuterOnly,
    FaceWeightOnly,
    InnerWeightOnly,
    OuterWeightOnly,
    CombinedWeightsPreview,
    FinalMixedSampling,
    LodOff,
    FaceLodOnly,
    InnerLodOnly,
    OuterLodOnly,
    FinalMixedWithLod,
    TintOff,
    TintOnly,
    AmbientShadowOnly,
    ContactShadowOnly,
    TintAndShadowFinal,
    BrdfOff,
    FresnelOnly,
    GgxSpecularOnly,
    BrdfSupportWeight,
    FinalWithBrdf,
    DispersionOff,
    RRayUv,
    GRayUv,
    BRayUv,
    InnerDispersionOnly,
    OuterDispersionOnly,
    FinalWithDispersion,
    DispersionTimesTenDebug,
}

/// <summary>In-process diagnostic switches. These values are deliberately not user settings.</summary>
public static class LiquidGlassDebugState
{
    private static LiquidGlassDebugView _view = ReadInitialView();
    private static bool _showPerformanceOverlay = ReadInitialPerformanceOverlay();
    private static LiquidGlassSamplingParameters _samplingParameters = LiquidGlassSamplingParameters.Default;
    private static LiquidGlassWeightParameters _weightParameters = LiquidGlassWeightParameters.Default;
    private static LiquidGlassLodParameters _lodParameters = LiquidGlassLodParameters.Default;
    private static LiquidGlassFinishingParameters _finishingParameters = LiquidGlassFinishingParameters.Default;
    private static LiquidGlassBrdfParameters _brdfParameters = LiquidGlassBrdfParameters.Default;
    private static LiquidGlassDispersionParameters _dispersionParameters = LiquidGlassDispersionParameters.Default;

    internal static event EventHandler? CompositionChanged;

    public static LiquidGlassDebugView View
    {
        get => _view;
        set
        {
            var normalized = Enum.IsDefined(value) ? value : LiquidGlassDebugView.None;
            if (_view == normalized) return;
            _view = normalized;
            CompositionChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool ShowPerformanceOverlay
    {
        get => _showPerformanceOverlay;
        set => _showPerformanceOverlay = value;
    }

    /// <summary>Live Chapter 05 tuning shared by the temporary diagnostics UI and each glass surface.</summary>
    public static LiquidGlassSamplingParameters SamplingParameters
    {
        get => _samplingParameters;
        set => _samplingParameters = value.Normalize();
    }

    /// <summary>Live Chapter 06 SDF band and channel-gain controls; never persisted.</summary>
    public static LiquidGlassWeightParameters WeightParameters
    {
        get => _weightParameters;
        set => _weightParameters = value.Normalize();
    }

    /// <summary>Live Chapter 07 per-region low-pass footprints; never persisted.</summary>
    public static LiquidGlassLodParameters LodParameters
    {
        get => _lodParameters;
        set => _lodParameters = value.Normalize();
    }

    /// <summary>Live Chapter 08 tint and external shadow controls; never persisted.</summary>
    public static LiquidGlassFinishingParameters FinishingParameters
    {
        get => _finishingParameters;
        set
        {
            var normalized = value.Normalize();
            if (_finishingParameters == normalized) return;
            _finishingParameters = normalized;
            CompositionChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Live Chapter 09 microfacet controls; never persisted.</summary>
    public static LiquidGlassBrdfParameters BrdfParameters
    {
        get => _brdfParameters;
        set
        {
            var normalized = value.Normalize();
            if (_brdfParameters == normalized) return;
            _brdfParameters = normalized;
            CompositionChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Live Chapter 10 channel IOR spread and region gains; never persisted.</summary>
    public static LiquidGlassDispersionParameters DispersionParameters
    {
        get => _dispersionParameters;
        set
        {
            var normalized = value.Normalize();
            if (_dispersionParameters == normalized) return;
            _dispersionParameters = normalized;
            CompositionChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static LiquidGlassDebugView ReadInitialView()
    {
        var configured = Environment.GetEnvironmentVariable("GLASSDESK_GLASS_DEBUG_VIEW");
        return Enum.TryParse(configured, true, out LiquidGlassDebugView view) && Enum.IsDefined(view)
            ? view
            : LiquidGlassDebugView.None;
    }

    private static bool ReadInitialPerformanceOverlay()
    {
        var configured = Environment.GetEnvironmentVariable("GLASSDESK_GLASS_DEBUG_METRICS");
        return string.Equals(configured, "1", StringComparison.Ordinal)
            || bool.TryParse(configured, out var enabled) && enabled;
    }
}
