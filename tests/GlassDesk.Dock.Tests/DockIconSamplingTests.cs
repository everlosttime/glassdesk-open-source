using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.Dock.Tests;

public sealed class DockIconSamplingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void FractionalSizeIconEdgesMatchHighQualityResampling(double scale)
    {
        StaTest.Run(() =>
        {
            const int sourceSize = 256;
            var pixels = new byte[sourceSize * sourceSize * 4];
            for (var y = 0; y < sourceSize; y++)
            for (var x = 0; x < sourceSize; x++)
            {
                var i = (y * sourceSize + x) * 4;
                if ((x - 127.5) * (x - 127.5) + (y - 127.5) * (y - 127.5) < 111.2 * 111.2)
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 255;
            }
            var source = BitmapSource.Create(sourceSize, sourceSize, 96, 96, PixelFormats.Pbgra32, null, pixels, sourceSize * 4);
            var strip = new DockTileStrip();
            var tile = (Border)typeof(DockTileStrip).GetMethod("CreateTile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(strip, [new DockTileModel { EntryId = "sample", Icon = source, IconSize = 43.6 }, true])!;
            var icon = FindImage(tile)!; Assert.NotNull(icon);
            var expected = new Image { Source = source, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(expected, BitmapScalingMode.HighQuality);
            var actual = Render(icon, scale);
            Assert.Equal(Render(expected, scale), actual);
            Export(actual, scale, "icon-high-quality");
            RenderOptions.SetBitmapScalingMode(expected, BitmapScalingMode.Linear);
            Export(Render(expected, scale), scale, "icon-before-linear");
        });
    }
    private static Image? FindImage(DependencyObject root)
    {
        if (root is Image image) return image;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindImage(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private static byte[] Render(Image image, double scale)
    {
        image.Width = image.Height = 43.6; image.Measure(new Size(43.6, 43.6)); image.Arrange(new Rect(0, 0, 43.6, 43.6)); image.UpdateLayout();
        var width = (int)Math.Ceiling(43.6 * scale);
        var bitmap = new RenderTargetBitmap(width, width, scale * 96, scale * 96, PixelFormats.Pbgra32); bitmap.Render(image);
        var pixels = new byte[width * width * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
    }
    private static void Export(byte[] pixels, double scale, string name)
    {
        var directory = Environment.GetEnvironmentVariable("GLASSDESK_GLASS_PREVIEW"); if (string.IsNullOrEmpty(directory)) return;
        System.IO.Directory.CreateDirectory(directory); var width = (int)Math.Ceiling(43.6 * scale);
        var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, $"{name}-{scale}.png")); encoder.Save(stream);
    }
}
