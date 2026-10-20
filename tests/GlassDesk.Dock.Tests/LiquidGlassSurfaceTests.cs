using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LiquidGlassSurfaceTests
{
    [Fact]
    public void TransitionBackdropStaysInScreenCoordinatesAndUsesOnlyTheLiveClip()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var bytes = new byte[320 * 180 * 4];
            for (var y = 0; y < 180; y++)
            for (var x = 0; x < 320; x++)
            {
                var i = (y * 320 + x) * 4;
                bytes[i] = bytes[i + 1] = bytes[i + 2] = (byte)(x / 2);
                bytes[i + 3] = 255;
            }
            var backdrop = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, bytes, 1280);
            typeof(LiquidGlassSurface).GetField("_transitionBackdrop", flags)!.SetValue(surface, backdrop);
            typeof(LiquidGlassSurface).GetField("_transitionBackdropBounds", flags)!.SetValue(surface, new Rect(0, 0, 320, 180));
            surface.SetContainerTransitionBounds(new Rect(0, 0, 320, 180));
            foreach (var panel in new[] { new Rect(20, 20, 280, 140), new Rect(80, 30, 180, 120) })
            {
                surface.SetPanelShape(panel, 30);
                surface.Measure(new Size(320, 180)); surface.Arrange(new Rect(0, 0, 320, 180));
                surface.UpdateLayout();
                var image = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
                image.Render(surface);
                var actual = new byte[bytes.Length]; image.CopyPixels(actual, 1280, 0);
                Assert.Equal(255, actual[(90 * 320 + 160) * 4 + 3]);
                Assert.Equal(80, actual[(90 * 320 + 160) * 4]);
                Assert.Equal(0, actual[(10 * 320 + 10) * 4 + 3]);
                Assert.Equal(0, actual[((int)panel.Top * 320 + (int)panel.Left) * 4 + 3]);
                var layer = (TransitionGlassLayer)typeof(LiquidGlassSurface).GetField("_transitionLayer", flags)!.GetValue(surface)!;
                Assert.Equal(new Size(320, 180), layer.RenderSize);
                if (layer.Effect is GlassDesk.UI.Effects.TransitionGlassEffect lens)
                {
                    Assert.InRange(Math.Abs(lens.Panel.X * 320 - panel.X), 0, .00001);
                    Assert.InRange(Math.Abs(lens.Panel.Y * 180 - panel.Y), 0, .00001);
                    Assert.InRange(Math.Abs(lens.Panel.Z * 320 - panel.Width), 0, .00001);
                    Assert.InRange(Math.Abs(lens.Panel.W * 180 - panel.Height), 0, .00001);
                    Assert.Equal(30, lens.Material.Z);
                }
            }
        });
    }

    [Fact]
    public void ExpandingPanelDoesNotExposeAnEmptyGapAroundThePreviousGlassMask()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var bytes = new byte[280 * 140 * 4];
            for (var y = 50; y < 90; y++)
            for (var x = 100; x < 180; x++) bytes[(y * 280 + x) * 4 + 3] = 255;
            var previous = new WriteableBitmap(280, 140, 96, 96, PixelFormats.Bgra32, null);
            previous.WritePixels(new Int32Rect(0, 0, 280, 140), bytes, 280 * 4, 0);
            typeof(LiquidGlassSurface).GetField("_frame", flags)!.SetValue(surface, previous);
            typeof(LiquidGlassSurface).GetField("_frameUsesAndroidGlass", flags)!.SetValue(surface, true);
            typeof(LiquidGlassSurface).GetField("_sampledPanelBounds", flags)!.SetValue(surface, new Rect(0, 0, 280, 140));
            var panelField = typeof(LiquidGlassSurface).GetField("_sampledGlassPanelBounds", flags);
            panelField?.SetValue(surface, new Rect(100, 50, 80, 40));
            surface.SetPanelShape(new Rect(40, 20, 200, 100), 30);
            surface.Measure(new Size(280, 140)); surface.Arrange(new Rect(0, 0, 280, 140));
            var bitmap = new RenderTargetBitmap(280, 140, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var pixels = new byte[280 * 140 * 4]; bitmap.CopyPixels(pixels, 280 * 4, 0);
            Assert.Equal(255, pixels[(70 * 280 + 70) * 4 + 3]);
            // The old optical mask must not survive as a second container inside the
            // expanded shape, even while the fresh backdrop is not ready yet.
            Assert.Equal(255, pixels[(70 * 280 + 140) * 4 + 3]);
            Assert.Equal(0, pixels[3]);
        });
    }

    [Fact]
    public void FirstCompatibleFramePaintsBodyBeforeCaptureInsteadOfOnlyABorder()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            typeof(LiquidGlassSurface).GetField("_frameUsesAndroidGlass", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(surface, true);
            surface.SetPanelShape(new Rect(40, 20, 200, 100), 30);
            surface.Measure(new Size(280, 140)); surface.Arrange(new Rect(0, 0, 280, 140));
            var bitmap = new RenderTargetBitmap(280, 140, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[280 * 140 * 4]; bitmap.CopyPixels(pixels, 280 * 4, 0);
            Assert.Equal(255, pixels[(70 * 280 + 140) * 4 + 3]);
            Assert.Equal(0, pixels[3]);
        });
    }

    [Fact]
    public void BackgroundFrameKeepsCapturePixelsStableAfterSamplerBufferChanges()
    {
        var captureBuffer = new byte[] { 10, 20, 30, 255 };
        var frame = new GlassBackgroundFrame(captureBuffer, 1, 1, GlassBackdropSource.LiveDesktop);

        captureBuffer[0] = 90;

        Assert.Equal((byte)10, frame.Pixels[0]);
        Assert.Equal(GlassBackdropSource.LiveDesktop, frame.Source);
    }

    [Fact]
    public void WaitingForANewCaptureDoesNotReplayTheOldSmallContainer()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            var frame = new WriteableBitmap(4,4,96,96,PixelFormats.Bgra32,null);
            frame.WritePixels(new Int32Rect(0,0,4,4), Enumerable.Repeat((byte)255,64).ToArray(),16,0);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(LiquidGlassSurface).GetField("_frame",flags)!.SetValue(surface,frame);
            typeof(LiquidGlassSurface).GetField("_sampledPanelBounds",flags)!.SetValue(surface,new Rect(10,10,20,20));
            surface.SetPanelShape(new Rect(0,0,200,100),20);
            surface.Measure(new Size(200,100)); surface.Arrange(new Rect(0,0,200,100));
            var image = new RenderTargetBitmap(200,100,96,96,PixelFormats.Pbgra32); image.Render(surface);
            var pixels = new byte[200*100*4]; image.CopyPixels(pixels,800,0);
            Assert.Equal(255, pixels[(20*200+20)*4+3]);
            Assert.Equal(255, pixels[(50*200+100)*4+3]);
            Assert.True(pixels[(20*200+20)*4] < 230, "The previous white container must not persist inside the new material.");
        });
    }

    [Fact]
    public void ClosingPanelDoesNotReplayAnOldRimInsideItsCurrentOutline()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            // Old right edge at x=170 becomes an interior edge after the panel moves.
            var pixels = new byte[300 * 180 * 4];
            for (var y = 30; y < 130; y++)
            for (var x = 167; x < 171; x++)
            {
                var i = (y * 300 + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 255;
            }
            var old = new WriteableBitmap(300, 180, 96, 96, PixelFormats.Bgra32, null);
            old.WritePixels(new Int32Rect(0, 0, 300, 180), pixels, 1200, 0);
            typeof(LiquidGlassSurface).GetField("_frame", flags)!.SetValue(surface, old);
            typeof(LiquidGlassSurface).GetField("_frameUsesAndroidGlass", flags)!.SetValue(surface, true);
            typeof(LiquidGlassSurface).GetField("_sampledPanelBounds", flags)!.SetValue(surface, new Rect(0, 0, 300, 180));
            typeof(LiquidGlassSurface).GetField("_sampledGlassPanelBounds", flags)!.SetValue(surface, new Rect(30, 30, 140, 100));
            surface.TransitionProgress = .5;
            surface.SetPanelShape(new Rect(100, 50, 100, 80), 25);
            surface.Measure(new Size(300, 180)); surface.Arrange(new Rect(0, 0, 300, 180));
            var bitmap = new RenderTargetBitmap(300, 180, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var actual = new byte[pixels.Length]; bitmap.CopyPixels(actual, 1200, 0);
            Assert.Equal(255, actual[(90 * 300 + 169) * 4 + 3]);
            Assert.True(actual[(90 * 300 + 169) * 4] < 230, "The previous white rim must not appear in the new panel's interior.");
        });
    }

    [Fact]
    public void AnimatedGlassOnlyPaintsTheCurrentPanelAndItsOwnCornerMask()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            surface.SetPanelShape(new Rect(80, 30, 100, 80), 30);
            surface.Measure(new Size(320, 160));
            surface.Arrange(new Rect(0, 0, 320, 160));
            var rendered = new RenderTargetBitmap(320, 160, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(surface);
            var pixels = new byte[320 * 160 * 4];
            rendered.CopyPixels(pixels, 320 * 4, 0);
            Assert.Equal(0, pixels[(30 * 320 + 80) * 4 + 3]);
            Assert.Equal(0, pixels[(70 * 320 + 40) * 4 + 3]);
            Assert.True(pixels[(70 * 320 + 130) * 4 + 3] > 0);
            Assert.Equal(new Rect(80, 30, 100, 80), surface.CaptureBoundsInWindow);
        });
    }

    [Fact]
    public void MissingBackdropKeepsCompleteMaterialAndCornersTransparent()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            surface.Measure(new Size(320, 80));
            surface.Arrange(new Rect(0, 0, 320, 80));
            var rendered = new RenderTargetBitmap(320, 80, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(surface);
            var pixels = new byte[320 * 80 * 4];
            rendered.CopyPixels(pixels, 320 * 4, 0);
            Assert.Equal(255, pixels[(40 * 320 + 160) * 4 + 3]);
            Assert.Equal(0, pixels[3]);
        });
    }
}
