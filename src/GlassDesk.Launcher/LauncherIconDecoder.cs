using System.IO;
using System.Windows.Media.Imaging;

namespace GlassDesk.Launcher;

internal static class LauncherIconDecoder
{
    internal static BitmapImage DecodeFile(string path, int maximumPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPixels);
        // Delay pixel creation: inspect dimensions before asking WIC for a bounded decode.
        // Setting both decode dimensions would distort custom icons.
        int width, height;
        using (var stream = File.OpenRead(path))
        {
            var metadata = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            width = metadata.PixelWidth; height = metadata.PixelHeight;
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (Math.Max(width, height) > maximumPixels)
        {
            if (width >= height) image.DecodePixelWidth = maximumPixels;
            else image.DecodePixelHeight = maximumPixels;
        }
        image.EndInit(); image.Freeze();
        return image;
    }
}
