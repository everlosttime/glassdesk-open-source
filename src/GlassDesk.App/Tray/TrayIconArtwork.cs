using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Platform;

namespace GlassDesk.App.Tray;

/// <summary>Renders the shared GlassDesk brand artwork for windows and the tray.</summary>
internal static class TrayIconArtwork
{
    /// <summary>渲染标识并返回图标句柄。句柄由调用方负责释放。</summary>
    public static nint CreateHandle(int size)
    {
        var bitmap = Render(size);
        var pixels = ToStraightBgra(bitmap);
        return IconFactory.CreateIconFromBgra(pixels, size, size);
    }

    /// <summary>Renders the same mark as a WPF image for application windows.</summary>
    public static BitmapSource RenderBitmap(int size) => Render(size);

    private static RenderTargetBitmap Render(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var mark = Application.Current?.TryFindResource("GlassDeskBrandMark") as DrawingImage
            ?? throw new InvalidOperationException("The GlassDesk brand mark resource is unavailable.");
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var context = visual.RenderOpen())
        {
            var scale = size / 256.0;
            context.PushTransform(new ScaleTransform(scale, scale));
            context.DrawDrawing(mark.Drawing);
            context.Pop();
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// 转换为图标位图所需的直通（非预乘）BGRA。
    /// </summary>
    private static byte[] ToStraightBgra(BitmapSource source)
    {
        // Bgra32 是直通 alpha 格式；从预乘的渲染目标转换过来
        // 就顺带替我们解除了预乘。
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
