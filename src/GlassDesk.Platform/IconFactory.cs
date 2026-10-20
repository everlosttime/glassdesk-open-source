using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 用我们自己渲染的原始像素数据构建 Windows 图标句柄。
/// </summary>
/// <remarks>
/// 直接接收 BGRA 字节而不是 WPF 位图，使本程序集不依赖任何 UI
/// 框架，图形怎么生成由调用方决定。
///
/// 这里返回的每个句柄都归调用方所有，必须用
/// <see cref="DestroyIcon"/> 释放。
/// </remarks>
public static class IconFactory
{
    /// <summary>从自上而下的 BGRA 像素创建带 alpha 通道的 32 位图标。</summary>
    /// <param name="bgra">按 BGRA 排列的像素，首行为顶部，每像素 4 字节。</param>
    public static nint CreateIconFromBgra(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expected = width * height * 4;
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"像素数据长度不足：需要 {expected} 字节，实际 {bgra.Length} 字节。", nameof(bgra));
        }

        nint colorBitmap = 0;
        nint maskBitmap = 0;
        var deviceContext = Gdi32.GetDC(0);

        try
        {
            // 高度取负表示自上而下的位图，与我们数据的顺序一致。
            var header = new BitmapV5Header
            {
                Size = Marshal.SizeOf<BitmapV5Header>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = Gdi32.BiBitfields,
                SizeImage = expected,
                RedMask = 0x00FF0000,
                GreenMask = 0x0000FF00,
                BlueMask = 0x000000FF,
                AlphaMask = 0xFF000000,
            };

            colorBitmap = Gdi32.CreateDibSection(deviceContext, ref header, Gdi32.DibRgbColors, out var bits, 0, 0);
            if (colorBitmap == 0 || bits == 0)
            {
                return 0;
            }

            Marshal.Copy(bgra, 0, bits, expected);

            // 全零的 1bpp 掩码表示「不遮掉任何像素」；透明度由 alpha 提供。
            var maskStride = ((width + 31) / 32) * 4;
            maskBitmap = Gdi32.CreateBitmap(width, height, 1, 1, new byte[maskStride * height]);
            if (maskBitmap == 0)
            {
                return 0;
            }

            var info = new IconInfo
            {
                IsIcon = 1,
                MaskBitmap = maskBitmap,
                ColorBitmap = colorBitmap,
            };

            return Gdi32.CreateIconIndirect(ref info);
        }
        finally
        {
            if (deviceContext != 0)
            {
                // 释放 DC 失败在这里没有可采取的动作；图标此时要么已经建成，
                // 要么失败已通过空句柄报告出去了。
                _ = Gdi32.ReleaseDC(0, deviceContext);
            }

            // CreateIconIndirect 会复制两张位图，所以它一返回就可以释放。
            // 不在这里释放的话，每次重建图标都会泄漏。
            if (colorBitmap != 0)
            {
                Gdi32.DeleteObject(colorBitmap);
            }

            if (maskBitmap != 0)
            {
                Gdi32.DeleteObject(maskBitmap);
            }
        }
    }

    /// <summary>释放由 <see cref="CreateIconFromBgra"/> 生成的图标句柄。</summary>
    public static void DestroyIcon(nint icon)
    {
        if (icon != 0)
        {
            User32.DestroyIcon(icon);
        }
    }
}