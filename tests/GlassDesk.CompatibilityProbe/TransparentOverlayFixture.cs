using System.ComponentModel;
using System.Runtime.InteropServices;

internal sealed class TransparentOverlayFixture : IDisposable
{
    private static readonly WindowProcedure Procedure = DefWindowProcW;
    private nint _window;

    internal TransparentOverlayFixture(string mode)
    {
        var className = mode == "other-alpha-zero" ? "GlassDesk transparent fixture" : "CEF-OSC-WIDGET";
        var definition = new WindowClass { ClassName = className,
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Background = GetStockObject(4) };
        if (RegisterClassW(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var fullMonitor = mode == "cef-screen-helper";
        var horizontal = fullMonitor ? 0 : 260;
        var vertical = fullMonitor ? 0 : 160;
        var width = fullMonitor ? GetSystemMetrics(0) : 360;
        var height = fullMonitor ? GetSystemMetrics(1) : 240;
        _window = CreateWindowExW(0x08080080, className, "", 0x94000000, horizontal, vertical, width, height, 0, 0, 0, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (mode == "cef-per-pixel") InitializePerPixel();
        else if (!SetLayeredWindowAttributes(_window, 0,
            mode is "cef-opaque" or "cef-colorkey" or "cef-screen-helper" ? (byte)255 : (byte)0, mode == "cef-colorkey" ? 3u : 2u))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowPos(_window, 1, horizontal, vertical, width, height, 0x50))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void SetOpacity(byte alpha)
    {
        if (!SetLayeredWindowAttributes(_window, 0, alpha, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void InitializePerPixel()
    {
        var context = CreateCompatibleDC(0);
        var info = new BitmapInfo { Size = 40, Width = 360, Height = -240, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(context, ref info, 0, out var bits, 0, 0);
        var previous = SelectObject(context, bitmap);
        try
        {
            if (context == 0 || bitmap == 0 || bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(new byte[360 * 240 * 4], 0, bits, 360 * 240 * 4);
            var destination = new Point(260, 160);
            var origin = new Point();
            var size = new Point(360, 240);
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(_window, 0, ref destination, ref size, context, ref origin, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (previous != 0) SelectObject(context, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (context != 0) DeleteDC(context);
        }
    }

    public void Dispose()
    {
        if (_window != 0) DestroyWindow(_window);
        _window = 0;
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point(int horizontal, int vertical) { public int Horizontal = horizontal, Vertical = vertical; }
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle, string className,
        string name, uint style, int horizontal, int vertical, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int horizontal, int vertical, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint screenContext, ref Point destination,
        ref Point size, nint sourceContext, ref Point origin, uint color, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint context);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint context, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint context);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint context, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
}
