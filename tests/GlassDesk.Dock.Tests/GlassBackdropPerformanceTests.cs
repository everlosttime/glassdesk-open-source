using System.Diagnostics;
using System.Windows;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using Xunit.Abstractions;

namespace GlassDesk.Dock.Tests;

public sealed class GlassBackdropPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 1, true, .2, .2, false)]
    [InlineData(19, 73, false, .17, .2, true)]
    [InlineData(740, 432, true, .2, .2, true)]
    [InlineData(65, 23, false, .81, 1, false)]
    [InlineData(71, 37, true, 0, .5, false)]
    [InlineData(33, 29, false, 1, 0, true)]
    [InlineData(47, 31, true, .35, 1, true)]
    public void BufferedBackdropPreservesScalarPixelsForLightDarkTintAndAlpha(int width, int height,
        bool dark, double tint, double clarity, bool launchpad)
    {
        var source = new byte[width * height * 4];
        new Random(7319).NextBytes(source);
        var original = source.ToArray();
        var size = new Size(width * 1.5, height * 1.5);
        var expected = ReferenceBackdrop(source, width, height, size, .91, clarity, dark, tint, launchpad);
        var result = new byte[source.Length];
        AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source, result, new byte[source.Length],
            width, height, size, dark, .91, tint, clarity, launchpad);
        Assert.Equal(expected, result);
        Assert.Equal(original, source);
    }
    [Fact]
    public void CapturePaddingAvoidsDuplicatingItsCompletePixelBuffer()
    {
        const int width = 640, height = 500;
        var source = new byte[630 * 490 * 4];
        new Random(1234).NextBytes(source);
        var frame = new GlassBackgroundFrame(source, 630, 490, GlassBackdropSource.LiveDesktop);
        GlassBackgroundFrame Extend() => GlassBackgroundLayer.ExtendCapture(frame,
            new PixelRect(10, 10, 1260, 980), new PixelRect(0, 0, 1280, 1000), width, height);
        for (var i = 0; i < 5; i++) Extend();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 12; i++) Extend();
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds / 12;
        allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 12;
        output.WriteLine($"320k capture padding: {milliseconds:F3} ms/frame; {allocated} allocated bytes/frame");
        Assert.Equal(source, frame.Pixels);
        Assert.True(allocated < width * height * 4L * 1.5, $"Allocated {allocated} bytes.");
        var extended = Extend();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX = Math.Clamp((int)Math.Floor(((x + .5) * 1280 / width - 10) * 630 / 1260), 0, 629);
            var sourceY = Math.Clamp((int)Math.Floor(((y + .5) * 1000 / height - 10) * 490 / 980), 0, 489);
            for (var c = 0; c < 4; c++)
                Assert.Equal(source[(sourceY * 630 + sourceX) * 4 + c], extended.Pixels[(y * width + x) * 4 + c]);
        }
    }

    [Fact]
    public void TransitionBackdropAvoidsMultipleLargeAllocationsPerFrame()
    {
        const int width = 640, height = 500;
        var source = new byte[width * height * 4];
        new Random(1234).NextBytes(source);
        var original = (byte[])source.Clone();
        byte[] Render() => AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, width, height,
            new Size(1280, 1000), true, 1);
        for (var i = 0; i < 5; i++) Render();
        const int frames = 12;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < frames; i++) Render();
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds / frames;
        allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated) / frames;
        output.WriteLine($"320k backdrop: {milliseconds:F3} ms/frame; {allocated} allocated bytes/frame");
        Assert.Equal(original, source);
        Assert.True(allocated < source.Length * 2L, $"Allocated {allocated} bytes for {source.Length} pixels bytes.");
        Assert.Equal(ReferenceBackdrop(source, width, height, new Size(1280, 1000), 1, .5), Render());
    }

    [Theory]
    [InlineData(1, 7, .6, .5)]
    [InlineData(9, 1, 1, 0)]
    [InlineData(33, 27, 1, .5)]
    [InlineData(64, 47, .8, .1)]
    [InlineData(64, 47, .6, .8)]
    [InlineData(33, 27, 0, .5)]
    [InlineData(33, 27, 1, 1)]
    [InlineData(4, 31, 1, 0)]
    [InlineData(5, 31, 1, .5)]
    [InlineData(73, 119, .73, .37)]
    [InlineData(121, 17, .91, .66)]
    public void OptimizedBackdropMatchesOriginalPixelsIncludingAlpha(int width, int height, double strength, double clarity)
    {
        var source = new byte[width * height * 4];
        new Random(5678).NextBytes(source);
        var size = new Size(width * 1.5, height * 1.5);
        var expected = ReferenceBackdrop(source, width, height, size, strength, clarity);
        var actual = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, width, height, size,
            true, strength, backgroundClarity: clarity);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BackdropRoundingMatchesTheOriginalAcrossBlurRadiiAndChannelExtremes()
    {
        const int width = 41, height = 37;
        var random = new Random(9876);
        var source = new byte[width * height * 4];
        for (var pattern = 0; pattern < 3; pattern++)
        {
            if (pattern == 0) random.NextBytes(source);
            else for (var i = 0; i < source.Length; i++) source[i] = (byte)(pattern == 1 ? 255 : i % 2 * 255);
            foreach (var scale in new[] { .3, .5, 1.0, 1.5, 3.0 })
            foreach (var strength in new[] { .01, .37, .6, .83, 1.0 })
            {
                var size = new Size(width * scale, height * scale);
                var expected = ReferenceBackdrop(source, width, height, size, strength, .5);
                var actual = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, width, height, size, true, strength);
                Assert.Equal(expected, actual);
            }
        }
    }

    // Original separable three-box filter, retained as a pixel oracle for the optimized path.
    internal static byte[] ReferenceBackdrop(byte[] source, int width, int height, Size size, double strength, double clarity,
        bool dark = true, double tint = AndroidLiquidGlassRenderer.BodyOpacity, bool launchpad = false)
    {
        var filtered = (byte[])source.Clone();
        if (strength > 0)
        {
            for (var i = 0; i < source.Length; i += 4)
            {
                var luminance = .072 * source[i] + .715 * source[i + 1] + .213 * source[i + 2];
                for (var c = 0; c < 3; c++)
                    filtered[i + c] = Byte(luminance + (source[i + c] - luminance) * (1 + .5 * strength));
            }
            var blurScale = clarity <= .5 ? 1.5 - clarity : 2 * (1 - clarity);
            if (blurScale > 0)
            {
                var temporary = new byte[source.Length];
                var result = new byte[source.Length];
                for (var pass = 0; pass < 3; pass++)
                {
                    Box(filtered, temporary, width, height, Radius(3.2 * strength * blurScale * width / size.Width, pass), true);
                    Box(temporary, result, width, height, Radius(3.2 * strength * blurScale * height / size.Height, pass), false);
                    (filtered, result) = (result, filtered);
                }
            }
        }
        var opacity = tint * (launchpad ? Math.Min(1, 2 * (1 - clarity)) : 1) * strength;
        for (var i = 0; i < filtered.Length; i += 4)
        for (var c = 0; c < 3; c++) filtered[i + c] = Byte(filtered[i + c] * (1 - opacity) + (dark ? 18 : 250) * opacity);
        return filtered;
    }

    private static int Radius(double sigma, int pass)
    {
        if (sigma < .1) return 0;
        var lower = Math.Max(1, (int)Math.Floor(Math.Sqrt(4 * sigma * sigma + 1)));
        if (lower % 2 == 0) lower--;
        var count = (int)Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4d * lower - 4));
        return (pass < count ? lower - 1 : lower + 1) / 2;
    }

    private static void Box(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        if (radius == 0) { source.CopyTo(target, 0); return; }
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var step = horizontal ? 4 : width * 4;
        var divisor = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        for (var c = 0; c < 4; c++)
        {
            var start = (horizontal ? line * width * 4 : line * 4) + c;
            var sum = 0;
            for (var k = -radius; k <= radius; k++) sum += source[start + Math.Clamp(k, 0, length - 1) * step];
            for (var position = 0; position < length; position++)
            {
                target[start + position * step] = (byte)((sum + divisor / 2) / divisor);
                sum -= source[start + Math.Clamp(position - radius, 0, length - 1) * step];
                sum += source[start + Math.Clamp(position + radius + 1, 0, length - 1) * step];
            }
        }
    }

    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
