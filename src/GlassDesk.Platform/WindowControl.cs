using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 对单个窗口做温和的动作控制：显示、隐藏、最小化、还原与请求关闭。
/// </summary>
/// <remarks>
/// 包一层而不是让上层直接调 <c>User32</c>（与 <see cref="WindowActivation"/> 同理）。
/// 全部动作都走窗口自己的消息与 <c>ShowWindow</c>，不结束任何进程——
/// 「显示桌面」与探针收尾这类批量操作靠它保持温和：关不掉的窗口
/// 会如实返回，由调用方决定下一步，绝不强杀。
///
/// <para>
/// 这些调用可以合理地失败（窗口已消失、被拒绝），返回 <c>false</c> 而不抛异常。
/// </para>
/// </remarks>
public static class WindowControl
{
    /// <summary>窗口句柄仍然有效时为 true。</summary>
    public static bool IsAlive(nint window)
    {
        try
        {
            return window != 0 && User32.IsWindow(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>窗口当前是否处于最小化。</summary>
    public static bool IsMinimized(nint window)
    {
        try
        {
            return window != 0 && User32.IsIconic(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>当前前台窗口；没有前台窗口时为 0。</summary>
    public static nint GetForegroundWindow()
    {
        try
        {
            return User32.GetForegroundWindow();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 经 <c>WM_SYSCOMMAND/SC_MINIMIZE</c> 请求最小化——与 <see cref="Minimize"/>
    /// 是两条等价路径，「显示桌面」探针实测两者对靶子窗口同样有效。
    /// </summary>
    public static bool MinimizeViaSysCommand(nint window)
    {
        const uint WmSysCommand = 0x0112;
        const nint ScMinimize = 0xF020;

        try
        {
            return window != 0 && User32.PostMessage(window, WmSysCommand, (nuint)ScMinimize, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }


    /// <summary>读窗口标题；读不到时为空串。</summary>
    public static string GetTitle(nint window)
    {
        try
        {
            return window == 0 ? string.Empty : User32.ReadWindowText(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>投递一条 <c>WM_CLOSE</c>，走窗口自己的关闭流程。</summary>
    public static bool Close(nint window)
    {
        try
        {
            return window != 0 && User32.PostMessage(window, User32.WmClose, 0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>最小化窗口。</summary>
    public static bool Minimize(nint window) => ShowWith(window, User32.SwMinimize);

    /// <summary>还原窗口。</summary>
    public static bool Restore(nint window) => ShowWith(window, User32.SwRestore);

    /// <summary>显示窗口（不激活）。</summary>
    public static bool Show(nint window) => ShowWith(window, User32.SwShow);

    /// <summary>隐藏窗口。隐藏与最小化不同：窗口从屏幕和任务栏一起消失。</summary>
    public static bool Hide(nint window) => ShowWith(window, User32.SwHide);

    private static bool ShowWith(nint window, int command)
    {
        try
        {
            return window != 0 && User32.ShowWindow(window, command);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
