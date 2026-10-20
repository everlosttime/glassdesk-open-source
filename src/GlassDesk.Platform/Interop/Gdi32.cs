using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// 用于把渲染好的位图变成 <c>HICON</c> 的 GDI 与图标入口点。
/// </summary>
/// <remarks>
/// 产品不附带第三方图标库，也不借用现成图形，所以托盘与
/// 窗口上的标记由我们自己绘制，再通过这些调用交给外壳。
/// </remarks>
internal static partial class Gdi32
{
    internal const int DibRgbColors = 0;
    internal const int BiBitfields = 3;

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    internal static partial nint CreateDibSection(nint dc, ref BitmapV5Header header, int usage, out nint bits, nint section, int offset);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    internal static partial nint CreateIconIndirect(ref IconInfo iconInfo);

    // ---- 从位图读回像素（阶段 3 的图标提取） ----

    internal const int DibRgb = 0;

    /// <summary>
    /// 读一个 GDI 对象的头信息。
    /// </summary>
    /// <remarks>
    /// 返回值是写入的字节数，0 表示失败。这里只用来问位图的宽高与位深——
    /// <c>GetDIBits</c> 自己不会告诉你源位图有多大。
    /// </remarks>
    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    internal static partial int GetObject(nint handle, int size, out Bitmap target);

    /// <summary>
    /// 把位图的像素复制出来。
    /// </summary>
    /// <remarks>
    /// 请求 32 位、且头里的高度取<strong>负数</strong>，得到的就是自上而下、
    /// 每像素 BGRA 的连续缓冲区，与 PNG 的排列一致，不必再翻转。
    /// </remarks>
    internal static unsafe int GetDIBits(nint deviceContext, nint bitmap, uint start, uint lines, byte* bits, BitmapInfo* info)
        => GetDIBitsCore(deviceContext, bitmap, start, lines, bits, info, DibRgb);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")]
    private static unsafe partial int GetDIBitsCore(nint deviceContext, nint bitmap, uint start, uint lines, byte* bits, BitmapInfo* info, int usage);
}

/// <summary><c>BITMAP</c>。字段顺序与原生一致，用来问出宽高与位深。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Bitmap
{
    internal int Type;
    internal int Width;
    internal int Height;
    internal int WidthBytes;
    internal ushort Planes;
    internal ushort BitsPerPixel;
    internal nint Bits;
}

/// <summary><c>BITMAPINFOHEADER</c>。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    internal int Size;
    internal int Width;
    internal int Height;
    internal ushort Planes;
    internal ushort BitCount;
    internal int Compression;
    internal int SizeImage;
    internal int XPelsPerMeter;
    internal int YPelsPerMeter;
    internal int ColorsUsed;
    internal int ColorsImportant;
}

/// <summary>
/// <c>BITMAPINFO</c>：一个头加一张颜色表。
/// </summary>
/// <remarks>
/// 颜色表在这里只占位。我们一律请求 32 位真彩，系统不会去读它，
/// 但结构体的大小仍要覆盖首项，否则 <c>GetDIBits</c> 写回时会越界。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfo
{
    internal BitmapInfoHeader Header;
    internal uint Colors;
}

/// <summary>
/// <c>BITMAPV5HEADER</c>。使用版本 5 的头，是因为它能携带显式的
/// 通道掩码，这正是带真实 alpha 通道的 32 位位图得以成立的原因。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapV5Header
{
    internal int Size;
    internal int Width;
    internal int Height;
    internal ushort Planes;
    internal ushort BitCount;
    internal int Compression;
    internal int SizeImage;
    internal int XPelsPerMeter;
    internal int YPelsPerMeter;
    internal int ColorsUsed;
    internal int ColorsImportant;

    internal uint RedMask;
    internal uint GreenMask;
    internal uint BlueMask;
    internal uint AlphaMask;

    internal uint CsType;
    internal int Endpoints0;
    internal int Endpoints1;
    internal int Endpoints2;
    internal int Endpoints3;
    internal int Endpoints4;
    internal int Endpoints5;
    internal int Endpoints6;
    internal int Endpoints7;
    internal int Endpoints8;

    internal uint GammaRed;
    internal uint GammaGreen;
    internal uint GammaBlue;

    internal int Intent;
    internal int ProfileData;
    internal int ProfileSize;
    internal int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IconInfo
{
    /// <summary>
    /// Win32 的 <c>BOOL</c>，用 <see cref="int"/> 而不是
    /// <see cref="bool"/> 表达，好让结构体保持 blittable。被封送的 bool 会强制
    /// 启用运行时封送器，从而排除上面由源生成的 P/Invoke。
    /// </summary>
    internal int IsIcon;

    internal int HotspotX;
    internal int HotspotY;
    internal nint MaskBitmap;
    internal nint ColorBitmap;
}