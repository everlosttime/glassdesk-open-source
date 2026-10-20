using System.Windows;
using GlassDesk.UI.Controls;
using GlassDesk.Core.Models;

namespace GlassDesk.Dock.Tests;

public sealed class AndroidLiquidGlassTests
{
    [Fact]
    public void FullyClearLaunchpadRemovesResidualVeilButKeepsDockAndDefaultBody()
    {
        var source = Uniform(100, 100, 100);
        var renderer = new AndroidLiquidGlassRenderer();
        byte[] Draw(bool launchpad, double clarity) => renderer.Render(source, 128, 96, new Size(128, 96),
            new Rect(8, 8, 112, 80), 24, true, true, 1, backgroundClarity: clarity, launchpadOptics: launchpad);
        var center = (48 * 128 + 64) * 4;
        Assert.Equal(87, Draw(false, 1)[center]);
        Assert.Equal(87, Draw(true, .5)[center]);
        Assert.Equal(100, Draw(true, 1)[center]);
        var moving = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, 128, 96,
            new Size(128, 96), true, 1, backgroundClarity: 1, launchpadOptics: true);
        Assert.Equal(100, moving[center]);
        Assert.Equal(255, moving[center + 3]);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LargeLaunchpadLensStretchesBothAxesBeyondTheNarrowDockBand(bool horizontal)
    {
        const int width = 300, height = 200;
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            source[i] = source[i + 1] = source[i + 2] = (byte)((horizontal ? x : y) * .7);
            source[i + 3] = 255;
        }
        var renderer = new AndroidLiquidGlassRenderer();
        byte[] Draw(bool launchpad) => renderer.Render(source, width, height, new Size(600, 400),
            new Rect(8, 8, 584, 384), 70, true, true, 1, tintOpacity: 0, backgroundClarity: 1, launchpadOptics: launchpad);
        var dock = Draw(false);
        var launcher = Draw(true);
        var point = horizontal ? (100 * width + 14) * 4 : (14 * width + 150) * 4;
        Assert.True(launcher[point + 1] > dock[point + 1] + 3);
        var center = (100 * width + 150) * 4;
        Assert.Equal(dock[center..(center + 4)], launcher[center..(center + 4)]);
        for (var i = 3; i < source.Length; i += 4) Assert.Equal(dock[i], launcher[i]);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(.5)]
    [InlineData(1)]
    public void DispersionIsSubtleAtRefractingEdgesAndAbsentInFlatCenter(double clarity)
    {
        var source = Uniform(0, 0, 0);
        for (var y = 0; y < 96; y++)
        for (var x = 0; x < 128; x++)
        {
            var i = (y * 128 + x) * 4;
            source[i] = source[i + 1] = source[i + 2] = (byte)(x * 2);
        }
        var renderer = new AndroidLiquidGlassRenderer();
        byte[] Draw(LiquidGlassDispersionParameters dispersion) => renderer.Render(source, 128, 96,
            new Size(128, 96), new Rect(8, 8, 112, 80), 24, true, true, 1,
            backgroundClarity: clarity, dispersionParameters: dispersion);
        var enabled = Draw(LiquidGlassDispersionParameters.Default);
        var disabled = Draw(LiquidGlassDispersionParameters.Default with { DispersionStrength = 0 });
        var edge = (48 * 128 + 10) * 4;
        var center = (48 * 128 + 64) * 4;
        Assert.InRange(Math.Abs(enabled[edge] - enabled[edge + 2]), 1, 5);
        Assert.Equal(enabled[center], enabled[center + 2]);
        Assert.Equal(disabled[center..(center + 4)], enabled[center..(center + 4)]);
        for (var i = 0; i < enabled.Length; i += 4)
        {
            Assert.Equal(disabled[i + 1], enabled[i + 1]); // Green keeps the original lens.
            Assert.Equal(disabled[i + 3], enabled[i + 3]); // Coverage never splits.
        }
    }
    [Fact]
    public void ClearBackgroundPreservesFineDetailWithoutChangingDefaultMaterialOrLens()
    {
        var source = Uniform(100, 100, 100);
        for (var y = 0; y < 96; y++)
        for (var x = 0; x < 128; x++)
        {
            var i = (y * 128 + x) * 4;
            source[i] = source[i + 1] = source[i + 2] = (byte)(x % 2 == 0 ? 30 : 200);
        }
        var renderer = new AndroidLiquidGlassRenderer();
        byte[] Draw(double clarity) => renderer.Render(source, 128, 96, new Size(128, 96),
            new Rect(8, 8, 112, 80), 24, true, true, 1, backgroundClarity: clarity);
        var baseline = Draw(.5);
        var clear = Draw(1);
        var soft = Draw(0);
        var center = (48 * 128 + 64) * 4;
        Assert.True(Math.Abs(clear[center] - clear[center + 4]) > 100);
        Assert.InRange(Math.Abs(baseline[center] - baseline[center + 4]), 0, 3);
        Assert.InRange(Math.Abs(soft[center] - soft[center + 4]), 0, 3);
        Assert.Equal(renderer.Render(source, 128, 96, new Size(128, 96),
            new Rect(8, 8, 112, 80), 24, true, true, 1), baseline);
        var uniform = Uniform(100, 100, 100);
        byte[] Flat(double clarity) => renderer.Render(uniform, 128, 96, new Size(128, 96),
            new Rect(8, 8, 112, 80), 24, true, true, 1, backgroundClarity: clarity);
        Assert.Equal(Flat(.5), Flat(1)); // Tint, edge light and shade must be byte-identical.
        for (var i = 3; i < clear.Length; i += 4) Assert.Equal(baseline[i], clear[i]);
        var motion = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, 128, 96,
            new Size(128, 96), true, 1, backgroundClarity: 1);
        Assert.True(Math.Abs(motion[center] - motion[center + 4]) > 100);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(.16)]
    [InlineData(.65)]
    [InlineData(1)]
    public void BodyTintLeavesCoverageAndEdgeLightingIndependent(double opacity)
    {
        var renderer = new AndroidLiquidGlassRenderer();
        byte[] Draw(byte value) => renderer.Render(Uniform(value, value, value), 128, 96,
            new Size(128, 96), new Rect(8, 8, 112, 80), 24, true, true, 1, opacity);
        var black = Draw(0);
        var white = Draw(255);
        var center = (48 * 128 + 64) * 4;
        var top = (11 * 128 + 64) * 4;
        Assert.InRange(black[center], (byte)Math.Max(0, 18 * opacity - 1), (byte)Math.Min(255, 18 * opacity + 1));
        Assert.True(black[top] > black[center] + 8, "The bevel remains bright even at zero body tint.");
        for (var i = 3; i < black.Length; i += 4) Assert.Equal(white[i], black[i]);
    }

    [Fact]
    public void FullyClearBodyStillRefractsBackgroundAndMotionTextureKeepsAlpha()
    {
        var source = Uniform(0, 0, 0);
        for (var y = 0; y < 96; y++)
        for (var x = 0; x < 128; x++)
        {
            var i = (y * 128 + x) * 4;
            source[i] = source[i + 1] = source[i + 2] = (byte)(x * 1.5);
        }
        var clear = new AndroidLiquidGlassRenderer().Render(source, 128, 96, new Size(128, 96),
            new Rect(8, 8, 112, 80), 24, true, true, 1, 0);
        Assert.InRange(clear[(48 * 128 + 13) * 4], (byte)39, (byte)45);
        var motion = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, 128, 96, new Size(128, 96), true, 1, 0);
        var veiled = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, 128, 96, new Size(128, 96), true, 1, 1);
        Assert.NotEqual(motion[centerIndex()], veiled[centerIndex()]);
        for (var i = 3; i < motion.Length; i += 4) Assert.Equal(motion[i], veiled[i]);
        static int centerIndex() => (48 * 128 + 64) * 4;
    }
    [Theory]
    [InlineData(false, 124)]
    [InlineData(true, 87)]
    public void TransitionBackdropKeepsMaterialButContainsNoBakedContainerBoundary(bool dark, byte expected)
    {
        var input = Uniform(100, 100, 100);
        var frame = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(input, 128, 96, new Size(128, 96), dark, 1);
        for (var i = 0; i < frame.Length; i += 4)
        {
            Assert.Equal(expected, frame[i]);
            Assert.Equal(expected, frame[i + 1]);
            Assert.Equal(expected, frame[i + 2]);
            Assert.Equal(255, frame[i + 3]);
        }
        Assert.Equal(100, input[0]);
    }

    [Fact]
    public void ProductionComposerSelectsNewMaterialAndKeepsLegacyRollbackExact()
    {
        var source = Uniform(100, 100, 100);
        var optics = new LiquidGlassOptics();
        byte[] Compose(GlassMaterialMode material, LiquidGlassDebugView view = LiquidGlassDebugView.None) =>
            LiquidGlassFrameComposer.Compose(optics, source, 128, 96, new Size(128, 96), 24,
                true, 1, 0, view, LiquidGlassSamplingParameters.Default,
                LiquidGlassWeightParameters.Default, new Rect(8, 8, 112, 80), LiquidGlassLodParameters.Default,
                LiquidGlassFinishingParameters.Default, LiquidGlassBrdfParameters.Default,
                LiquidGlassDispersionParameters.Default, material: material);
        var legacy = Compose(GlassMaterialMode.PhysicalGlass);
        var newFrame = Compose(GlassMaterialMode.AndroidLiquidGlass);
        Assert.Equal(87, newFrame[(48 * 128 + 64) * 4]);
        Assert.NotEqual(legacy, newFrame);
        Assert.Equal(legacy, Compose(GlassMaterialMode.PhysicalGlass));
        // Diagnostic views must always retain the original optical path.
        var originalDiagnostic = Compose(GlassMaterialMode.PhysicalGlass, LiquidGlassDebugView.Normal);
        Assert.Equal(originalDiagnostic, Compose(GlassMaterialMode.AndroidLiquidGlass, LiquidGlassDebugView.Normal));
    }

    [Theory]
    [InlineData(0, -24)]
    [InlineData(12, -3.215390309173472)]
    [InlineData(24, 0)]
    [InlineData(40, 0)]
    public void LensUsesReferenceCircularProfile(double depth, double expected)
    {
        var actual = AndroidLiquidGlassRenderer.LensOffset(depth, 24, 24);
        Assert.Equal(expected, actual, 8);
    }

    [Theory]
    [InlineData(false, 124)]
    [InlineData(true, 87)]
    public void UniformCenterUsesClearSurfaceTint(bool dark, byte expected)
    {
        var input = Uniform(100, 100, 100);
        var frame = Render(input, dark);
        var i = (48 * 128 + 64) * 4;
        Assert.InRange(frame[i], (byte)(expected - 1), (byte)(expected + 1));
        Assert.Equal(frame[i], frame[i + 1]);
        Assert.Equal(frame[i], frame[i + 2]);
        Assert.Equal(255, frame[i + 3]);
        Assert.Equal(100, input[i]);
    }

    [Fact]
    public void ZeroStrengthPreservesInsidePixelsAndClearsOutsideAlpha()
    {
        var input = Uniform(43, 82, 171);
        var original = input.ToArray();
        var frame = Render(input, false, 0);
        Assert.Equal(original, input);
        var i = (48 * 128 + 64) * 4;
        Assert.Equal(input[i..(i + 4)], frame[i..(i + 4)]);
        Assert.Equal(0, frame[3]);
    }

    [Fact]
    public void BackgroundDetailIsFilteredWithoutNeutralizingColorFields()
    {
        var input = Uniform(10, 90, 180);
        for (var y = 20; y < 76; y++)
        for (var x = 30; x < 98; x++)
        {
            var i = (y * 128 + x) * 4;
            input[i] = input[i + 1] = input[i + 2] = (byte)(x % 2 == 0 ? 0 : 255);
        }
        var frame = Render(input, false);
        var even = (48 * 128 + 64) * 4;
        var odd = even + 4;
        Assert.InRange(Math.Abs(frame[even] - frame[odd]), 0, 3);
        var color = (48 * 128 + 20) * 4;
        Assert.True(frame[color + 2] > frame[color] + 30);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void LensMappingRemainsStableInDipsAcrossDpi(double scale)
    {
        var width = (int)(128 * scale);
        var height = (int)(96 * scale);
        var input = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            input[i] = input[i + 1] = input[i + 2] = (byte)(x / scale * 1.5);
            input[i + 3] = 255;
        }
        var frame = Render(input, true, 1, width, height);
        var point = ((int)(48 * scale) * width + (int)(13 * scale)) * 4;
        // The deeper lens moves the ramp about 12 DIP; the clear .16 dark veil
        // gives roughly (13+12)*1.5*.84 + 18*.16 plus 3 bevel-light channel units.
        Assert.InRange(frame[point], (byte)36, (byte)40);
    }

    [Fact]
    public void ClearCenterPreservesBroadBackgroundContrast()
    {
        var input = Uniform(100, 100, 100);
        for (var y = 0; y < 96; y++)
        for (var x = 0; x < 128; x++)
        {
            var i = (y * 128 + x) * 4;
            input[i] = input[i + 1] = input[i + 2] = (byte)((x / 16) % 2 == 0 ? 0 : 255);
        }
        var pixels = Render(input, false);
        Assert.True(Math.Abs(pixels[(48 * 128 + 56) * 4] - pixels[(48 * 128 + 72) * 4]) > 100);
    }

    [Fact]
    public void BevelHasLightAndDarkSidesWhileCenterStaysClear()
    {
        var pixels = Render(Uniform(100, 100, 100), false);
        var center = pixels[(48 * 128 + 64) * 4];
        Assert.True(pixels[(11 * 128 + 64) * 4] > center + 8);
        Assert.True(pixels[(84 * 128 + 64) * 4] < center - 5);
        Assert.Equal(255, pixels[(11 * 128 + 64) * 4 + 3]);
    }

    private static byte[] Render(byte[] input, bool dark, double strength = 1, int width = 128, int height = 96)
        => new AndroidLiquidGlassRenderer().Render(input, width, height, new Size(128, 96),
            new Rect(8, 8, 112, 80), 24, true, dark, strength);

    private static byte[] Uniform(byte b, byte g, byte r)
    {
        var pixels = new byte[128 * 96 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
        return pixels;
    }
}
