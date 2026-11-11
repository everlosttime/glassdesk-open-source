using System.IO;
using System.Windows.Media.Imaging;

namespace GlassDesk.UI.Imaging;

/// <summary>Retains detailed ICO frames and bounds decoding of large custom images.</summary>
public static class IconBitmapLoader
{
    public static BitmapImage DecodeFile(string path, int maximumPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPixels);
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder is System.Windows.Media.Imaging.IconBitmapDecoder
            ? decoder.Frames.MaxBy(item => (long)item.PixelWidth * item.PixelHeight)!
            : decoder.Frames[0];
        var width = frame.PixelWidth; var height = frame.PixelHeight;
        // BitmapImage otherwise always opens ICO frame zero, often the 16-pixel thumbnail.
        // Re-encode only the selected ICO frame losslessly; ordinary images stay bounded at decode.
        using var selected = decoder is System.Windows.Media.Imaging.IconBitmapDecoder ? new MemoryStream() : null;
        if (selected is not null)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(frame); encoder.Save(selected); selected.Position = 0;
        }
        var image = new BitmapImage(); image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = selected is null ? BitmapCreateOptions.IgnoreImageCache : BitmapCreateOptions.None;
        if (selected is not null) image.StreamSource = selected;
        else image.UriSource = new Uri(path, UriKind.Absolute);
        if (Math.Max(width, height) > maximumPixels)
        {
            if (width >= height) image.DecodePixelWidth = maximumPixels;
            else image.DecodePixelHeight = maximumPixels;
        }
        image.EndInit(); image.Freeze(); return image;
    }
}
