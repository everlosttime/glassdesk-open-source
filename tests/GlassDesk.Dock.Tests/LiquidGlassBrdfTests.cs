using System.Numerics;
using System.Reflection;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LiquidGlassBrdfTests
{
    private const BindingFlags StaticMethods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void Chapter09AddsTheFiveRequestedDiagnosticModes()
    {
        var names = Enum.GetNames<LiquidGlassDebugView>();

        Assert.Contains("BrdfOff", names);
        Assert.Contains("FresnelOnly", names);
        Assert.Contains("GgxSpecularOnly", names);
        Assert.Contains("BrdfSupportWeight", names);
        Assert.Contains("FinalWithBrdf", names);
    }

    [Fact]
    public void BrdfControlsAreCentralizedAndHaveSafeLiveDefaults()
    {
        var type = BrdfParametersType();
        Assert.NotNull(type);
        var defaults = type!.GetProperty("Default", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;

        Assert.InRange(Read(defaults, "Roughness"), .25, .95);
        Assert.InRange(Read(defaults, "SpecularStrength"), 0, 1);
        Assert.InRange(Read(defaults, "FresnelStrength"), 0, 1);
        Assert.InRange(Read(defaults, "LightIntensity"), 0, 2);
        Assert.InRange(Read(defaults, "BevelSpecularSupport"), 0, 1);
    }

    [Fact]
    public void F0IsDerivedFromTheCurrentGlassIor()
    {
        var f0 = Invoke("DielectricF0", 1.46);

        Assert.InRange(f0, .0348, .0351);
        Assert.Equal(0, Invoke("DielectricF0", 1), 12);
    }

    [Fact]
    public void TrowbridgeReitzDistributionIsFiniteAndPeaksAtTheHalfVector()
    {
        var atPeak = Invoke("TrowbridgeReitzDistribution", 1d, .58);
        var atHorizon = Invoke("TrowbridgeReitzDistribution", 0d, .58);

        Assert.True(double.IsFinite(atPeak));
        Assert.True(double.IsFinite(atHorizon));
        Assert.True(atPeak > atHorizon);
        Assert.InRange(atPeak, 0, 4);
    }

    [Fact]
    public void SmithHeightCorrelatedVisibilityIsFiniteAndNormalizedAtNormalIncidence()
    {
        var headOn = Invoke("SmithHeightCorrelatedVisibility", 1d, 1d, .58);
        var grazing = Invoke("SmithHeightCorrelatedVisibility", .001, .7, .58);

        Assert.Equal(.25, headOn, 6);
        Assert.True(double.IsFinite(grazing));
        Assert.InRange(grazing, 0, 4);
    }

    [Fact]
    public void BrdfRendererAndSdfSupportEvaluatorAreProvidedByTheOpticalModule()
    {
        var brdf = typeof(LiquidGlassOptics).Assembly.GetType("GlassDesk.UI.Controls.LiquidGlassBrdf");
        Assert.NotNull(brdf);
        Assert.NotNull(brdf!.GetMethod("Apply", StaticMethods));
        Assert.NotNull(brdf.GetMethod("CalculateSupportWeight", StaticMethods));
    }

    [Fact]
    public void SupportUsesBevelSdfAndNormalCurvatureWithoutMakingABorderOrCenterHighlight()
    {
        var brdfType = typeof(LiquidGlassOptics).Assembly.GetType("GlassDesk.UI.Controls.LiquidGlassBrdf");
        Assert.NotNull(brdfType);
        var support = brdfType!.GetMethod("CalculateSupportWeight", StaticMethods);
        Assert.NotNull(support);
        var defaults = DefaultBrdfParameters();
        var center = CreateSample(depth: 30, height: 1, Vector3.UnitZ, curvature: 0);
        var boundary = CreateSample(depth: 0, height: 0, Vector3.UnitZ, curvature: 0);
        var bevel = CreateSample(depth: 4, height: .3,
            Vector3.Normalize(new Vector3(.32f, -.37f, .87f)), curvature: .08);

        Assert.Equal(0, EvaluateSupport(support!, center, defaults), 8);
        Assert.Equal(0, EvaluateSupport(support!, boundary, defaults), 8);
        Assert.True(EvaluateSupport(support!, bevel, defaults) > .08);
    }

    [Fact]
    public void BevelSupportChangesContinuouslyAcrossAdjacentSdfSamples()
    {
        var brdfType = typeof(LiquidGlassOptics).Assembly.GetType("GlassDesk.UI.Controls.LiquidGlassBrdf");
        Assert.NotNull(brdfType);
        var support = brdfType!.GetMethod("CalculateSupportWeight", StaticMethods);
        Assert.NotNull(support);
        var defaults = DefaultBrdfParameters();
        var normal = Vector3.Normalize(new Vector3(.32f, -.37f, .87f));
        var before = CreateSample(depth: 4, height: .30, normal, curvature: .08);
        var after = CreateSample(depth: 4.1, height: .32, normal, curvature: .079);

        Assert.InRange(Math.Abs(EvaluateSupport(support!, before, defaults)
            - EvaluateSupport(support!, after, defaults)), 0, .06);
    }

    [Fact]
    public void ZeroStrengthReturnsTheChapter08ArrayAndSpecularNeverChangesAlpha()
    {
        var brdfType = typeof(LiquidGlassOptics).Assembly.GetType("GlassDesk.UI.Controls.LiquidGlassBrdf");
        Assert.NotNull(brdfType);
        var apply = brdfType!.GetMethod("Apply", StaticMethods);
        Assert.NotNull(apply);
        var sample = CreateSample(depth: 4, height: .3,
            Vector3.Normalize(new Vector3(.32f, -.37f, .87f)), curvature: .08);
        var samples = Array.CreateInstance(sample.GetType(), 1);
        samples.SetValue(sample, 0);
        byte[] chapter08 = [23, 61, 112, 157];
        var zeroStrength = WithBrdfParameter(DefaultBrdfParameters(), "SpecularStrength", 0);
        var identity = InvokeApply(apply!, chapter08, samples, zeroStrength, "FinalWithBrdf");
        Assert.Same(chapter08, identity);

        var alphaInput = new byte[] { 23, 61, 112, 157 };
        var alphaOutput = InvokeApply(apply!, alphaInput, samples, DefaultBrdfParameters(), "FinalWithBrdf");
        Assert.Equal(alphaInput[3], alphaOutput[3]);
    }

    [Fact]
    public void ComponentViewsAreIsolatedAndFinalWithBrdfAddsToTheChapter08Base()
    {
        var brdfType = typeof(LiquidGlassOptics).Assembly.GetType("GlassDesk.UI.Controls.LiquidGlassBrdf");
        Assert.NotNull(brdfType);
        var apply = brdfType!.GetMethod("Apply", StaticMethods);
        Assert.NotNull(apply);
        var sample = CreateSample(depth: 4, height: .3,
            Vector3.Normalize(new Vector3(.32f, -.37f, .87f)), curvature: .08);
        var samples = Array.CreateInstance(sample.GetType(), 1);
        samples.SetValue(sample, 0);
        byte[] chapter08 = [23, 61, 112, 157];

        var fresnel = InvokeApply(apply!, chapter08, samples, DefaultBrdfParameters(), "FresnelOnly");
        var ggx = InvokeApply(apply!, chapter08, samples, DefaultBrdfParameters(), "GgxSpecularOnly");
        var final = InvokeApply(apply!, chapter08, samples, DefaultBrdfParameters(), "FinalWithBrdf");

        Assert.NotEqual(fresnel, ggx);
        Assert.NotEqual(ggx, final);
        Assert.Equal(chapter08[3], fresnel[3]);
        Assert.Equal(chapter08[3], ggx[3]);
        Assert.Equal(chapter08[3], final[3]);
    }

    private static Type? BrdfParametersType() => typeof(LiquidGlassOptics).Assembly
        .GetType("GlassDesk.UI.Controls.LiquidGlassBrdfParameters");

    private static object DefaultBrdfParameters()
    {
        var type = BrdfParametersType();
        Assert.NotNull(type);
        return type!.GetProperty("Default", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
    }

    private static object CreateSample(double depth, double height, Vector3 normal, double curvature)
    {
        var type = typeof(LiquidGlassOptics).GetNestedType("Sample", BindingFlags.NonPublic);
        Assert.NotNull(type);
        var constructor = type!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "Inside" => (object)true,
            "Edge" => 1f,
            "Depth" => (float)depth,
            "HeightFraction" => (float)height,
            "InwardX" => 0f,
            "InwardY" => 1f,
            "SurfaceNormal" => normal,
            "SurfaceCurvature" => (float)curvature,
            _ when parameter.ParameterType == typeof(float) => 0f,
            _ => throw new InvalidOperationException($"Unexpected sample field: {parameter.Name}"),
        }).ToArray();
        return constructor.Invoke(arguments);
    }

    private static double EvaluateSupport(MethodInfo support, object sample, object parameters) =>
        (double)support.Invoke(null, [sample, 1d, 1d, LiquidGlassWeightParameters.Default, parameters])!;

    private static object WithBrdfParameter(object instance, string propertyName, object value)
    {
        var type = instance.GetType();
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.Name == propertyName ? value
                : type.GetProperty(parameter.Name!)!.GetValue(instance))
            .ToArray();
        return constructor.Invoke(arguments);
    }

    private static byte[] InvokeApply(MethodInfo apply, byte[] pixels, Array samples,
        object parameters, string viewName)
    {
        var view = Enum.Parse<LiquidGlassDebugView>(viewName);
        return (byte[])apply.Invoke(null,
            [pixels, samples, 1, 1, new System.Windows.Size(1, 1), LiquidGlassWeightParameters.Default,
                parameters, 1.46, view])!;
    }

    private static double Read(object instance, string propertyName) =>
        (double)instance.GetType().GetProperty(propertyName)!.GetValue(instance)!;

    private static double Invoke(string methodName, params object[] arguments)
    {
        var type = typeof(LiquidGlassOptics).Assembly.GetType("GlassDesk.UI.Controls.LiquidGlassBrdf");
        Assert.NotNull(type);
        var method = type!.GetMethod(methodName, StaticMethods);
        Assert.NotNull(method);
        return (double)method!.Invoke(null, arguments)!;
    }
}
