using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassCompositionOrderTests
{
    [Fact]
    public void ProductionComposerAppliesTintAndBrdfBeforeTheExistingDispersionPass()
    {
        const int width = 128;
        const int height = 88;
        var size = new Size(width, height);
        var bounds = new Rect(18, 14, 92, 60);
        var source = CreateHighContrastBackground(width, height);
        var sampling = LiquidGlassSamplingParameters.Default;
        var weights = LiquidGlassWeightParameters.Default;
        var lod = LiquidGlassLodParameters.Default;
        var finishing = LiquidGlassFinishingParameters.Default;
        var brdf = LiquidGlassBrdfParameters.Default;
        var dispersion = LiquidGlassDispersionParameters.Default;
        var optics = new LiquidGlassOptics();

        var expected = optics.Render(source, width, height, size, 24, squircle: true,
            debugView: LiquidGlassDebugView.DispersionOff,
            samplingParameters: sampling, weightParameters: weights,
            glassBoundsDip: bounds, lodParameters: lod,
            dispersionParameters: dispersion);
        expected = LiquidGlassFinishComposer.ApplyTint(expected, finishing);
        expected = optics.ApplyBrdf(expected, size, weights, brdf, LiquidGlassDebugView.None);
        expected = optics.ApplyDispersion(source, expected, sampling, weights, lod, dispersion,
            LiquidGlassDebugView.None);

        var actual = LiquidGlassFrameComposer.Compose(optics, source, width, height, size,
            cornerRadiusDip: 24, squircle: true, refractionStrength: 1, detailSoftnessDip: 0,
            LiquidGlassDebugView.None, sampling, weights, bounds, lod, finishing, brdf, dispersion);

        Assert.Equal(expected, actual);
        Assert.Equal(ReadAlpha(expected), ReadAlpha(actual));
    }

    [Fact]
    public void DispersionOffAndZeroStrengthKeepTheFinishedChapter09Pixels()
    {
        const int width = 96;
        const int height = 72;
        var size = new Size(width, height);
        var bounds = new Rect(14, 12, 68, 48);
        var source = CreateHighContrastBackground(width, height);
        var finishing = LiquidGlassFinishingParameters.Default;
        var weights = LiquidGlassWeightParameters.Default;
        var brdf = LiquidGlassBrdfParameters.Default;
        var optics = new LiquidGlassOptics();
        var zeroDispersion = LiquidGlassDispersionParameters.Default with { DispersionStrength = 0 };
        var expected = optics.Render(source, width, height, size, 22, squircle: true,
            debugView: LiquidGlassDebugView.DispersionOff,
            glassBoundsDip: bounds, dispersionParameters: zeroDispersion);
        expected = LiquidGlassFinishComposer.ApplyTint(expected, finishing);
        expected = optics.ApplyBrdf(expected, size, weights, brdf, LiquidGlassDebugView.None);

        var off = LiquidGlassFrameComposer.Compose(optics, source, width, height, size,
            cornerRadiusDip: 22, squircle: true, refractionStrength: 1, detailSoftnessDip: 0,
            LiquidGlassDebugView.DispersionOff, LiquidGlassSamplingParameters.Default,
            weights, bounds, LiquidGlassLodParameters.Default, finishing, brdf,
            LiquidGlassDispersionParameters.Default);
        var zero = LiquidGlassFrameComposer.Compose(optics, source, width, height, size,
            cornerRadiusDip: 22, squircle: true, refractionStrength: 1, detailSoftnessDip: 0,
            LiquidGlassDebugView.None, LiquidGlassSamplingParameters.Default,
            weights, bounds, LiquidGlassLodParameters.Default, finishing, brdf, zeroDispersion);

        Assert.Equal(expected, off);
        Assert.Equal(expected, zero);
    }

    private static byte[] CreateHighContrastBackground(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(((x / 5 + y / 7) % 2 == 0) ? 18 : 238);
            var index = (y * width + x) * 4;
            pixels[index] = value;
            pixels[index + 1] = (byte)(255 - value);
            pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static byte[] ReadAlpha(byte[] bgra)
    {
        var alpha = new byte[bgra.Length / 4];
        for (var pixel = 0; pixel < alpha.Length; pixel++) alpha[pixel] = bgra[pixel * 4 + 3];
        return alpha;
    }
}
