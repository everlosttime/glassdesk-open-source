using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class TransitionBitmapTests
{
    [Fact]
    public void BoundedSubmissionCopiesCompleteRowsAndDoesNotBorrowTheWorkerBuffer()
    {
        StaTest.Run(() =>
        {
            var submit = typeof(LiquidGlassSurface).GetMethod("TryUpdateBackdropBitmap",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(submit);
            var pixels = new byte[] { 17, 18, 19, 255, 27, 28, 29, 255, 37, 38, 39, 255, 47, 48, 49, 255 };
            object?[] args = [null, pixels, 2, 2, null];
            Assert.True((bool)submit.Invoke(null, args)!);
            var bitmap = Assert.IsType<WriteableBitmap>(args[4]);
            var actual = new byte[16]; bitmap.CopyPixels(actual, 8, 0); Assert.Equal(pixels, actual);
            Array.Fill(pixels, (byte)0); bitmap.CopyPixels(actual, 8, 0); Assert.Equal((byte)47, actual[12]);
        });
    }
    [Fact]
    public void NativeBackdropReusesItsBitmapAndPublishesNewPixelsToTheExistingBrush()
    {
        StaTest.Run(() =>
        {
            BitmapSource Update(BitmapSource? current, byte[] pixels, int width)
            {
                Assert.True(LiquidGlassSurface.TryUpdateBackdropBitmap(current, pixels, width, 1, out var bitmap));
                return bitmap;
            }
            var pixels = new byte[] { 17, 17, 17, 255, 42, 42, 42, 255 };
            var first = Update(null, pixels, 2);
            var visual = new System.Windows.Shapes.Rectangle { Width=2, Height=1, Fill=new ImageBrush(first) { Stretch=Stretch.Fill } };
            visual.Measure(new Size(2, 1)); visual.Arrange(new Rect(0, 0, 2, 1));
            var captured = new RenderTargetBitmap(2, 1, 96, 96, PixelFormats.Pbgra32); captured.Render(visual);
            pixels[0]=pixels[1]=pixels[2]=99;
            var second = Update(first, pixels, 2);
            Assert.Same(first, second);
            var actual = new byte[8]; first.CopyPixels(actual, 8, 0); Assert.Equal(pixels, actual);
            Array.Fill(pixels, (byte)0);
            first.CopyPixels(actual, 8, 0); Assert.Equal((byte)99, actual[0]);
            var next = new RenderTargetBitmap(2, 1, 96, 96, PixelFormats.Pbgra32); next.Render(visual);
            next.CopyPixels(actual, 8, 0); Assert.Equal((byte)99, actual[0]);
            captured.CopyPixels(actual, 8, 0); Assert.Equal((byte)17, actual[0]);
            var resized = Update(first, new byte[12], 3); Assert.NotSame(first, resized); Assert.Equal(3, resized.PixelWidth);
        });
    }
}
