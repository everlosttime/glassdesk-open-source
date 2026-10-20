using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Displays;

/// <summary>
/// 显示器枚举、DPI 感知探测，以及物理像素与 DIP 之间的换算。
/// </summary>
/// <remarks>
/// 基线报告记录过一个真实缺陷：参考启动台把低级鼠标钩子报告的**物理像素**直接与
/// Electron 的 **DIP** 显示器边界比较，在副屏上相差 (1024, 246)，导致副屏热角永不触发、
/// 或在大范围内误触发。
///
/// 本产品不依赖那套钩子，但同一个陷阱在原生实现里依然存在。因此这里的原则是：
/// **凡是坐标，先问清楚是哪个空间**。<see cref="DisplayInfo"/> 上的换算方法就是为了
/// 让这件事只有一个落点，而不是在每个调用点各自心算。
/// </remarks>
public static partial class DisplayService
{
    // DPI 感知上下文的伪句柄。它们不是真指针，只是约定值。
    private static readonly nint ContextUnaware = -1;
    private static readonly nint ContextSystemAware = -2;
    private static readonly nint ContextPerMonitorAware = -3;
    private static readonly nint ContextPerMonitorAwareV2 = -4;
    private static readonly nint ContextUnawareGdiScaled = -5;

    /// <summary>
    /// 枚举所有显示器。边界与工作区均为**物理像素**。
    /// </summary>
    /// <remarks>
    /// 本进程若已声明为每屏 DPI 感知，这里拿到的就是未经缩放的真实像素；若不是，
    /// 系统会把结果按当前缩放拉伸，数值看起来「差不多」却是错的——这也是为什么
    /// <see cref="ProbeDpiAwareness"/> 要和枚举结果一起看。
    /// </remarks>
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var displays = new List<DisplayInfo>();

        // 用列表而不是闭包捕获：枚举回调不能抛异常穿过原生栈。
        // 参数必须显式写出类型——委托里有 ref 参数，lambda 无法靠推断匹配。
        Display32.MonitorEnumProc callback = (nint monitor, nint deviceContext, ref Rect monitorRect, nint data) =>
        {
            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty,
            };

            if (Display32.GetMonitorInfo(monitor, ref info))
            {
                displays.Add(new DisplayInfo(
                    DeviceName: info.DeviceName,
                    IsPrimary: (info.Flags & Display32.MonitorInfoPrimary) != 0,
                    Bounds: ToPixelRect(info.Monitor),
                    WorkArea: ToPixelRect(info.Work),
                    Dpi: DpiForMonitor(monitor)));
            }

