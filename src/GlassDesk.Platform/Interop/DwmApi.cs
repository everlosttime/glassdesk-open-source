using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// 用于构建玻璃外观的窗口级 DWM 特性。
/// </summary>
/// <remarks>
/// 它们只在 <see cref="WindowsCapabilities"/> 列出的版本上有意义；
/// 在更旧的版本上这些调用会返回失败的 HRESULT，调用方应把它当作
/// 「退回自绘面板」而不是错误。
/// </remarks>
internal static partial class DwmApi
{
    /// <summary>Mica / Acrylic 等背景材质。Windows 11 22621 及以上。</summary>
    internal const int DwmwaSystemBackdropType = 38;

    /// <summary>窗口边框圆角。Windows 11 22000 及以上。</summary>
    internal const int DwmwaWindowCornerPreference = 33;

    /// <summary>在浅色与深色之间切换非客户区。</summary>
    internal const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>边框颜色。</summary>
    internal const int DwmwaBorderColor = 34;

    /// <summary>
    /// 窗口的**可见**边框，物理像素。
    /// </summary>
    /// <remarks>
    /// 这是全屏检测的关键一环，不能省。Windows 10 起，最大化窗口的
    /// <c>GetWindowRect</c> 会把被推到屏幕外的不可见调整边框算进去（每边约 8 像素），
    /// 于是一个普通的最大化窗口在数值上「覆盖了整块显示器」，
    /// 只看 <c>GetWindowRect</c> 就会把它误判成全屏。
    /// 这个特性返回的是画面上真正看到的那一圈边框（最大化时正好是工作区），
    /// 用它判定才能把「最大化」与「全屏」分开。
    /// </remarks>
    internal const int DwmwaExtendedFrameBounds = 9;

    /// <summary>窗口是否已被 DWM 隐藏（UWP 挂起、虚拟桌面切走等）。</summary>
    internal const int DwmwaCloaked = 14;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowIntAttribute(nint hwnd, int attribute, out int value, int size);

    /// <summary>
    /// 取窗口的可见边框（物理像素）。失败时返回 false，调用方需自行退回 <c>GetWindowRect</c>。
    /// </summary>
    /// <remarks>
    /// 组合缩放下这个值与 <c>GetWindowRect</c> 可能相差几个像素，属正常；
    /// 真正需要区分的是最大化窗口那几十像素的差别。
    /// </remarks>
    internal static bool TryGetExtendedFrameBounds(nint hwnd, out Rect bounds)
    {
        bounds = default;

        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<Rect>()) == 0
                && bounds.Width > 0
                && bounds.Height > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>窗口是否被 DWM 隐藏。查询失败时返回 false（当作正常窗口处理）。</summary>
    internal static bool IsCloaked(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return DwmGetWindowIntAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 应用一项窗口特性，并报告系统是否接受。
    /// </summary>
    /// <remarks>
    /// 返回 false 而不是抛异常。机器不认识某个特性
    /// 属于正常且可预期的情况，必须安静降级——而不是当作异常。
    /// </remarks>
    internal static bool TrySetAttribute(nint hwnd, int attribute, int value)
    {
        if (hwnd == 0)
        {
            return false;
        }

        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryExtendFrameIntoClientArea(nint hwnd, int left, int top, int right, int bottom)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var margins = new Margins
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
        };

        try
        {
            return DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Margins
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}