using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 把窗口带到前台。
/// </summary>
/// <remarks>
/// 包一层薄封装而不是把原始互操作公开出去，这样产品其余部分
/// 不必知道背后是哪个 API。主要调用方是托盘菜单：通知区域菜单
/// 在点击其外部时不会收起，除非它的宿主窗口先成为
/// 前台窗口。
///
/// 这次调用可以合理地失败——Windows 会拒绝来自用户未在交互的进程的前台切换，
/// 并返回 false 而不是抛异常。调用方把 false 结果视作外观上的限制，
/// 绝不当作错误。
/// </remarks>
public static class WindowActivation
{
    public static bool TryBringToForeground(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            return User32.SetForegroundWindow(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}