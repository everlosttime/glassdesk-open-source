using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassPresentationRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ExpiredCompactMaterialDoesNotTurnIntoAnOpaqueDarkPanel()
    {
        StaTest.Run(() =>
        {
            var original = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600);
            var scene = new GlassBackdropScene(original);
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene, IsBackdropActive = () => true };
            using var pending = new SharedBackdropFeed((_, stop) => stop.WaitHandle.WaitOne());
            var background = (GlassBackgroundLayer)Get(surface, "_background")!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, pending);
            var owner = new PanelWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); surface.SetPanelShape(new(0, 0, 200, 100), 20);
                var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 20, 180, 20, 255 }, 4, 0);
                Set(surface, "_frame", bitmap); Set(surface, "_frameUsesAndroidGlass", true);
                Set(surface, "_sampledPanelBounds", new Rect(0, 0, 200, 100));
                Set(surface, "_sampledGlassPanelBounds", new Rect(0, 0, 200, 100)); Set(surface, "_sampledGlassPanelRadius", 20d);
                Assert.Equal(255, Render(surface, 1)[(50 * 200 + 100) * 4 + 3]);
                scene = new(original with { Left = 30 }); surface.RequestBackdropRefresh();
                Set(surface, "_materialRetentionUntil", 1L); surface.RequestBackdropRefresh();
                var pixels = Render(surface, 1); Export(pixels, 200, 100, "expired-compact.png");
                Assert.Null(Get(surface, "_frame")); // stale external window content was retired
                var alpha = pixels[(50 * 200 + 100) * 4 + 3];
                Assert.InRange(alpha, (byte)1, (byte)80);
            }
            finally { owner.Close(); }
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SharedPanelCornersKeepSmoothCoverageAtPhysicalDpi(double scale)
    {
        StaTest.Run(() =>
        {
            var panel = new Rect(2.25, 2.25, 195.5, 95.5);
            var shape = SquircleGlassGeometry.Create(panel, 25.5);
            var owner = new PanelWindow { Content = new Border { Background = Brushes.White }, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); owner.Shape(shape); owner.UpdateLayout();
                var pixels = Render(owner, scale);
                var reference = new DrawingVisual(); using (var dc = reference.RenderOpen()) dc.DrawGeometry(Brushes.White, null, shape);
                var smooth = Render(reference, scale);
                var actualPartial = 0; var expectedPartial = 0;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] is > 0 and < 255) actualPartial++;
                    if (smooth[i] is > 0 and < 255) expectedPartial++;
                }
                Export(pixels, (int)(200 * scale), (int)(100 * scale), $"edge-{scale}.png");
                Assert.True(expectedPartial > 100);
                Assert.True(actualPartial >= expectedPartial * .9, $"Only {actualPartial} partially covered edge pixels; smooth geometry has {expectedPartial} at DPI scale {scale}.");
                Assert.Equal(0, pixels[3]);
                Assert.Equal(255, pixels[((int)(50 * scale) * (int)(200 * scale) + (int)(100 * scale)) * 4 + 3]);
            }
            finally { owner.Close(); }
        });
    }
    private static object? Get(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static byte[] Render(Visual visual, double scale)
    {
        if (visual is FrameworkElement element) { element.Measure(new Size(200, 100)); element.Arrange(new Rect(0, 0, 200, 100)); element.UpdateLayout(); }
        var bitmap = new RenderTargetBitmap((int)(200 * scale), (int)(100 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual); var pixels = new byte[(int)(200 * scale) * (int)(100 * scale) * 4]; bitmap.CopyPixels(pixels, (int)(200 * scale) * 4, 0); return pixels;
    }
    private static void Export(byte[] pixels, int width, int height, string name)
    {
        var directory = Environment.GetEnvironmentVariable("GLASSDESK_GLASS_PREVIEW"); if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory); var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
    private sealed class PanelWindow : GlassWindow { public void Shape(Geometry geometry) => SetSharedPanelGeometry(geometry); }
}
