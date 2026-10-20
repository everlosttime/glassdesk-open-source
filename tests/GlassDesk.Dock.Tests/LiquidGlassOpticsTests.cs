using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LiquidGlassOpticsTests
{
    [Fact]
    public void ContinuousCornersPreserveFlatDockEdgesAndContainMoreThanCircularCorners()
    {
        var bounds = new Rect(10, 20, 640, 96);
        Assert.True(ContinuousGlassGeometry.Contains(bounds, 28, new Point(330, 20.2)));
        Assert.True(ContinuousGlassGeometry.Contains(bounds, 28, new Point(16, 26)));
        Assert.False(ContinuousGlassGeometry.Contains(bounds, 28, new Point(10.2, 20.2)));
        Assert.False(ContinuousGlassGeometry.Contains(bounds, 28, new Point(9, 60)));
        var shape = ContinuousGlassGeometry.Create(bounds, 28);
        Assert.True(shape.FillContains(new Point(16, 26)));
        Assert.False(shape.FillContains(new Point(10.2, 20.2)));
        Assert.Equal(bounds, shape.Bounds);
    }

    [Fact]
    public void CornerNormalApproachesTheStraightEdgeWithZeroCurvature()
    {
        var bounds = new Rect(0, 0, 200, 100);
        // Points just inside the top-left corner at two hand-chosen curve parameters.
        Assert.True(ContinuousGlassGeometry.TryGetEdge(bounds, 40,
            new Point(34.295, 0.015), out _, out var nearNormal));
        Assert.True(ContinuousGlassGeometry.TryGetEdge(bounds, 40,
            new Point(29.16, 0.05), out _, out var farNormal));
        Assert.InRange(nearNormal.X, 0, farNormal.X * .45);
        Assert.InRange(nearNormal.Y, .99, 1);
    }

    [Fact]
    public void ShoulderRefractionActuallyMovesAnUnderlyingLine()
    {
        const int width = 96, height = 80;
        var source = Frame(width, height, (_, y) => Math.Abs(y - 8) <= 2 ? (byte)255 : (byte)0);
        var output = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 20);
        var sourcePeak = 8;
        var outputPeak = Enumerable.Range(1, 18)
            .MaxBy(y => output[(y * width + width / 2) * 4 + 1]);
        Assert.NotEqual(sourcePeak, outputPeak);
        Assert.InRange(output[(outputPeak * width + width / 2) * 4 + 1], 90, 255);
    }

    [Fact]
    public void TransitionStrengthBlendsFromUnbentToFullyRefractedGlass()
    {
        const int width = 96, height = 80;
        // A broad contour survives detail filtering; a one-pixel line deliberately does not.
        var source = Frame(width, height, (_, y) => (byte)Math.Max(0, 255 - 40 * Math.Abs(y - 8)));
        var optics = new LiquidGlassOptics();

        var clear = optics.Render(source, width, height, new Size(width, height), 20, refractionStrength: 0);
        var refracted = optics.Render(source, width, height, new Size(width, height), 20, refractionStrength: 1);
        var clearPeak = Enumerable.Range(1, 18).MaxBy(y => clear[(y * width + width / 2) * 4 + 1]);
        var refractedPeak = Enumerable.Range(1, 18).MaxBy(y => refracted[(y * width + width / 2) * 4 + 1]);

        Assert.Equal(8, clearPeak);
        Assert.NotEqual(clearPeak, refractedPeak);
    }

    [Fact]
    public void FineBackgroundDetailIsSuppressedWithoutLosingOpacity()
    {
        const int width = 120, height = 100;
        var source = Frame(width, height, (x, y) => (x + y) % 2 == 0 ? (byte)255 : (byte)0);
        var output = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 26);
        for (var y = 35; y < 65; y++)
        for (var x = 45; x < 75; x++)
        {
            var index = (y * width + x) * 4;
            Assert.InRange(output[index], 110, 145);
            Assert.Equal(255, output[index + 3]);
        }
    }

    [Fact]
    public void BroadColorBlocksRetainContrastWithoutWhiteOrGrayMixing()
    {
        const int width = 160, height = 100;
        var source = Frame(width, height, (x, _) => x < 80 ? (byte)20 : (byte)220);
        var output = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 26);
        Assert.Equal(20, output[(50 * width + 40) * 4]);
        Assert.Equal(220, output[(50 * width + 120) * 4]);
        Assert.InRange(output[(50 * width + 70) * 4], 20, 25);
        Assert.InRange(output[(50 * width + 90) * 4], 215, 220);
    }

    [Fact]
    public void FlatCenterDoesNotDisplaceContoursAndZeroStrengthPreservesSource()
    {
        const int width = 160, height = 100;
        var source = Frame(width, height, (x, _) => (byte)x);
        var optics = new LiquidGlassOptics();
        var output = optics.Render(source, width, height, new Size(width, height), 26);
        var clear = optics.Render(source, width, height, new Size(width, height), 26, refractionStrength: 0);
        Assert.Equal(source[(40 * width + 50) * 4], output[(40 * width + 50) * 4]);
        Assert.Equal(source[(40 * width + 50) * 4], clear[(40 * width + 50) * 4]);
    }

    [Fact]
    public void UniformBackdropKeepsItsColorAndOpacityAtTheGlassShoulder()
    {
        var source = Frame(80, 60, (_, _) => 31);
        var output = new LiquidGlassOptics().Render(source, 80, 60, new Size(80, 60), 20);
        for (var y = 0; y < 20; y++)
        {
            var index = (y * 80 + 40) * 4;
            Assert.Equal(31, output[index]);
            Assert.Equal(31, output[index + 1]);
            Assert.Equal(31, output[index + 2]);
            Assert.Equal(255, output[index + 3]);
        }
        Assert.Equal(0, output[3]);
    }

    [Fact]
    public void NeutralBackdropHasNoRgbChannelSeparation()
    {
        // A neutral ramp exposes any accidental per-channel UV offset.
        var source = Frame(80, 60, (_, y) => (byte)Math.Min(255, y * 10));
        var output = new LiquidGlassOptics().Render(source, 80, 60, new Size(80, 60), 20);
        var separation = Enumerable.Range(2, 14).Select(y =>
        {
            var index = (y * 80 + 40) * 4;
            return Math.Abs(output[index] - output[index + 2]);
        }).Max();
        Assert.Equal(0, separation);
    }

    [Fact]
    public void DockSizedSquircleFrameKeepsUniformColorAcrossTheRim()
    {
        const int width = 640, height = 96;
        var source = Frame(width, height, (_, _) => 73);
        var output = new LiquidGlassOptics().Render(source, width, height,
            new Size(width, height), 40, squircle: true);
        Assert.Equal(source.Length, output.Length);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            if (output[index + 3] == 0) continue;
            Assert.Equal(73, output[index]);
            Assert.Equal(73, output[index + 1]);
            Assert.Equal(73, output[index + 2]);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(2, 2)]
    public void TinyFramesAndOversizedCornersRemainSafe(int width, int height)
    {
        var source = Frame(width, height, (_, _) => 173);
        var output = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 500);
        Assert.Equal(source.Length, output.Length);
        for (var i = 0; i < output.Length; i += 4)
        {
            if (output[i + 3] == 0) continue;
            Assert.Equal(173, output[i]);
            Assert.Equal(255, output[i + 3]);
        }
        Assert.Contains((byte)255, output.Where((_, i) => i % 4 == 3));
    }

    [Fact]
    public void ReusedMapsStillSampleTheNewFrameAndRefreshAfterResize()
    {
        var optics = new LiquidGlassOptics();
        var first = optics.Render(Frame(80, 60, (_, _) => 20), 80, 60, new Size(80, 60), 20);
        var secondSource = Frame(80, 60, (_, _) => 200);
        var second = optics.Render(secondSource, 80, 60, new Size(80, 60), 20);
        Assert.Equal(20, first[(5 * 80 + 40) * 4]);
        Assert.Equal(200, second[(5 * 80 + 40) * 4]);
        Assert.Equal(200, secondSource[0]);
        var resized = optics.Render(Frame(20, 10, (_, _) => 90), 20, 10, new Size(40, 20), 2);
        Assert.Equal(800, resized.Length);
        Assert.Equal(90, resized[(5 * 20 + 10) * 4]);
    }

    [Fact]
    public void InvalidDimensionsAndNonFiniteGeometryAreRejected()
    {
        var optics = new LiquidGlassOptics();
        Assert.Throws<ArgumentOutOfRangeException>(() => optics.Render([], 0, 1, new Size(1, 1), 1));
        Assert.Throws<ArgumentException>(() => optics.Render(new byte[2], 1, 1, new Size(1, 1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => optics.Render(new byte[4], 1, 1, new Size(0, 1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => optics.Render(new byte[4], 1, 1, new Size(1, 1), double.NaN));
    }

    [Theory]
    [InlineData("SignedDistance")]
    [InlineData("Height")]
    [InlineData("Normal")]
    [InlineData("RefractionUvOffset")]
    public void DebugViewsExposeTheCurrentOpticalFields(string viewName)
    {
        const int width = 128, height = 80;
        var output = new LiquidGlassOptics().Render(Frame(width, height, (_, _) => 120), width, height,
            new Size(width, height), 20, debugView: Enum.Parse<LiquidGlassDebugView>(viewName));
        Assert.Equal(width * height * 4, output.Length);

        var center = (40 * width + 64) * 4;
        var topBevel = (2 * width + 64) * 4;
        if (viewName == "SignedDistance")
        {
            Assert.InRange(output[center], (byte)0, (byte)100);
            Assert.InRange(output[3], (byte)150, (byte)255);
        }
        else if (viewName == "Height")
        {
            Assert.InRange(output[center], (byte)230, (byte)255);
            Assert.InRange(output[topBevel], (byte)0, (byte)60);
        }
        else if (viewName == "Normal")
        {
            Assert.InRange(output[center + 2], (byte)120, (byte)136);
            Assert.Equal(255, output[center]);
            Assert.True(output[topBevel + 1] < 128);
        }
        else
        {
            Assert.InRange(output[center + 2], (byte)120, (byte)136);
            Assert.InRange(output[center + 1], (byte)120, (byte)136);
            Assert.True(output[topBevel + 1] != 128 || output[topBevel + 2] != 128);
        }
    }

    [Fact]
    public void DebugRenderingLeavesTheNormalOpticalOutputUnchanged()
    {
        const int width = 128, height = 80;
        var optics = new LiquidGlassOptics();
        var source = Frame(width, height, (x, y) => (byte)((x * 13 + y * 7) % 256));
        var expected = optics.Render(source, width, height, new Size(width, height), 20);

        foreach (var view in Enum.GetValues<LiquidGlassDebugView>().Where(view => view != LiquidGlassDebugView.None))
            _ = optics.Render(source, width, height, new Size(width, height), 20, debugView: view);

        var actual = optics.Render(source, width, height, new Size(width, height), 20);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("FaceOnly")]
    [InlineData("InnerOnly")]
    [InlineData("OuterOnly")]
    public void Chapter05ColorDebugViewsReturnSampledGlassColor(string viewName)
    {
        const int width = 200, height = 100;
        var source = Frame(width, height, (x, y) => (byte)((x + y * 2) % 256));
        var mode = Enum.Parse<LiquidGlassDebugView>(viewName);
        var output = new LiquidGlassOptics().Render(source, width, height,
            new Size(width, height), 24, debugView: mode);

        Assert.Equal(source.Length, output.Length);
        var center = (50 * width + 100) * 4;
        Assert.Equal(255, output[center + 3]);
        Assert.InRange(Math.Abs(output[center] - source[center]), 0, 1);
        Assert.Equal(0, output[3]);
    }

    [Fact]
    public void Chapter05SamplingParametersAreGroupedAndRuntimeAdjustable()
    {
        var property = typeof(LiquidGlassDebugState).GetProperty("SamplingParameters");
        Assert.NotNull(property);
        Assert.True(property!.CanRead && property.CanWrite);
        foreach (var name in new[]
        {
            "FaceOpticalCenterScale", "InnerRimBoost", "InnerRimWidthDip",
            "OuterRimStrength", "OuterRimWidthDip",
        })
            Assert.NotNull(property.PropertyType.GetProperty(name));
    }

    [Fact]
    public void Chapter05SamplingParameterRangesKeepFaceAndOuterRimRestrained()
    {
        var normalized = new LiquidGlassSamplingParameters(
            FaceOpticalCenterScale: double.PositiveInfinity,
            InnerRimBoost: 99,
            InnerRimWidthDip: 99,
            OuterRimStrength: 99,
            OuterRimWidthDip: 99).Normalize();

        Assert.Equal(LiquidGlassSamplingParameters.DefaultFaceOpticalCenterScale, normalized.FaceOpticalCenterScale);
        Assert.Equal(LiquidGlassSamplingParameters.MaximumInnerRimBoost, normalized.InnerRimBoost);
        Assert.Equal(LiquidGlassSamplingParameters.MaximumInnerRimWidthDip, normalized.InnerRimWidthDip);
        Assert.Equal(LiquidGlassSamplingParameters.MaximumOuterRimStrength, normalized.OuterRimStrength);
        Assert.Equal(LiquidGlassSamplingParameters.MaximumOuterRimWidthDip, normalized.OuterRimWidthDip);
        Assert.Equal(LiquidGlassSamplingParameters.MaximumOuterRimCaptureMarginDip, normalized.OuterRimCaptureMarginDip);
    }

    [Fact]
    public void FaceOnlyKeepsTheOpticalCenterFixedAndAppliesOnlyItsBoundedScale()
    {
        const int width = 400, height = 200;
        var source = Frame(width, height, (x, _) => (byte)((x * 5) % 256));
        var tuning = LiquidGlassSamplingParameters.Default;
        var unscaled = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 30,
            debugView: LiquidGlassDebugView.FaceOnly,
            samplingParameters: tuning with { FaceOpticalCenterScale = 0 });
        var scaled = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 30,
            debugView: LiquidGlassDebugView.FaceOnly, samplingParameters: tuning);

        var center = (100 * width + 200) * 4;
        var face = (100 * width + 300) * 4;
        Assert.Equal(source[center], scaled[center]);
        Assert.InRange(Math.Abs(scaled[face] - unscaled[face]), 1, 5);
    }

    [Fact]
    public void InnerOnlyAmplifiesTheExistingPhysicalRefractionAlongTheBevel()
    {
        const int width = 200, height = 100;
        var source = Frame(width, height, (_, y) => (byte)(y * 3));
        var tuning = LiquidGlassSamplingParameters.Default;
        var baseline = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 24,
            debugView: LiquidGlassDebugView.InnerOnly,
            samplingParameters: tuning with { InnerRimBoost = 0 });
        var inner = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 24,
            debugView: LiquidGlassDebugView.InnerOnly, samplingParameters: tuning);

        var top = (2 * width + width / 2) * 4;
        Assert.True(baseline[top] > source[top], $"Base refraction should move inward: {source[top]} -> {baseline[top]}.");
        Assert.True(inner[top] > baseline[top], $"Inner Rim should increase the physical edge displacement: {baseline[top]} -> {inner[top]}.");
    }

    [Fact]
    public void OuterOnlySamplesTheOppositeSideOfTheSilhouetteFromItsPaddedCapture()
    {
        const int width = 200, height = 106;
        var glassBounds = new Rect(0, 3, width, 100);
        var source = Frame(width, height, (_, y) => (byte)(y * 16));
        var tuning = LiquidGlassSamplingParameters.Default with { OuterRimStrength = .75, OuterRimWidthDip = 5 };
        var output = new LiquidGlassOptics().Render(source, width, height, new Size(width, height), 24,
            debugView: LiquidGlassDebugView.OuterOnly, samplingParameters: tuning, glassBoundsDip: glassBounds);

        var topBevel = (3 * width + width / 2) * 4;
        Assert.Equal(255, output[topBevel + 3]);
        Assert.True(output[topBevel] < source[topBevel],
            $"Outer Rim should sample outward past the glass edge: {source[topBevel]} -> {output[topBevel]}.");
    }

    [Fact]
    public void Chapter06WeightDebugViewsFormNormalizedOuterInnerFaceBandsFromGeometry()
    {
        const int width = 160, height = 100;
        var size = new Size(width, height);
        var source = Frame(width, height, (_, _) => 80);
        var combinedMode = Enum.Parse<LiquidGlassDebugView>("CombinedWeightsPreview");
        var combined = new LiquidGlassOptics().Render(source, width, height, size, 22,
            debugView: combinedMode);
        var face = new LiquidGlassOptics().Render(source, width, height, size, 22,
            debugView: Enum.Parse<LiquidGlassDebugView>("FaceWeightOnly"));
        var inner = new LiquidGlassOptics().Render(source, width, height, size, 22,
            debugView: Enum.Parse<LiquidGlassDebugView>("InnerWeightOnly"));
        var outer = new LiquidGlassOptics().Render(source, width, height, size, 22,
            debugView: Enum.Parse<LiquidGlassDebugView>("OuterWeightOnly"));

        byte Weight(byte[] image, int x, int y, int channel) => image[(y * width + x) * 4 + channel];
        var edge = (width / 2, 0);
        var innerBand = (width / 2, 4);
        var center = (width / 2, height / 2);
        Assert.True(Weight(outer, edge.Item1, edge.Item2, 0) > Weight(inner, edge.Item1, edge.Item2, 0));
        Assert.True(Weight(inner, innerBand.Item1, innerBand.Item2, 0) > Weight(outer, innerBand.Item1, innerBand.Item2, 0));
        Assert.True(Weight(face, center.Item1, center.Item2, 0) >= 250);
        Assert.Equal(0, combined[3]);

        for (var y = 1; y < height - 1; y++)
        {
            var pixel = (y * width + width / 2) * 4;
            var sum = combined[pixel] + combined[pixel + 1] + combined[pixel + 2];
            Assert.InRange(sum, 252, 258);
            var previous = ((y - 1) * width + width / 2) * 4;
            Assert.InRange(Math.Abs(combined[pixel + 1] - combined[previous + 1]), 0, 120);
        }

        var changedBackdrop = Frame(width, height, (_, _) => 210);
        var changedWeights = new LiquidGlassOptics().Render(changedBackdrop, width, height, size, 22,
            debugView: combinedMode);
        Assert.Equal(combined, changedWeights);
    }

    [Fact]
    public void Chapter06FinalMixedSamplingKeepsStableCenterAndGlassAlpha()
    {
        const int width = 180, height = 100;
        var source = Frame(width, height, (x, y) => (byte)((x * 7 + y * 3) % 256));
        var tuning = LiquidGlassSamplingParameters.Default with
        {
            FaceOpticalCenterScale = 0,
            InnerRimBoost = 0,
            OuterRimStrength = 0,
        };
        var output = new LiquidGlassOptics().Render(source, width, height,
            new Size(width, height), 24, refractionStrength: 0,
            debugView: Enum.Parse<LiquidGlassDebugView>("FinalMixedSampling"),
            samplingParameters: tuning);

        var center = (50 * width + 90) * 4;
        Assert.Equal(source[center], output[center]);
        Assert.Equal(source[center + 1], output[center + 1]);
        Assert.Equal(source[center + 2], output[center + 2]);
        Assert.Equal(255, output[center + 3]);
        Assert.Equal(0, output[3]);
    }

    [Fact]
    public void Chapter06WeightParametersAreGroupedAndBounded()
    {
        var property = typeof(LiquidGlassDebugState).GetProperty("WeightParameters");
        Assert.NotNull(property);
        Assert.True(property!.CanRead && property.CanWrite);
        foreach (var name in new[]
        {
            "OuterBandWidthDip", "FaceTransitionStartDip", "FaceTransitionWidthDip",
            "AntiAliasScale", "FaceWeightGain", "InnerWeightGain", "OuterWeightGain", "HeightBevelInfluence",
        })
            Assert.NotNull(property.PropertyType.GetProperty(name));

        var parametersType = property.PropertyType;
        var defaults = parametersType.GetProperty("Default")!.GetValue(null)!;
        Assert.NotNull(defaults);
        var normalize = parametersType.GetMethod("Normalize")!;
        var unbounded = Activator.CreateInstance(parametersType,
            double.NaN, double.PositiveInfinity, 0d, 99d, -2d, 99d, 0d, double.PositiveInfinity)!;
        var normalized = normalize.Invoke(unbounded, null)!;
        foreach (var name in new[]
        {
            "OuterBandWidthDip", "FaceTransitionStartDip", "FaceTransitionWidthDip",
            "AntiAliasScale", "FaceWeightGain", "InnerWeightGain", "OuterWeightGain", "HeightBevelInfluence",
        })
        {
            var value = (double)parametersType.GetProperty(name)!.GetValue(normalized)!;
            var minimum = (double)parametersType.GetField("Minimum" + name)!.GetRawConstantValue()!;
            var maximum = (double)parametersType.GetField("Maximum" + name)!.GetRawConstantValue()!;
            Assert.InRange(value, minimum, maximum);
        }
        Assert.NotNull(defaults);
    }

    [Fact]
    public void Chapter07ExposesIndependentLodComparisonModesAndLiveParameters()
    {
        foreach (var name in new[]
        {
            "LodOff", "FaceLodOnly", "InnerLodOnly", "OuterLodOnly", "FinalMixedWithLod",
        })
            Assert.True(Enum.TryParse(name, out LiquidGlassDebugView view) && Enum.IsDefined(view),
                $"Missing liquid-glass LOD comparison mode {name}.");

        var property = typeof(LiquidGlassDebugState).GetProperty("LodParameters");
        Assert.NotNull(property);
        Assert.True(property!.CanRead && property.CanWrite);
        foreach (var name in new[] { "FaceFootprintDip", "InnerFootprintDip", "OuterFootprintDip" })
            Assert.NotNull(property.PropertyType.GetProperty(name));

        var defaults = property.PropertyType.GetProperty("Default")!.GetValue(null)!;
        var normalized = property.PropertyType.GetMethod("Normalize")!.Invoke(
            Activator.CreateInstance(property.PropertyType, double.PositiveInfinity, 8d, -2d), null)!;
        double Read(object value, string name) => (double)property.PropertyType.GetProperty(name)!.GetValue(value)!;
        Assert.Equal(Read(defaults, "FaceFootprintDip"), Read(normalized, "FaceFootprintDip"));
        Assert.Equal(0, Read(normalized, "OuterFootprintDip"));
        Assert.True(Read(normalized, "InnerFootprintDip") > Read(defaults, "InnerFootprintDip"));
        Assert.True(Read(defaults, "FaceFootprintDip") > Read(defaults, "InnerFootprintDip"));
        Assert.True(Read(defaults, "OuterFootprintDip") > Read(defaults, "FaceFootprintDip"));
    }

    [Fact]
    public void Chapter07LodOffPreservesTheExistingMixedResultAndEachLodModeChangesItsChannel()
    {
        const int width = 160, height = 100;
        var source = Frame(width, height, (x, y) => (x + y) % 2 == 0 ? (byte)230 : (byte)25);
        byte[] Render(string view, double face, double inner, double outer) => RenderWithLod(
            source, width, height, new Size(width, height), 22, view, face, inner, outer);

        var oldMixed = Render("FinalMixedSampling", .85, .35, 1.1);
        var lodOff = Render("LodOff", .85, .35, 1.1);
        Assert.Equal(oldMixed, lodOff);

        var noLod = Render("LodOff", 0, 0, 0);
        var face = Render("FaceLodOnly", .85, 0, 0);
        var inner = Render("InnerLodOnly", 0, .55, 0);
        var outer = Render("OuterLodOnly", 0, 0, 1.1);
        Assert.False(noLod.AsSpan().SequenceEqual(face));
        Assert.False(noLod.AsSpan().SequenceEqual(inner));
        Assert.False(noLod.AsSpan().SequenceEqual(outer));

        var final = Render("FinalMixedWithLod", .85, .35, 1.1);
        var normal = Render("None", .85, .35, 1.1);
        Assert.Equal(normal, final);
        Assert.Equal(noLod.Where((_, index) => index % 4 == 3), face.Where((_, index) => index % 4 == 3));
        Assert.Equal(noLod.Where((_, index) => index % 4 == 3), inner.Where((_, index) => index % 4 == 3));
        Assert.Equal(noLod.Where((_, index) => index % 4 == 3), outer.Where((_, index) => index % 4 == 3));
    }

    [Fact]
    public void Chapter07LodFootprintIsStableAcrossRenderTargetScalesAndKeepsUniformColorClear()
    {
        const int widthDip = 120, heightDip = 90;
        byte[] MakeFrame(int width, int height)
        {
            var scale = width / (double)widthDip;
            return Frame(width, height, (x, _) =>
                (byte)Math.Round(30 + (x + .5) / scale * 200 / widthDip));
        }

        var low = RenderWithLod(MakeFrame(widthDip, heightDip), widthDip, heightDip,
            new Size(widthDip, heightDip), 22, "FinalMixedWithLod", .4, 0, 0);
        const double scale = 1.5;
        var highWidth = (int)(widthDip * scale);
        var highHeight = (int)(heightDip * scale);
        var high = RenderWithLod(MakeFrame(highWidth, highHeight), highWidth, highHeight,
            new Size(widthDip, heightDip), 22, "FinalMixedWithLod", .4, 0, 0);

        var worstDifference = 0.0;
        for (var xDip = 30.0; xDip <= 90; xDip += .5)
            worstDifference = Math.Max(worstDifference,
                Math.Abs(SampleAtDip(low, widthDip, heightDip, widthDip, heightDip, xDip, heightDip / 2.0)
                    - SampleAtDip(high, highWidth, highHeight, widthDip, heightDip, xDip, heightDip / 2.0)));
        Assert.InRange(worstDifference, 0, 3);

        var uniform = Frame(widthDip, heightDip, (_, _) => 73);
        var clear = RenderWithLod(uniform, widthDip, heightDip, new Size(widthDip, heightDip),
            22, "FinalMixedWithLod", 1.2, 1.2, 1.2);
        for (var i = 0; i < clear.Length; i += 4)
        {
            if (clear[i + 3] == 0) continue;
            Assert.Equal(73, clear[i]);
            Assert.Equal(73, clear[i + 1]);
            Assert.Equal(73, clear[i + 2]);
            Assert.Equal(255, clear[i + 3]);
        }
    }

    [Fact]
    public void Chapter07FaceLodSoftensAContourLocallyAndPreservesDistantCenterContrast()
    {
        const int width = 160, height = 100;
        var source = Frame(width, height, (x, _) => x < width / 2 ? (byte)24 : (byte)232);
        var off = RenderWithLod(source, width, height, new Size(width, height), 22, "LodOff", .85, .1, .75);
        var face = RenderWithLod(source, width, height, new Size(width, height), 22, "FaceLodOnly", .85, .1, .75);
        var row = height / 2;
        int Red(byte[] image, int x) => image[(row * width + x) * 4 + 2];

        var offEdgeContrast = Math.Abs(Red(off, 79) - Red(off, 80));
        var faceEdgeContrast = Math.Abs(Red(face, 79) - Red(face, 80));
        var distantCenterContrast = Math.Abs(Red(face, 65) - Red(face, 95));
        Assert.True(faceEdgeContrast < offEdgeContrast);
        Assert.InRange(distantCenterContrast, 180, 210);
    }

    private static byte[] RenderWithLod(byte[] source, int width, int height, Size sizeDip,
        double cornerRadiusDip, string debugView, double face, double inner, double outer)
    {
        var parameterProperty = typeof(LiquidGlassDebugState).GetProperty("LodParameters");
        Assert.NotNull(parameterProperty);
        var parameterType = parameterProperty!.PropertyType;
        var tuning = Activator.CreateInstance(parameterType, face, inner, outer)!;
        var render = typeof(LiquidGlassOptics).GetMethod(nameof(LiquidGlassOptics.Render));
        Assert.NotNull(render);
        var parameters = render!.GetParameters();
        var arguments = parameters.Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
        arguments[0] = source;
        arguments[1] = width;
        arguments[2] = height;
        arguments[3] = sizeDip;
        arguments[4] = cornerRadiusDip;
        arguments[Array.FindIndex(parameters, parameter => parameter.Name == "refractionStrength")] = 0d;
        arguments[Array.FindIndex(parameters, parameter => parameter.Name == "debugView")] = Enum.Parse<LiquidGlassDebugView>(debugView);
        arguments[Array.FindIndex(parameters, parameter => parameter.Name == "lodParameters")] = tuning;
        // These tests establish the Chapter 07 baseline. Keep the new Chapter 10
        // default out of that historical comparison unless a test opts into it.
        arguments[Array.FindIndex(parameters, parameter => parameter.Name == "dispersionParameters")]
            = LiquidGlassDispersionParameters.Default with { DispersionStrength = 0 };
        return Assert.IsType<byte[]>(render.Invoke(new LiquidGlassOptics(), arguments));
    }

    private static double SampleAtDip(byte[] image, int width, int height, int widthDip, int heightDip,
        double xDip, double yDip)
    {
        var x = Math.Clamp(xDip * width / widthDip - .5, 0, width - 1);
        var y = Math.Clamp(yDip * height / heightDip - .5, 0, height - 1);
        var x0 = (int)x; var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, width - 1); var y1 = Math.Min(y0 + 1, height - 1);
        var fx = x - x0; var fy = y - y0;
        double At(int px, int py) => image[(py * width + px) * 4];
        return (At(x0, y0) * (1 - fx) + At(x1, y0) * fx) * (1 - fy)
            + (At(x0, y1) * (1 - fx) + At(x1, y1) * fx) * fy;
    }

    [Theory]
    [InlineData(180)]
    [InlineData(640)]
    public void RefractionUvDirectionAndAmountRemainStableAcrossDpiAndOppositeEdges(int widthDip)
    {
        const int heightDip = 96;
        var size = new Size(widthDip, heightDip);
        var low = new LiquidGlassOptics().Render(Frame(widthDip, heightDip, (_, _) => 128),
            widthDip, heightDip, size, 36, refractionStrength: .8,
            debugView: LiquidGlassDebugView.RefractionUvOffset);
        const double dpiScale = 1.5;
        var highWidth = (int)(widthDip * dpiScale);
        var highHeight = (int)(heightDip * dpiScale);
        var high = new LiquidGlassOptics().Render(Frame(highWidth, highHeight, (_, _) => 128),
            highWidth, highHeight, size, 36, refractionStrength: .8,
            debugView: LiquidGlassDebugView.RefractionUvOffset);

        Vector OffsetAt(byte[] image, int width, int x, int y)
        {
            var index = (y * width + x) * 4;
            return new Vector((image[index + 2] - 128) * (4.0 / 127),
                (image[index + 1] - 128) * (4.0 / 127));
        }

        Vector AtScale(byte[] image, int imageWidth, int x, int y, double scale) =>
            OffsetAt(image, imageWidth,
                Math.Clamp((int)Math.Round((x + .5) * scale - .5), 0, imageWidth - 1),
                Math.Clamp((int)Math.Round((y + .5) * scale - .5), 0, highHeight - 1));

        var center = OffsetAt(low, widthDip, widthDip / 2, heightDip / 2);
        var top = OffsetAt(low, widthDip, widthDip / 2, 4);
        var bottom = OffsetAt(low, widthDip, widthDip / 2, heightDip - 5);
        var left = OffsetAt(low, widthDip, 4, heightDip / 2);
        var right = OffsetAt(low, widthDip, widthDip - 5, heightDip / 2);
        Assert.InRange(center.Length, 0, .08);
        Assert.True(Math.Abs(top.Y) > .1, $"Top bevel displacement was {top} DIP for width {widthDip}.");
        Assert.True(top.Y * bottom.Y < 0, $"Opposite vertical edges bent the same way: top={top}, bottom={bottom}.");
        Assert.True(left.X * right.X < 0, $"Opposite horizontal edges bent the same way: left={left}, right={right}.");

        foreach (var (x, y) in new[]
        {
            (widthDip / 2, 4),
            (widthDip / 2, heightDip - 5),
            (4, heightDip / 2),
            (widthDip - 5, heightDip / 2),
        })
        {
            var lowOffset = OffsetAt(low, widthDip, x, y);
            var highOffset = AtScale(high, highWidth, x, y, dpiScale);
            Assert.InRange((lowOffset - highOffset).Length, 0, .4);
        }
    }

    private static byte[] Frame(int width, int height, Func<int, int, byte> brightness)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = brightness(x, y);
            pixels[index + 3] = 255;
        }
        return pixels;
    }

}
