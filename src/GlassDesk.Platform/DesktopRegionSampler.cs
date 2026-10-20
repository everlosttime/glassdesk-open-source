using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Platform;

/// <summary>Bounded, memory-only desktop pixels; only runs with verified self-exclusion.</summary>
public sealed class DesktopRegionSampler : IDisposable
{
    private nint _sourceDc, _outputDc, _sourceBitmap, _outputBitmap, _oldSource, _oldOutput, _bits;
    private (int RegionWidth, int RegionHeight, int Width, int Height) _size;
    private byte[]? _pixels;

    public bool TryCapture(PixelRect region, int width, int height, out byte[]? pixels)
        => TryCapture(region, width, height, false, out pixels);

    /// <summary>One-shot capture before Show, with no overlapping visible product HWND.</summary>
    public bool TryCaptureBeforeReveal(PixelRect region, int width, int height, out byte[]? pixels)
        => TryCapture(region, width, height, true, out pixels);

    /// <summary>The caller has completely clipped this owner's panel out of its HWND.</summary>
    public bool TryCaptureBeforeReveal(PixelRect region, int width, int height, nint emptyOwner, out byte[]? pixels)
        => TryCapture(region, width, height, true, out pixels, emptyOwner);

    private bool TryCapture(PixelRect region, int width, int height, bool beforeReveal, out byte[]? pixels, nint emptyOwner = 0)
    {
        pixels = null;
        if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 32_000_000
            || width <= 0 || height <= 0 || width > 2048 || height > 2048 || (long)width * height > 500_000
            || !(beforeReveal ? CaptureExclusionSession.CanCaptureBeforeReveal(region, emptyOwner)
                : CaptureExclusionSession.CanCaptureRegion(region))) return false;
        var screen = GetDC(0);
        if (screen == 0) return false;
        try
        {
            var size = (region.Width, region.Height, width, height);
            if (_size != size || _bits == 0)
            {
                Dispose();
                _sourceDc = CreateCompatibleDC(screen); _outputDc = CreateCompatibleDC(screen);
                _sourceBitmap = CreateCompatibleBitmap(screen, region.Width, region.Height);
                var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, Bits = 32 };
                // BITMAPINFOHEADER is 40 bytes; the structure below deliberately has no palette.
                _outputBitmap = CreateDIBSection(screen, ref info, 0, out _bits, 0, 0);
                if (_sourceDc == 0 || _outputDc == 0 || _sourceBitmap == 0 || _outputBitmap == 0 || _bits == 0)
                { Dispose(); return false; }
                _oldSource = SelectObject(_sourceDc, _sourceBitmap); _oldOutput = SelectObject(_outputDc, _outputBitmap);
                if (_oldSource == 0 || _oldOutput == 0 || _oldSource == -1 || _oldOutput == -1) { Dispose(); return false; }
                if (SetStretchBltMode(_outputDc, 3) == 0) { Dispose(); return false; }
                _size = size; _pixels = new byte[width * height * 4];
            }
            if (!BitBlt(_sourceDc, 0, 0, region.Width, region.Height, screen, region.X, region.Y, 0x40CC0020)
                || !StretchBlt(_outputDc, 0, 0, width, height, _sourceDc, 0, 0, region.Width, region.Height, 0x00CC0020)) return false;
            GdiFlush();
            Marshal.Copy(_bits, _pixels!, 0, _pixels!.Length);
            for (var i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
            pixels = _pixels;
            return true;
        }
        finally { _ = ReleaseDC(0, screen); }
    }

    public void Dispose()
    {
        if (_sourceDc != 0 && _oldSource != 0 && _oldSource != -1) SelectObject(_sourceDc, _oldSource);
        if (_outputDc != 0 && _oldOutput != 0 && _oldOutput != -1) SelectObject(_outputDc, _oldOutput);
        if (_sourceBitmap != 0) DeleteObject(_sourceBitmap);
        if (_outputBitmap != 0) DeleteObject(_outputBitmap);
        if (_sourceDc != 0) DeleteDC(_sourceDc);
        if (_outputDc != 0) DeleteDC(_outputDc);
        _sourceDc = _outputDc = _sourceBitmap = _outputBitmap = _oldSource = _oldOutput = _bits = 0;
        _pixels = null; _size = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, ImportantColors;
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int width, int height, nint src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dst, int x, int y, int width, int height, nint src, int sx, int sy, int sw, int sh, uint op);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
