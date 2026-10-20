namespace GlassDesk.Dock.Tests;

public sealed class NativeFrameResizeTests
{
    [Fact]
    public void ResizeIntoPreservesLegacyPixelsWithoutAllocatingAFrameArray()
    {
        using var reference=new NativeFrameResize();using var resize=new NativeFrameResize();
        var source=new byte[91*57*4];new Random(1781).NextBytes(source);
        var frame=new CapturePixels(91,57,source);var crop=new PixelBounds(7,5,87,52);
        var output=new byte[39*27*4];
        resize.ResizeInto(frame,39,27,crop,output);
        Assert.Equal(reference.Resize(frame,39,27,crop),output);
        var allocated=GC.GetAllocatedBytesForCurrentThread();
        for(var n=0;n<10;n++) {source[n]++;resize.ResizeInto(frame,39,27,crop,output);}
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread()-allocated,0,1024);
        Assert.Equal(reference.Resize(frame,39,27,crop),output);
    }
    [Fact]
    public void IdenticalNativePixelsReuseTheImmutableResizedFrame()
    {
        using var resize = new NativeFrameResize();
        var source = new byte[1024 * 768 * 4];
        new Random(1357).NextBytes(source);
        var frame = new CapturePixels(1024, 768, source);
        var first = resize.Resize(frame, 640, 480);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var second = resize.Resize(frame with { SourceQpcTicks = 100 }, 640, 480);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allocated < 16_384, $"Unchanged native pixels allocated {allocated} bytes.");
        Assert.Same(first, second);
    }

    [Fact]
    public void ChangingAReusableNativeBufferDoesNotMutateEarlierOutput()
    {
        using var resize = new NativeFrameResize();
        var source = new byte[] { 10, 20, 30, 40, 90, 80, 70, 60 };
        var frame = new CapturePixels(2, 1, source);
        var first = resize.Resize(frame, 2, 1);
        source[0] = 200;
        var second = resize.Resize(frame, 2, 1);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 90, 80, 70, 255 }, first);
        Assert.Equal(new byte[] { 200, 20, 30, 255, 90, 80, 70, 255 }, second);
    }

    [Fact]
    public void MovingTheCropResamplesEvenWhenNativePixelsAreIdentical()
    {
        using var resize = new NativeFrameResize();
        var frame = new CapturePixels(2, 1, [10, 20, 30, 40, 90, 80, 70, 60]);
        var left = resize.Resize(frame, 1, 1, new PixelBounds(0, 0, 1, 1));
        var right = resize.Resize(frame, 1, 1, new PixelBounds(1, 0, 2, 1));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, left);
        Assert.Equal(new byte[] { 90, 80, 70, 255 }, right);
    }
}
