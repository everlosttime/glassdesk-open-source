using System.ComponentModel;
using System.Runtime.InteropServices;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Launcher;

internal sealed class UnifiedTransitionHost : IDisposable
{
    private const string ClassName = "GlassDesk.UnifiedTransitionHost.v1";
    private static readonly WindowProcedure Procedure = WndProc;
    private static bool _registered;
    private PixelRect? _bounds;
    private bool _visible;
    private readonly nint _owner;
    internal nint Handle { get; private set; }

    internal UnifiedTransitionHost(nint owner)
    {
        _owner = owner;
        if (!_registered)
        {
            var descriptor = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Name = ClassName };
            if (RegisterClassEx(ref descriptor) == 0 && Marshal.GetLastWin32Error() != 1410) throw new Win32Exception();
            _registered = true;
        }
        Handle = CreateWindowEx(0x082800A0, ClassName, "GlassDesk transition", 0x80000000, 0, 0, 1, 1, 0, 0, GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception();
        if (!SetLayeredWindowAttributes(Handle, 0, 255, 2) || !WindowShadowStyles.IsInputTransparent(Handle))
        { var error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
    }

    internal void Update(PixelRect bounds, bool visible, bool reconcileOrder = false)
    {
        if (Handle == 0) return;
        if (_bounds != bounds)
        {
            if (!DisplayService.MoveWindowToPixelRect(Handle, bounds)) throw new Win32Exception();
            _bounds = bounds;
        }
        if (visible == _visible && (!reconcileOrder || !visible || GetWindow(_owner, 2) == Handle)) return;
        if (visible)
        {
            if (!WindowShadowStyles.PositionBehind(Handle, _owner, bounds)
                || !SetWindowPos(Handle, _owner, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x0200)) throw new Win32Exception();
        }
        else _ = ShowWindow(Handle, 0);
        _visible = visible;
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        _ = DestroyWindow(Handle); Handle = 0; _visible = false;
    }
    internal void Hide()
    {
        if (Handle != 0) _ = ShowWindow(Handle, 0);
        _visible = false;
    }
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x000F) { _ = ValidateRect(window, 0); return 0; }
        if (message == 0x0014) return 1;
        if (message == 0x0084) return new(-1);
        if (message == 0x0021) return new(3);
        return DefWindowProc(window, message, wParam, lParam);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        internal uint Size, Style; internal nint Procedure; internal int ClassBytes, WindowBytes;
        internal nint Instance, Icon, Cursor, Background; internal string? Menu, Name; internal nint SmallIcon;
    }
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass descriptor);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ValidateRect(nint hwnd, nint rect);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