            return true;
        };

        Display32.EnumDisplayMonitors(0, 0, callback, 0);

        // 主屏排在最前，其余按设备名排序，保证同一台机器上每次枚举顺序一致。
        return displays
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>取主屏；枚举失败时返回 null。</summary>
    public static DisplayInfo? Primary() => Enumerate().FirstOrDefault(static d => d.IsPrimary);

    /// <summary>
    /// 取鼠标指针当前的位置，**物理像素**。
    /// </summary>
    /// <remarks>
    /// 自动隐藏的热区探测用它：热区在目标显示器的工作区边缘上，
    /// 判定必须与 <see cref="Enumerate"/> 的坐标同处一个空间。
    /// 读不到时返回 null（例如远控会话下光标不可用）——调用方按
    /// 「指针不在任何热区」处理，绝不当作错误。
    /// </remarks>
    public static (int X, int Y)? GetCursorPixelPoint()
    {
        try
        {
            return Display32.GetCursorPos(out var point) ? (point.X, point.Y) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>按设备名查显示器，例如 <c>\\.\DISPLAY2</c>。</summary>
    /// <remarks>
    /// 用设备名而不是索引来持久化「Dock 在哪块屏」：索引在不同会话之间会变，设备名不会。
    /// </remarks>
    public static DisplayInfo? FindByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        return Enumerate()
            .FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按设备名查显示器，找不到时回落到主屏。</summary>
    /// <remarks>
    /// 显示器被拔掉之后 Dock 必须回到主屏，而不是留在屏幕外——所以这里刻意不返回 null。
    /// </remarks>
    public static DisplayInfo? ResolveOrPrimary(string? deviceName) => FindByDeviceName(deviceName) ?? Primary();

    /// <summary>给定物理像素坐标，返回它落在哪块显示器上。</summary>
    public static DisplayInfo? FromPixelPoint(int x, int y)
    {
        var handle = Display32.MonitorFromPoint(new Point { X = x, Y = y }, Display32.MonitorDefaultToNearest);
        if (handle == 0)
        {
            return null;
        }

        var info = new MonitorInfoEx
        {
            Size = Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };

        if (!Display32.GetMonitorInfo(handle, ref info))
        {
            return null;
        }

        return new DisplayInfo(
            info.DeviceName,
            (info.Flags & Display32.MonitorInfoPrimary) != 0,
            ToPixelRect(info.Monitor),
            ToPixelRect(info.Work),
            DpiForMonitor(handle));
    }

    /// <summary>探测当前进程的 DPI 感知等级。</summary>
    public static DpiAwarenessInfo ProbeDpiAwareness()
    {
        var context = Dpi32.GetThreadDpiAwarenessContext();
        var coarse = Dpi32.GetAwarenessFromDpiAwarenessContext(context);

        var level = coarse switch
        {
            0 => DpiAwarenessLevel.Unaware,
            1 => DpiAwarenessLevel.SystemAware,
            2 => DpiAwarenessLevel.PerMonitorAware,
            _ => DpiAwarenessLevel.Unknown,
        };

        return new DpiAwarenessInfo(
            Level: level,
            IsPerMonitorV2: IsContext(context, ContextPerMonitorAwareV2),
            RawContext: DescribeContext(context),
            SystemDpi: Dpi32.GetDpiForSystem());
    }

    /// <summary>
    /// 比较两个 DPI 感知上下文是否等价。
    /// </summary>
    /// <remarks>
    /// 必须用这个 API，不能直接比较句柄数值。当感知等级来自清单声明时，
    /// <c>GetThreadDpiAwarenessContext</c> 返回的是真实句柄（实测形如 0x9011），
    /// 而不是 <c>DPI_AWARENESS_CONTEXT_*</c> 那几个约定值；只有通过
    /// <c>SetThreadDpiAwarenessContext</c> 主动设置时才会拿到约定值。
    /// 直接比较数值会把清单声明的 PerMonitorV2 误判成「未知」。
    /// </remarks>
    private static bool IsContext(nint context, nint reference)
    {
        try
        {
            return Dpi32.AreDpiAwarenessContextsEqual(context, reference);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老系统没有这个 API，只能退回不区分 V1/V2。
            return false;
        }
    }

    /// <summary>取某个窗口所在显示器的 DPI。</summary>
    public static uint DpiForWindow(nint window)
    {
        if (window == 0)
        {
            return 96;
        }

        var dpi = Dpi32.GetDpiForWindow(window);
        return dpi == 0 ? 96 : dpi;
    }

    /// <summary>
    /// 把窗口按**物理像素**矩形摆放。
    /// </summary>
    /// <remarks>
    /// 这是跨屏定位唯一可靠的路径。实测过 WPF 的 <c>Left</c>/<c>Top</c>：它们的
    /// DIP→物理换算用的是**窗口当前所在显示器**的 DPI，而不是目标显示器的。
    /// 于是在混合 DPI 环境里一次性赋值跨屏必然偏移——实测副屏 X 偏了 640 像素
    /// （当前在主屏 150%，目标是副屏 125%）。而 Win32 的窗口矩形本来就是物理像素，
    /// 不存在这个歧义。
    /// </remarks>
    public static bool MoveWindowToPixelRect(nint window, PixelRect rect)
    {
        if (window == 0)
        {
            return false;
        }

        // 两次摆放，中间让已排队的窗口消息跑完。
        //
        // 这不是冗余。一次到位的物理摆放**位置对、尺寸错**：窗口落到目标显示器后，
        // 界面框架（WPF 尤其明显）会收到 DPI 变更通知，并按它**过期的 DIP 尺寸**
        // 重新缩放窗口。实测在 125% 的副屏上 320×80 被改成 267×67，正是 120/144 的比例。
        //
        // 两件事都封在这里，是为了让调用方无从踩这个坑：早先只有探测程序自己调了两次
        // 才通过，而公共方法只摆一次——那种「正确用法只写在测试里」的封装比不封装更危险。
        if (!TrySetPositionOnce(window, rect))
        {
            return false;
        }

        DrainPendingWindowMessages(window);

        return TrySetPositionOnce(window, rect);
    }

    private static bool TrySetPositionOnce(nint window, PixelRect rect)
    {
        // 不改 Z 序、不激活：定位不该顺带把窗口提到最前或抢走焦点。
        const uint SwpNoZOrder = 0x0004;
        const uint SwpNoActivate = 0x0010;

        return Dpi32.SetWindowPos(
            window,
            0,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>
    /// 把该窗口已排队的消息处理掉，但不碰别的窗口的消息。
    /// </summary>
    /// <remarks>
    /// 只取该窗口的消息（<c>PeekMessage</c> 指定 hwnd），因此不会顺手把别的窗口的
    /// 消息也派发掉——那会给调用方带来难以排查的副作用。
    ///
    /// 有上限：万一某个消息不断自我投递，这里也不能变成死循环。到上限就返回，
    /// 后面的第二次摆放仍然会执行。
    ///
    /// <b>必须在拥有该窗口的线程上调用。</b>这是消息泵的基本约束：在别的线程上派发
    /// 该窗口的消息，等于在错误的线程上执行它的窗口过程。
    /// </remarks>
    private static void DrainPendingWindowMessages(nint window)
    {
        const int MaxMessages = 64;

        for (var i = 0; i < MaxMessages; i++)
        {
            // 先不动队列地看一眼：退出消息必须留给真正的消息循环，
            // 被我们取走会让程序收不到退出通知、无法正常结束。
            if (!User32.PeekMessage(out var message, window, 0, 0, User32.PmNoRemove))
            {
                return;
            }

            if (message.Message == User32.WmQuit)
            {
                return;
            }

            if (!User32.PeekMessage(out message, window, 0, 0, User32.PmRemove))
            {
                return;
            }

            _ = User32.TranslateMessage(in message);
            _ = User32.DispatchMessage(in message);
        }
    }

    /// <summary>取窗口在屏幕上的矩形，**物理像素**。</summary>
    /// <remarks>
    /// 包装在这里而不是让界面层直接调用互操作：一方面内部声明不该泄漏出去，
    /// 另一方面「这个值是物理像素」这件事需要一个显式类型
    /// （<see cref="PixelRect"/>）来承载，否则很容易和 WPF 的 DIP 混用。
    /// </remarks>
    public static PixelRect? GetWindowPixelBounds(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        return Display32.GetWindowRect(window, out var rect)
            ? ToPixelRect(rect)
            : null;
    }

    private static string DescribeContext(nint context)
    {
        if (context == ContextUnaware) return "UNAWARE";
        if (context == ContextSystemAware) return "SYSTEM_AWARE";
        if (context == ContextPerMonitorAware) return "PER_MONITOR_AWARE";
        if (context == ContextPerMonitorAwareV2) return "PER_MONITOR_AWARE_V2";
        if (context == ContextUnawareGdiScaled) return "UNAWARE_GDISCALED";
        return $"未知（0x{context:X}）";
    }

    /// <summary>
    /// 取显示器 DPI。
    /// </summary>
    /// <remarks>
    /// 优先用 <c>GetDpiForMonitor</c>（Shcore），它按显示器句柄查询，对这个用途最直接；
    /// 失败时退到系统 DPI，而不是抛异常——枚举显示器不该因为一个查询失败就整体中断。
    /// </remarks>
    private static uint DpiForMonitor(nint monitor)
    {
        try
        {
            var result = Shcore.GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out _);
            if (result == 0 && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 没有 Shcore 也不要紧，下面还有退路。
        }

        var systemDpi = Dpi32.GetDpiForSystem();
        return systemDpi == 0 ? 96 : systemDpi;
    }

    private static PixelRect ToPixelRect(Rect rect) =>
        new(rect.Left, rect.Top, rect.Width, rect.Height);
}

internal enum MonitorDpiType
{
    /// <summary>实际生效的 DPI。绝大多数情况下要的就是这个。</summary>
    Effective = 0,

    Angular = 1,
    Raw = 2,
}

internal static partial class Shcore
{
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);
}