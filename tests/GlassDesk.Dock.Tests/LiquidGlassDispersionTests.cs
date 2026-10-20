using System.Numerics;
using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LiquidGlassDispersionTests
{
    [Fact]
    public void DefaultsAreConservativeAndNormalizationBoundsEveryControl()
    {
        var defaults = LiquidGlassDispersionParameters.Default;

        Assert.Equal(0, defaults.FaceDispersionGain);
        Assert.InRange(defaults.DispersionStrength, 0, .004);
        Assert.InRange(defaults.InnerDispersionGain, 0, .5);
        Assert.InRange(defaults.OuterDispersionGain, defaults.InnerDispersionGain, .65);

        var normalized = (defaults with
        {
            DispersionStrength = double.NaN,
            FaceDispersionGain = 9,
            InnerDispersionGain = -1,
            OuterDispersionGain = double.PositiveInfinity,
        }).Normalize();

        Assert.Equal(defaults.DispersionStrength, normalized.DispersionStrength);
        Assert.Equal(LiquidGlassDispersionParameters.MaximumFaceDispersionGain,
            normalized.FaceDispersionGain);
        Assert.Equal(LiquidGlassDispersionParameters.MinimumInnerDispersionGain,
            normalized.InnerDispersionGain);
        Assert.Equal(defaults.OuterDispersionGain, normalized.OuterDispersionGain);
    }

    [Fact]
    public void RedGreenAndBlueUseTheConfiguredIorOffsetsAndDistinctDualInterfacePaths()
    {
        const double centerIor = 1.46;
        const double dispersion = .004;
        var redIor = LiquidGlassDispersion.ChannelIor(centerIor, dispersion, LiquidGlassDispersionChannel.Red);
        var greenIor = LiquidGlassDispersion.ChannelIor(centerIor, dispersion, LiquidGlassDispersionChannel.Green);
        var blueIor = LiquidGlassDispersion.ChannelIor(centerIor, dispersion, LiquidGlassDispersionChannel.Blue);

        Assert.Equal(centerIor - dispersion, redIor, 12);
        Assert.Equal(centerIor, greenIor, 12);
        Assert.Equal(centerIor + dispersion, blueIor, 12);

        var normal = Vector3.Normalize(new Vector3(.52f, -.31f, 1));
        Assert.True(GlassRayTracer.TryTrace(new Point(60, 60), normal, 4, 8, redIor, out var red));
        Assert.True(GlassRayTracer.TryTrace(new Point(60, 60), normal, 4, 8, greenIor, out var green));
        Assert.True(GlassRayTracer.TryTrace(new Point(60, 60), normal, 4, 8, blueIor, out var blue));
        Assert.NotEqual(red, green);
        Assert.NotEqual(green, blue);
    }

    [Fact]
    public void DispersionOffAndZeroStrengthAreByteIdenticalToChapter09Optics()
    {
        const int width = 180;
        const int height = 120;
        var size = new Size(width, height);
        var source = CreateVerticalLinePattern(width, height);
        var optics = new LiquidGlassOptics();
        var zeroParameters = LiquidGlassDispersionParameters.Default with { DispersionStrength = 0 };
        var chapter09 = optics.Render(source, width, height, size, 26, squircle: true,
            dispersionParameters: zeroParameters);

        var dispersionOff = optics.Render(source, width, height, size, 26, squircle: true,
            debugView: LiquidGlassDebugView.DispersionOff,
            dispersionParameters: LiquidGlassDispersionParameters.Default);
        var zeroStrength = optics.Render(source, width, height, size, 26, squircle: true,
            debugView: LiquidGlassDebugView.FinalWithDispersion,
            dispersionParameters: zeroParameters);

        Assert.Equal(chapter09, dispersionOff);
        Assert.Equal(chapter09, zeroStrength);
    }

    [Fact]
    public void RayUvDiagnosticsSeparateRedGreenAndBlueOnHighContrastLines()
    {
        const int width = 220;
        const int height = 150;
        var size = new Size(width, height);
        var source = CreateVerticalLinePattern(width, height);
        var optics = new LiquidGlassOptics();
        var red = optics.Render(source, width, height, size, 28, squircle: true,
            debugView: LiquidGlassDebugView.RRayUv);
        var green = optics.Render(source, width, height, size, 28, squircle: true,
            debugView: LiquidGlassDebugView.GRayUv);
        var blue = optics.Render(source, width, height, size, 28, squircle: true,
            debugView: LiquidGlassDebugView.BRayUv);

        Assert.NotEqual(red, green);
        Assert.NotEqual(green, blue);
    }

    [Fact]
    public void HighContrastDispersionStaysAtTheRimAndPreservesAlphaAndFaceCenter()
    {
        const int width = 220;
        const int height = 150;
        const double radius = 28;
        var size = new Size(width, height);
        var bounds = new Rect(16, 16, width - 32, height - 32);
        var source = CreateVerticalLinePattern(width, height);
        var optics = new LiquidGlassOptics();
        var chapter09 = optics.Render(source, width, height, size, radius, squircle: true,
            glassBoundsDip: bounds);
        var dispersed = optics.Render(source, width, height, size, radius, squircle: true,
            glassBoundsDip: bounds,
            debugView: LiquidGlassDebugView.FinalWithDispersion,
            dispersionParameters: LiquidGlassDispersionParameters.Default with
            {
                DispersionStrength = LiquidGlassDispersionParameters.MaximumDispersionStrength,
                InnerDispersionGain = 1,
                OuterDispersionGain = 1,
            });

        var changedRimPixels = 0;
        var centerChannelSplit = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            Assert.Equal(chapter09[index + 3], dispersed[index + 3]);
            var distanceFromCenter = Math.Min(
                Math.Min(x - bounds.Left, bounds.Right - x),
                Math.Min(y - bounds.Top, bounds.Bottom - y));
            if (distanceFromCenter >= 22 && x >= bounds.Left && x < bounds.Right
                && y >= bounds.Top && y < bounds.Bottom)
                Assert.Equal(chapter09.AsSpan(index, 3).ToArray(), dispersed.AsSpan(index, 3).ToArray());

            if (x < bounds.Left + 16 && y > bounds.Top + 20 && y < bounds.Bottom - 20
                && (dispersed[index] != chapter09[index]
                    || dispersed[index + 1] != chapter09[index + 1]
                    || dispersed[index + 2] != chapter09[index + 2]))
                changedRimPixels++;

            if (distanceFromCenter >= 32 && x >= bounds.Left && x < bounds.Right
                && y >= bounds.Top && y < bounds.Bottom)
                centerChannelSplit = Math.Max(centerChannelSplit,
                    Math.Max(Math.Abs(dispersed[index + 2] - dispersed[index + 1]),
                        Math.Abs(dispersed[index + 1] - dispersed[index])));
        }

        Assert.True(changedRimPixels > 0, "High-contrast lines produce a measurable rim-only optical difference.");
        Assert.Equal(0, centerChannelSplit);
    }

    private static byte[] CreateVerticalLinePattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(x / 7 % 2 == 0 ? 255 : 0);
            var index = (y * width + x) * 4;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }
        return pixels;
    }
}
