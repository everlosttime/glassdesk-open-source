using System.Runtime.InteropServices;

namespace GlassDesk.DesktopAcceptance;

/// <summary>屏幕上的一个点（像素，虚拟桌面坐标）。</summary>
internal readonly record struct ScreenPoint(int X, int Y);

/// <summary>虚拟桌面矩形（像素，可为负原点——副屏在主屏左侧时就是负数）。</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;

    internal int Height => Bottom - Top;

    internal bool IsEmpty => Width <= 0 || Height <= 0;

    internal bool Contains(ScreenPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    internal string Describe() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>一个顶层窗口此刻的可观察事实，全部来自操作系统而不是产品内部状态。</summary>
internal sealed record WindowFacts(
    nint Handle,
    string ClassName,
    string Title,
    int ProcessId,
    long ExStyle,
    ScreenRect Rect,
    bool Visible,
    nint Owner)
{
    // 扩展样式的三个位。产品要求程序坞「不抢焦点、不出现在任务栏」，
    // 这两条在窗口属性上的表现就是 NoActivate 与下面的 IsOnTaskbar。
    private const long NoActivate = 0x08000000L;
    private const long ToolWindow = 0x00000080L;
    private const long AppWindow = 0x00040000L;

    internal bool HasNoActivate => (ExStyle & NoActivate) != 0;

    internal bool HasToolWindow => (ExStyle & ToolWindow) != 0;

    internal bool HasAppWindow => (ExStyle & AppWindow) != 0;

    internal bool HasOwner => Owner != 0;

    /// <summary>
    /// 这个窗口此刻会不会出现在任务栏上。
    /// </summary>
    /// <remarks>
    /// 任务栏的规则是：可见、不是工具窗口、<strong>没有属主</strong>、也不是 WS_EX_APPWINDOW。
    /// 这里刻意不把「有 WS_EX_TOOLWINDOW」当作「不在任务栏」的判据——实测过：产品程序坞
    /// 的扩展样式是 <c>0x08080008</c>（NOACTIVATE + LAYERED + TOPMOST，没有 TOOLWINDOW），
    /// 而它确实不在任务栏上。WPF 的 <c>ShowInTaskbar=False</c> 走的正是「让窗口被一个隐藏
    /// 窗口拥有」这条路。因此判据必须看属主，而不是看那一个位。
    /// </remarks>
    internal bool IsOnTaskbar => Visible && !HasToolWindow && !HasOwner && !HasAppWindow;

    internal string Describe() =>
        $"hwnd 0x{Handle:X} 类 {ClassName} 标题「{Title}」pid {ProcessId} "
        + $"矩形 {Rect.Describe()} 可见 {Visible} "
        + $"ExStyle 0x{ExStyle:X8}（NOACTIVATE={HasNoActivate} TOOLWINDOW={HasToolWindow} "
        + $"APPWINDOW={HasAppWindow} 属主 0x{Owner:X}）"
        + $"→ 任务栏上可见 {IsOnTaskbar}";
}

/// <summary>
/// 驱动用到的 Win32 入口。刻意只放驱动真正调用到的那些，
/// 免得这里长成一个没人维护的 interop 垃圾场。
/// </summary>
internal static class Win32
{
    // ---- 窗口属性 ----

    internal const int GwlExStyle = -20;

    /// <summary>GetWindow 的 GW_OWNER：取窗口的属主（没有则是 0）。</summary>
    private const uint GwOwner = 4;

    internal static nint GetForegroundWindow() => GetForegroundWindowRaw();

    internal static WindowFacts? Describe(nint handle)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            return null;
        }

        var className = new char[256];
        var length = GetClassNameRaw(handle, className, className.Length);

        var title = new char[512];
        var titleLength = GetWindowTextRaw(handle, title, title.Length);

        GetWindowThreadProcessId(handle, out var processId);

        var rect = GetWindowRect(handle, out var bounds)
            ? new ScreenRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
            : default;

        // GetWindowLongPtr 的 32 位变体在 64 位进程里是 GetWindowLongPtrW，
        // 但 32 位进程只有 GetWindowLongW。这个驱动只在 64 位 Windows 上跑，
        // 入口点名字由下面两次尝试兜住（窄屏/罕见配置下也不会炸）。
        long exStyle;
        try
        {
            exStyle = GetWindowLongPtrWrapper(handle, GwlExStyle).ToInt64();
        }
        catch (EntryPointNotFoundException)
        {
            exStyle = GetWindowLongRaw(handle, GwlExStyle);
        }

        return new WindowFacts(
            handle,
            new string(className, 0, Math.Max(0, length)),
            new string(title, 0, Math.Max(0, titleLength)),
            (int)processId,
            exStyle,
            rect,
            IsWindowVisible(handle),
            // GW_OWNER, not GA_ROOTOWNER: the latter walks the owner chain to its root and
            // returns the window itself when there is no owner, which would make every window
            // look owned.
            GetWindowRaw(handle, GwOwner));
    }

    /// <summary>枚举顶层窗口。用于诊断输出与「点上的窗口属于谁」的核对。</summary>
    internal static List<nint> TopLevelWindows()
    {
        var handles = new List<nint>();

        _ = EnumWindows((handle, _) =>
        {
            handles.Add(handle);
            return true;
        }, 0);

        return handles;
    }

    /// <summary>坐标下方的窗口句柄。分层窗口的 alpha 0 区域会穿透，与真实点击一致。</summary>
    internal static nint WindowAt(ScreenPoint point) => WindowFromPointRaw(new Point32 { X = point.X, Y = point.Y });

    /// <summary>从任意子窗口一路找上去的顶层窗口。</summary>
    internal static nint RootAncestor(nint handle) => GetAncestorRaw(handle, 2 /* GA_ROOT */);

    /// <summary>
    /// 给一个窗口发关闭请求。
    /// </summary>
    /// <remarks>
    /// 只发消息，不结束进程。资源管理器是用户共用的进程，绝不能被测试结束；
    /// 而且在关之前还要核对窗口标题里含本次唯一的文件夹名。
    /// </remarks>
    internal static bool RequestClose(nint handle) =>
        handle != 0 && PostMessage(handle, 0x0010 /* WM_CLOSE */, 0, 0);

    internal static int ProcessOf(nint handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return (int)processId;
    }

    // ---- 坐标与显示 ----

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    internal static ScreenRect VirtualScreen() => new(
        GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmXVirtualScreen) + GetSystemMetrics(SmCxVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen) + GetSystemMetrics(SmCyVirtualScreen));

    internal static int SystemDpi()
    {
        var screen = GetDcScreen(0);
        if (screen == 0)
        {
            return 96;
        }

        try
        {
            return GetDeviceCaps(screen, 88 /* LOGPIXELSX */);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// 本进程的 DPI 感知级别。
    /// </summary>
    /// <remarks>
    /// 必须写进证据：不感知的进程拿到的坐标是系统缩放过的，而 UIA 的控件矩形是物理像素，
    /// 两者混用会让点击落到别处。驱动靠 app.manifest 声明 PerMonitorV2，这里把它读回来
    /// 证明声明真的生效了，而不是只在文件里写着。
    /// </remarks>
    internal static string DpiAwareness()
    {
        try
        {
            var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());

            return awareness switch
            {
                0 => "未感知（坐标会被系统缩放，与 UIA 的物理像素不是同一空间）",
                1 => "系统 DPI",
                2 => "每显示器 DPI v2",
                _ => $"未知（{awareness}）",
            };
        }
        catch (EntryPointNotFoundException)
        {
            return "（这台系统读不出 DPI 感知级别）";
        }
    }

    /// <summary>逐个显示器的物理矩形、工作区与主屏身份。</summary>
    internal static List<(ScreenRect Rect, ScreenRect Work, bool Primary)> Monitors()
    {
        var monitors = new List<(ScreenRect, ScreenRect, bool)>();

        _ = EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MonitorInfo32 { Size = Marshal.SizeOf<MonitorInfo32>() };

            if (GetMonitorInfoRaw(handle, ref info))
            {
                monitors.Add((
                    new ScreenRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
                    new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
                    (info.Flags & 1) != 0));
            }

            return true;
        }, 0);

        return monitors;
    }

    // ---- 桌面与会话 ----

    /// <summary>
    /// 当前输入桌面的名字。交互式已登录桌面叫 "Default"；
    /// 锁屏后输入桌面会变成 "Winlogon"——那是「环境阻塞」，不是产品失败。
    /// </summary>
    internal static string? InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (desktop == 0)
        {
            return null;
        }

        try
        {
            var buffer = new byte[512];
            if (!GetUserObjectInformation(desktop, 2 /* UOI_NAME */, buffer, buffer.Length, out var needed))
            {
                return null;
            }

            // 名字是 UTF-16，长度以字节计。
            var text = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2));
            return text.TrimEnd('\0');
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    /// <summary>本进程自己的桌面名字。与输入桌面不一致说明我们不在用户正看着的桌面上。</summary>
    internal static string? CurrentThreadDesktopName()
    {
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == 0)
        {
            return null;
        }

        var buffer = new byte[512];
        if (!GetUserObjectInformation(desktop, 2 /* UOI_NAME */, buffer, buffer.Length, out var needed))
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2));
        return text.TrimEnd('\0');
    }

    internal static uint ActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    /// <summary>
    /// 本进程的窗口站是否「可见」——也就是是否连着一个能显示窗口的交互式桌面。
    /// </summary>
    /// <remarks>
    /// 这是判「服务会话 vs 真实桌面」的官方依据（<c>WSF_VISIBLE</c>）。
    /// 服务会话里的窗口站不可见，窗口画不出来，也就谈不上鼠标键盘语义。
    /// 刻意<strong>不</strong>拿「控制台会话号」来比：经远程桌面或虚拟机增强会话接入时，
    /// 控制台会话是另一个号，那样比会把合法的交互式会话误判成阻塞。
    /// </remarks>
    internal static bool WindowStationIsVisible()
    {
        var station = GetProcessWindowStation();
        if (station == 0)
        {
            return false;
        }

        var flags = default(UserObjectFlags);
        return GetUserObjectInformationFlags(station, 1 /* UOI_FLAGS */, ref flags, Marshal.SizeOf<UserObjectFlags>(), out _)
            && (flags.Flags & 1 /* WSF_VISIBLE */) != 0;
    }

    // ---- 截图用的 GDI ----

    internal const int DibRgbColors = 0;
    internal const uint BiRgb = 0;
    internal const uint Srccopy = 0x00CC0020;
    internal const uint Captureblt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader32
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo32
    {
        internal BitmapInfoHeader32 Header;
        internal uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint dc, nint handle);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint handle);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo32 info, uint usage);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern nint GetDcScreen(nint window);

    // ---- 显示器枚举 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo32
    {
        internal int Size;
        internal Rect32 Monitor;
        internal Rect32 Work;
        internal uint Flags;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoRaw(nint monitor, ref MonitorInfo32 info);

    // ---- 窗口 ----

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint data);

    private delegate bool EnumWindowsProc(nint handle, nint data);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameRaw(nint handle, [Out] char[] buffer, int length);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextRaw(nint handle, [Out] char[] buffer, int length);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out Rect32 rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint handle, int index);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint handle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint GetWindowRaw(nint handle, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindowRaw();

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint handle, uint message, nint wparam, nint lparam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint WindowFromPointRaw(Point32 point);

    // ---- 桌面 ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, [Out] byte[] info, int length, out int needed);

    /// <remarks>
    /// 结构体有三个字段（<c>fInherit</c>、<c>fReserved</c>、<c>dwFlags</c>），不是两个。
    /// 只声明两个字段会读错字段：<c>dwFlags</c> 落在结构体之外，读回来恒为 0，
    /// 于是每个交互式会话都会被误判成服务会话。实测过一次，这里的字段顺序是必须的。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct UserObjectFlags
    {
        internal int Inherit;
        internal int Reserved;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    private static extern bool GetUserObjectInformationFlags(nint handle, int index, ref UserObjectFlags info, int length, out int needed);

    // ---- 小包装：把「32 位还是 64 位入口点」这件事收在一处 ----

    private static nint GetWindowLongPtrWrapper(nint handle, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : GetWindowLong32(handle, index);

    private static long GetWindowLongRaw(nint handle, int index) => GetWindowLong32(handle, index);

    private static nint GetAncestorRaw(nint handle, uint flags) => GetAncestor(handle, flags);

    /// <summary>整屏像素（BGRA，自顶向下）。截图证据用它，不依赖任何外部图像库。</summary>
    internal static byte[] CaptureBgra(ScreenRect area, out int stride)
    {
        stride = area.Width * 4;

        var screen = GetDcScreen(0);
        if (screen == 0)
        {
            throw new InvalidOperationException("拿不到屏幕设备上下文，截图不可用。");
        }

        var memory = nint.Zero;
        var bitmap = nint.Zero;

        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);

            var previous = SelectObject(memory, bitmap);
            try
            {
                if (!BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, Srccopy | Captureblt))
                {
                    throw new InvalidOperationException("BitBlt 失败，截图不可用。");
                }
            }
            finally
            {
                _ = SelectObject(memory, previous);
            }

            var info = new BitmapInfo32
            {
                Header = new BitmapInfoHeader32
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader32>(),
                    // 负高度 = 自顶向下的行序，省掉一次翻转。
                    Width = area.Width,
                    Height = -area.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)(stride * area.Height),
                },
            };

            var pixels = new byte[stride * area.Height];
            var lines = GetDIBits(memory, bitmap, 0, (uint)area.Height, pixels, ref info, DibRgbColors);

            if (lines == 0)
            {
                throw new InvalidOperationException("GetDIBits 失败，截图不可用。");
            }

            return pixels;
        }
        finally
        {
            if (bitmap != 0)
            {
                _ = DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                _ = DeleteDC(memory);
            }

            _ = ReleaseDC(0, screen);
        }
    }
}
