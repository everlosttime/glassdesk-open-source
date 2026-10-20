using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassFrameBufferTests
{
    private static Task<GlassFrameResult?> Render(GlassFrameWorker worker, int length, Action<byte[], byte[]> render)
        => worker.RenderBufferedAsync(length, render);

    [Fact]
    public async Task TheNextFrameCannotOverwritePixelsUntilPresentationReleasesThem()
    {
        var worker = new GlassFrameWorker();
        var first = (await Render(worker, 1280, (pixels, scratch) => pixels[0] = 17))!;
        Assert.True(worker.IsBusy);
        Assert.Null(await Render(worker, 1280, (_, _) => throw new InvalidOperationException("must not render")));
        var release = Assert.IsAssignableFrom<IDisposable>(first);
        release.Dispose();
        var second = (await Render(worker, 1280, (pixels, scratch) => pixels[0] = 42))!;
        Assert.Same(first.Pixels, second.Pixels);
        // A duplicate release from the previous frame cannot release the new lease.
        release.Dispose();
        Assert.True(worker.IsBusy);
        Assert.Equal((byte)42, second.Pixels[0]);
        Assert.IsAssignableFrom<IDisposable>(second).Dispose();
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public async Task RepeatedAnimationFramesReuseExactlySizedOutputAndScratchBuffers()
    {
        var worker = new GlassFrameWorker();
        byte[]? output = null, temporary = null;
        for (var i = 0; i < 16; i++)
        {
            var frame = await Render(worker, 640 * 500 * 4, (pixels, scratch) =>
            {
                Assert.Equal(640 * 500 * 4, pixels.Length);
                Assert.Equal(pixels.Length, scratch.Length);
                Assert.NotSame(pixels, scratch);
                if (output is not null) { Assert.Same(output, pixels); Assert.Same(temporary, scratch); }
                output = pixels; temporary = scratch;
            });
            Assert.IsAssignableFrom<IDisposable>(frame).Dispose();
        }
        var resized = await Render(worker, 64, (pixels, scratch) =>
        {
            Assert.Equal(64, pixels.Length);
            Assert.Equal(64, scratch.Length);
            Assert.NotSame(output, pixels);
            Assert.NotSame(temporary, scratch);
        });
        Assert.IsAssignableFrom<IDisposable>(resized).Dispose();
    }

    [Fact]
    public async Task InvalidatedOrFailedFramesReleaseTheirLeaseWithoutPublication()
    {
        var worker = new GlassFrameWorker();
        var stale = await Render(worker, 64, (_, _) => worker.Invalidate());
        Assert.Null(stale);
        Assert.False(worker.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Render(worker, 64,
            (_, _) => throw new InvalidOperationException("render failure")));
        Assert.False(worker.IsBusy);
        var valid = await Render(worker, 64, (pixels, scratch) => pixels[0] = 99);
        Assert.Equal((byte)99, valid!.Pixels[0]);
        Assert.IsAssignableFrom<IDisposable>(valid).Dispose();
    }

    [Fact]
    public async Task ClearingHiddenBuffersPreservesAnOutstandingFrameAndReallocatesOnReopen()
    {
        var worker = new GlassFrameWorker();
        var first = (await Render(worker, 64, (pixels, _) => pixels[0] = 17))!;
        worker.ClearCachedBuffers();
        Assert.True(worker.IsBusy);
        Assert.Equal((byte)17, first.Pixels[0]);
        Assert.IsAssignableFrom<IDisposable>(first).Dispose();
        var reopened = (await Render(worker, 64, (pixels, _) => pixels[0] = 42))!;
        Assert.NotSame(first.Pixels, reopened.Pixels);
        Assert.Equal((byte)17, first.Pixels[0]);
        Assert.IsAssignableFrom<IDisposable>(reopened).Dispose();
    }

    [Fact]
    public void ReusingPixelsAfterBitmapPublicationCannotChangeEarlierVisiblePixels()
    {
        StaTest.Run(() =>
        {
            var worker = new GlassFrameWorker();
            var first = Render(worker, 8, (pixels, scratch) => new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }.CopyTo(pixels, 0))
                .GetAwaiter().GetResult()!;
            var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, first.Pixels, 8);
            image.Freeze();
            Assert.IsAssignableFrom<IDisposable>(first).Dispose();
            var second = Render(worker, 8, (pixels, scratch) => Array.Fill(pixels, (byte)200)).GetAwaiter().GetResult()!;
            Assert.Same(first.Pixels, second.Pixels);
            var copy = new byte[8];
            image.CopyPixels(copy, 8, 0);
            Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, copy);
            Assert.IsAssignableFrom<IDisposable>(second).Dispose();
        });
    }
}
