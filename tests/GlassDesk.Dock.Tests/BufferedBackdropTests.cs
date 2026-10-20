using System.Diagnostics;
using System.Windows;
using GlassDesk.UI.Controls;
using Xunit.Abstractions;

namespace GlassDesk.Dock.Tests;

public sealed class BufferedBackdropTests(ITestOutputHelper output)
{
    private static void Render(byte[] source, byte[] pixels, byte[] scratch, int width, int height, double strength, double clarity)
        => AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source, pixels, scratch, width, height,
            new Size(width * 1.5, height * 1.5), true, strength, backgroundClarity: clarity);

    [Theory]
    [InlineData(1, 7)]
    [InlineData(9, 1)]
    [InlineData(33, 27)]
    [InlineData(73, 119)]
    public void ReusedBuffersPreserveOriginalPixelsAndCaptureAcrossMaterialChanges(int width, int height)
    {
        var source = new byte[width * height * 4];
        var pixels = new byte[source.Length];
        var scratch = new byte[source.Length];
        foreach (var clarity in new[] { 0d, .37, .5, .8, 1 })
        foreach (var strength in new[] { 0d, .01, .6, .83, 1 })
        {
            new Random(5678 + (int)(strength * 100)).NextBytes(source);
            var original = (byte[])source.Clone();
            Array.Fill(pixels, (byte)193); Array.Fill(scratch, (byte)211);
            Render(source, pixels, scratch, width, height, strength, clarity);
            Assert.Equal(GlassBackdropPerformanceTests.ReferenceBackdrop(source, width, height,
                new Size(width * 1.5, height * 1.5), strength, clarity), pixels);
            Assert.Equal(original, source);
        }
    }

    [Fact]
    public void BufferedMaterialHonorsThemeTintAndLaunchpadClarityWithoutAliasingLegacySnapshots()
    {
        const int width=37, height=31;
        var source = new byte[width * height * 4]; new Random(5719).NextBytes(source);
        var pixels = new byte[source.Length]; var scratch = new byte[source.Length];
        var size = new Size(74, 62);
        foreach (var dark in new[] { false, true })
        foreach (var launchpad in new[] { false, true })
        foreach (var clarity in new[] { .13, .4991652754590962, .87, double.NaN })
        foreach (var tint in new[] { 0d, .15, .7, double.NaN })
        {
            var expected = AndroidLiquidGlassRenderer.RenderTransitionBackdrop(source, width, height, size, dark, .73, tint, clarity, launchpad);
            AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source, pixels, scratch, width, height, size, dark, .73, tint, clarity, launchpad);
            Assert.Equal(expected, pixels);
            Array.Fill(pixels, (byte)193);
            Assert.NotEqual(expected, pixels);
        }
    }

    [Fact]
    public void ActiveBackdropDoesNotAllocateLargeArraysAfterWarmup()
    {
        const int width=640, height=500;
        var source = new byte[width * height * 4];
        new Random(1234).NextBytes(source);
        var pixels = new byte[source.Length]; var scratch = new byte[source.Length];
        for (var i=0; i<5; i++) Render(source, pixels, scratch, width, height, 1, .5);
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var started = Stopwatch.GetTimestamp();
        for (var i=0; i<12; i++) Render(source, pixels, scratch, width, height, 1, .5);
        allocated = (GC.GetAllocatedBytesForCurrentThread()-allocated)/12;
        output.WriteLine($"Owned 320k backdrop: {Stopwatch.GetElapsedTime(started).TotalMilliseconds/12:F3} ms/frame; {allocated} allocated bytes/frame");
        Assert.True(allocated < 4096, $"Allocated {allocated} bytes/frame.");
    }
}
