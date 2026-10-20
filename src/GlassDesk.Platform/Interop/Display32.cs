using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// 显示器枚举与 DPI 查询。
/// </summary>
/// <remarks>
/// 这些声明刻意返回**物理像素**：本进程应当声明为 PerMonitorV2 感知，此时
/// <c>EnumDisplayMonitors</c> 给出的是未经缩放的真实像素边界。WPF 的布局用的是 DIP，
/// 两者是不同的坐标空间，混用会导致副屏定位错位——见 <c>CoordinateSpace</c>。
/// </remarks>
internal static partial class Display32
{
    internal const int MonitorInfoPrimary = 0x00000001;

    /// <summary><c>MONITOR_DEFAULTTONEAREST</c>：找不到时返回最近的显示器而不是失败。</summary>
    internal const int MonitorDefaultToNearest = 0x00000002;

    internal const int MonitorDefaultToPrimary = 0x00000001;

    internal delegate bool MonitorEnumProc(nint monitor, nint deviceContext, ref Rect monitorRect, nint data);

    // 这里用 DllImport 而不是 LibraryImport：回调委托无法用源生成封送，
    // 强行改造会把调用点变得比收益更难读。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint deviceContext, nint clipRect, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, int flags);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, int flags);

    /// <summary>取窗口矩形，**物理像素**（每屏感知进程下为真实像素）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    /// <summary>取窗口客户区矩形，**物理像素**。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint window, out Rect rect);

    /// <summary>鼠标指针当前位置，物理像素。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);
}

internal static partial class Dpi32
{
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    /// <summary>当前线程的 DPI 感知上下文。返回值是伪句柄，不能当真指针用。</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindowDpiAwarenessContext(nint window);

    [LibraryImport("user32.dll")]
    internal static partial int GetAwarenessFromDpiAwarenessContext(nint awarenessContext);

    /// <summary>
    /// 比较两个 DPI 感知上下文是否等价。判断清单声明的 PerMonitorV2 只能靠它。
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AreDpiAwarenessContextsEqual(nint awarenessContext1, nint awarenessContext2);

    /// <summary>把窗口移到指定显示器并返回是否成功。用于验证跨屏定位。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}

/// <summary>Win32 <c>RECT</c>，物理像素。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;

    internal readonly int Width => Right - Left;

    internal readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    internal int X;
    internal int Y;
}

/// <summary><c>MONITORINFOEX</c>。<c>szDevice</c> 形如 <c>\\.\DISPLAY1</c>。</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx
{
    internal int Size;
    internal Rect Monitor;
    internal Rect Work;
    internal int Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;
}