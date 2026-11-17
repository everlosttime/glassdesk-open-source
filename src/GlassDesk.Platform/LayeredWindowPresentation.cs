using System.Runtime.InteropServices;

namespace GlassDesk.Platform;

/// <summary>Clears a hidden layered window's retained bitmap without changing its geometry or styles.</summary>
public static class LayeredWindowPresentation
{
    public static unsafe bool ClearHiddenPixels(nint window)
    {
        if (window == 0 || IsWindowVisible(window) || !GetWindowRect(window, out var rect)
            || (GetWindowLongPtrW(window, -20).ToInt64() & 0x80000) == 0) return false;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 8_000_000) return false;
        var dc = CreateCompatibleDC(0);
        if (dc == 0) return false;
        nint bitmap = 0, previous = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) return false;
            new Span<byte>((void*)bits, checked(width * height * 4)).Clear();
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) return false;
            var size = new NativeSize { Width = width, Height = height };
            var source = new NativePoint();
            var blend = new BlendFunction { Alpha = 255, Format = 1 };
            return UpdateLayeredWindow(window, 0, 0, ref size, dc, ref source, 0, ref blend, 2);
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important, Color;
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc,
        nint position, ref NativeSize size, nint sourceDc, ref NativePoint source, uint key, ref BlendFunction blend, uint flags);
}
