using System.Windows;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassCaptureGeometryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PartiallyHiddenDockKeepsCompleteGlassInsideLogicalSamplingDomain(double dpi)
    {
        // Bottom 50 physical pixels of the requested frame are outside the Primary.
        var geometry = GlassCaptureGeometry.Create(new PixelRect(90, 750, 120, 100),
            new PixelRect(90, 750, 120, 50), new Point(100, 760),
            new Rect(0, 0, 100 / dpi, 80 / dpi), dpi, true);

        Assert.Equal(new Size(120 / dpi, 100 / dpi), geometry.SampleSizeDip);
        Assert.True(new Rect(geometry.SampleSizeDip).Contains(geometry.GlassBoundsDip));
        Assert.Equal(new Rect(-10 / dpi, -10 / dpi, 120 / dpi, 100 / dpi), geometry.FrameBoundsDip);
        var pixels = Enumerable.Repeat((byte)128, 120 * 100 * 4).ToArray();
        var result = new LiquidGlassOptics().Render(pixels, 120, 100, geometry.SampleSizeDip,
            20 / dpi, true, glassBoundsDip: geometry.GlassBoundsDip);
        Assert.Equal(pixels.Length, result.Length);
    }

    [Fact]
    public void TopEdgeClippingCannotMoveGlassToNegativeSampleCoordinates()
    {
        var geometry = GlassCaptureGeometry.Create(new PixelRect(90, -50, 120, 100),
            new PixelRect(90, 0, 120, 50), new Point(100, -40),
            new Rect(0, 0, 100, 80), 1, true);

        Assert.Equal(new Rect(10, 10, 100, 80), geometry.GlassBoundsDip);
        Assert.True(new Rect(geometry.SampleSizeDip).Contains(geometry.GlassBoundsDip));
    }

    [Fact]
    public void OffscreenRowsUseOnlyCapturedPrimaryEdgePixelsWithoutCompressingTheImage()
    {
        var pixels = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 };
        var frame = new GlassBackgroundFrame(pixels, 1, 2, GlassBackdropSource.LiveDesktop);
        var extended = GlassBackgroundLayer.ExtendCapture(frame, new PixelRect(0, 0, 1, 2),
            new PixelRect(0, -1, 1, 4), 1, 4);

        Assert.Equal(new byte[] {
            10, 20, 30, 255, 10, 20, 30, 255,
            40, 50, 60, 255, 40, 50, 60, 255 }, extended.Pixels);
        Assert.Equal(GlassBackdropSource.LiveDesktop, extended.Source);
    }

    [Fact]
    public void OffscreenColumnsRepeatPrimaryEdges()
    {
        var frame = new GlassBackgroundFrame(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 },
            2, 1, GlassBackdropSource.Wallpaper);
        var extended = GlassBackgroundLayer.ExtendCapture(frame, new PixelRect(0, 0, 2, 1),
            new PixelRect(-1, 0, 4, 1), 4, 1);

        Assert.Equal(new byte[] {
            10, 20, 30, 255, 10, 20, 30, 255,
            40, 50, 60, 255, 40, 50, 60, 255 }, extended.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransientNeutralFillCannotReplaceTheVisibleBackdrop(bool extend)
    {
        var region = new PixelRect(90, 30, 4, 4);
        var requested = extend ? new PixelRect(89, 29, 6, 6) : region;
        using var layer = new GlassBackgroundLayer();
        using var fill = new GlassBackgroundFrame(Enumerable.Repeat(new byte[] { 178, 178, 178, 255 }, 16)
            .SelectMany(pixel => pixel).ToArray(), 4, 4, GlassBackdropSource.LiveDesktop);
        using var wallpaper = new GlassBackgroundFrame(Enumerable.Repeat(new byte[] { 36, 108, 62, 255 }, 16)
            .SelectMany(pixel => pixel).ToArray(), 4, 4, GlassBackdropSource.LiveDesktop);

        using var initial = layer.ExtendCapturedFrame(wallpaper, region, requested, requested.Width, requested.Height);
        Assert.NotNull(initial);
        Assert.Null(layer.ExtendCapturedFrame(fill, region, requested, requested.Width, requested.Height));
        using var recovered = layer.ExtendCapturedFrame(wallpaper, region, requested, requested.Width, requested.Height);
        Assert.NotNull(recovered);
        Assert.Null(layer.ExtendCapturedFrame(fill, region, requested, requested.Width, requested.Height));
        Thread.Sleep(180);
        using var realSolidColor = layer.ExtendCapturedFrame(fill, region, requested, requested.Width, requested.Height);
        Assert.NotNull(realSolidColor);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(5, true)]
    public void NeutralFillInPartOfTheCropUsesObservedCoverage(int grayColumns, bool shouldHold)
    {
        var region = new PixelRect(90, 30, 12, 6);
        using var layer = new GlassBackgroundLayer();
        var pixels = new byte[12 * 6 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var gray = index / 4 % region.Width < grayColumns;
            pixels[index] = gray ? (byte)178 : (byte)36;
            pixels[index + 1] = gray ? (byte)178 : (byte)108;
            pixels[index + 2] = gray ? (byte)178 : (byte)62;
            pixels[index + 3] = 255;
        }
        using var frame = new GlassBackgroundFrame(pixels, region.Width, region.Height, GlassBackdropSource.LiveDesktop);
        using var result = layer.ExtendCapturedFrame(frame, region, region, region.Width, region.Height);
        Assert.Equal(shouldHold, result is null);
    }

    [Theory]
    [InlineData(GlassMaterialTarget.StatusBar, false)]
    [InlineData(GlassMaterialTarget.Launcher, true)]
    public void GlassSkipsASingleBrightFrameButAcceptsASustainedChange(GlassMaterialTarget target, bool stillHoldAt120Milliseconds)
    {
        const int width = 32, height = 16;
        using var layer = new GlassBackgroundLayer();
        using var regular = Frame(170, 172, 176, allPixels: true);
        using var bright = Frame(206, 205, 192, allPixels: false);
        var geometry = new GlassCaptureGeometry(new Size(width, height), new Rect(0, 0, width, height), Rect.Empty);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var ticksPerMillisecond = System.Diagnostics.Stopwatch.Frequency / 1000;
        Assert.False(layer.ShouldHoldBrightBackdropFrame(target, regular, geometry, now, samePose: false));
        Assert.True(layer.ShouldHoldBrightBackdropFrame(target, bright, geometry, now + 20 * ticksPerMillisecond, samePose: true));
        Assert.False(layer.ShouldHoldBrightBackdropFrame(target, regular, geometry, now + 55 * ticksPerMillisecond, samePose: true));
        Assert.True(layer.ShouldHoldBrightBackdropFrame(target, bright, geometry, now + 70 * ticksPerMillisecond, samePose: true));
        Assert.True(layer.ShouldHoldBrightBackdropFrame(target, bright, geometry, now + 140 * ticksPerMillisecond, samePose: true));
        Assert.Equal(stillHoldAt120Milliseconds,
            layer.ShouldHoldBrightBackdropFrame(target, bright, geometry, now + 190 * ticksPerMillisecond, samePose: true));
        Assert.False(layer.ShouldHoldBrightBackdropFrame(target, bright, geometry, now + 320 * ticksPerMillisecond, samePose: true));

        static GlassBackgroundFrame Frame(byte blue, byte green, byte red, bool allPixels)
        {
            var pixels = new byte[width * height * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var changed = allPixels || index / 4 % width < width / 2;
                pixels[index] = changed ? blue : (byte)170;
                pixels[index + 1] = changed ? green : (byte)172;
                pixels[index + 2] = changed ? red : (byte)176;
                pixels[index + 3] = 255;
            }
            return new(pixels, width, height, GlassBackdropSource.LiveDesktop);
        }
    }

    [Fact]
    public void FullyOnscreenCaptureReturnsTheIdenticalFrameWithoutResampling()
    {
        var region = new PixelRect(90, 30, 2, 1);
        var frame = new GlassBackgroundFrame(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 },
            2, 1, GlassBackdropSource.LiveDesktop);
        Assert.Same(frame, GlassBackgroundLayer.ExtendCapture(frame, region, region, 2, 1));
    }
}
