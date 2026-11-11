using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherTransparentLoadingTests
{
    [Fact]
    public void LauncherWithoutCapturedFrameDrawsNoPlaceholderBodyOrRim()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            try
            {
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                surface.SetPerformanceSleeping(true);
                var pixels = Render(surface);
                Assert.Equal(0, pixels[(90 * 320 + 160) * 4 + 3]);
                Assert.All(Enumerable.Range(0, pixels.Length / 4), i => Assert.Equal(0, pixels[i * 4 + 3]));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ACompletedFrameRestoresGlassAfterTheTransparentWait()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            try
            {
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                surface.SetPerformanceSleeping(true);
                var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
                Assert.Equal(0, Render(surface)[(90 * 320 + 160) * 4 + 3]);
                var image = new WriteableBitmap(320, 180, 96, 96, PixelFormats.Bgra32, null);
                var source = new byte[320 * 180 * 4];
                for (var i = 0; i < source.Length; i += 4) { source[i + 1] = 180; source[i + 3] = 255; }
                image.WritePixels(new Int32Rect(0, 0, 320, 180), source, 320 * 4, 0);
                Set("_frame", image); Set("_frameScene", scene); Set("_frameUsesAndroidGlass", true);
                Set("_sampledPanelBounds", new Rect(0, 0, 320, 180));
                Set("_sampledGlassPanelBounds", new Rect(0, 0, 320, 180));
                Set("_sampledGlassPanelRadius", window.PanelCornerRadius.TopLeft);
                surface.InvalidateVisual();
                var rendered = Render(surface);
                Assert.Equal(255, rendered[(90 * 320 + 160) * 4 + 3]);
                Assert.InRange(rendered[(90 * 320 + 160) * 4 + 1], 170, 190);
                void Set(string name, object value) => typeof(LiquidGlassSurface)
                    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, value);
            }
            finally { window.Close(); }
        });
    }

    private static byte[] Render(LiquidGlassSurface surface)
    {
        surface.Measure(new Size(320, 180)); surface.Arrange(new Rect(0, 0, 320, 180));
        surface.UpdateLayout();
        var image = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
        var pixels = new byte[320 * 180 * 4]; image.CopyPixels(pixels, 320 * 4, 0); return pixels;
    }
}
