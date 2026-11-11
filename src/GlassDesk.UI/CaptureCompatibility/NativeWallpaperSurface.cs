using System.ComponentModel;
using System.Runtime.InteropServices;

internal sealed class NativeWallpaperSurface : IDisposable
{
    private readonly nint _window;
    private readonly int _width, _height, _stride;
    private nint _bitmap, _bits;
    private bool _disposed;
    internal bool HasBitmap => _bitmap != 0;

    internal NativeWallpaperSurface(nint window, int width, int height)
    {
        if (window == 0) throw new ArgumentException("A wallpaper carrier is required.", nameof(window));
        if (width <= 0 || height <= 0 || (long)width * height > 32_000_000)
            throw new ArgumentOutOfRangeException(nameof(width));
        _window = window;
        _width = width;
        _height = height;
        _stride = checked((width * 3 + 3) & ~3);
    }

    private void EnsureBitmap()
    {
        if (_bitmap != 0) return;
        var info = new BitmapInfo { Header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = _width, Height = -_height,
            Planes = 1, BitCount = 24
        }};
        _bitmap = CreateDIBSection(0, ref info, 0, out _bits, 0, 0);
        if (_bitmap == 0 || _bits == 0)
        {
            var error = Marshal.GetLastWin32Error();
            ReleaseBitmap();
            throw new Win32Exception(error, "Cannot create the native wallpaper bitmap.");
        }
    }

    internal unsafe void Update(byte[] pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(_width * _height * 4))
            throw new ArgumentException("Wallpaper geometry does not match the scene.", nameof(pixels));
        EnsureBitmap();
        // The DIB already owns the destination storage. Write its BGR rows directly
        // instead of retaining a second full-size managed image and copying it.
        GdiFlush();
        var destination = new Span<byte>((void*)_bits, checked(_stride * _height));
        for (var row = 0; row < _height; row++)
        {
            var input = row * _width * 4;
            var output = row * _stride;
            for (var column = 0; column < _width; column++, input += 4, output += 3)
            {
                destination[output] = pixels[input];
                destination[output + 1] = pixels[input + 1];
                destination[output + 2] = pixels[input + 2];
            }
            destination.Slice(row * _stride + _width * 3, _stride - _width * 3).Clear();
        }
        SendMessageW(_window, 0x0172, 0, _bitmap);
        InvalidateRect(_window, 0, false);
        UpdateWindow(_window);
        Marshal.ThrowExceptionForHR(DwmFlush());
    }

    // Direct-background consumers do not read this carrier. Retain its HWND and
    // geometry, but recreate pixel storage only when composition needs it again.
    internal void ReleaseBitmap()
    {
        if (_bitmap == 0) return;
        SendMessageW(_window, 0x0172, 0, 0);
        DeleteObject(_bitmap);
        _bitmap = _bits = 0;
    }

    public void Dispose()
    {
        ReleaseBitmap();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint context,
        ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rectangle, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
