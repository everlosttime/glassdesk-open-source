using System.Windows;
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

    [Fact]
    public void FullyOnscreenCaptureReturnsTheIdenticalFrameWithoutResampling()
    {
        var region = new PixelRect(90, 30, 2, 1);
        var frame = new GlassBackgroundFrame(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 },
            2, 1, GlassBackdropSource.LiveDesktop);
        Assert.Same(frame, GlassBackgroundLayer.ExtendCapture(frame, region, region, 2, 1));
    }
}
