using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 读取与修改窗口的扩展样式位。
/// </summary>
/// <remarks>
/// 包一层薄封装而不是把原始互操作公开出去（与 <see cref="WindowActivation"/> 同理），
/// 这样产品其余部分不必知道背后是哪个 API，也不必自己拼样式位。
/// </remarks>
public static class WindowStyles
{
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Raise an existing topmost window without changing focus, position, or size.</summary>
    public static bool BringToTopWithoutActivation(nint window)
    {
        if (window == 0) return false;

        try
        {
            return Dpi32.SetWindowPos(
                window,
                HwndTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 给窗口加上 <c>WS_EX_NOACTIVATE</c>：点击它不会把它调到前台。
    /// </summary>
    /// <remarks>
    /// 程序坞的主栏靠它做到「点了不抢焦点」——用户正在别的应用里打字时
    /// 点一下 Dock，焦点必须留在原地。代价是 Dock 自己弹出的东西
    /// （右键菜单、文件夹面板、窗口选择列表）也不会随点击取得焦点，
    /// 需要键盘交互时要显式调 <see cref="WindowActivation.TryBringToForeground"/>。
    ///
    /// 返回值表示样式确实生效，供诊断与测试核对；已经有该样式时
    /// 返回 true 且不再写一遍。
    /// </remarks>
    public static bool MakeNoActivate(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            var style = User32.GetWindowLong(window, User32.GwlExStyle);
            if ((style & User32.WsExNoactivate) != 0)
            {
                return true;
            }

            // SetWindowLong 返回的是改动前的值而不是成败，这里弃元（CA1806）：
            // 写没写进去以下面的样式读回为准，返回值核对不出失败——
            // 失败时它返回 0，而新窗口的旧样式碰巧也可能是 0。
            _ = User32.SetWindowLong(window, User32.GwlExStyle, style | User32.WsExNoactivate);

            return IsNoActivate(window);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把窗口放到桌面之上、所有普通窗口之下（贴着桌面）。
    /// 这样任何窗口都能盖住它，回到桌面时又能看到它。
    /// </summary>
    /// <remarks>
    /// 不能直接把窗口压到 <c>HWND_BOTTOM</c>：点“显示桌面”时 Progman 会被抬到
    /// 普通窗口之上，HWND_BOTTOM 会把 Dock 压到 Progman 下面。窗口虽然仍报告
    /// “可见”，实际却被桌面盖住。这里把窗口插到 Progman 前一个窗口之后，
    /// 也就是紧贴桌面上沿。
    /// </remarks>
    public static bool SendAboveDesktop(nint window)
    {
        var desktop = User32.GetShellWindow();
        if (desktop == 0)
        {
            return false;
        }

        // GW_HWNDPREV = 3：Z 序中排在桌面窗口前面的那个窗口。
        // 把目标窗口插在它后面，就正好落在“桌面之上、普通窗口之下”。
        var aboveDesktop = User32.GetWindow(desktop, 3);
        return Dpi32.SetWindowPos(
            window, aboveDesktop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    /// <summary>按窗口标题查找顶层窗口（用于找到采集载体）。</summary>
    public static nint FindTopLevelWindow(string? title)
    {
        return FindWindowNative(null, title);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowNative(string? className, string? windowName);

    /// <summary>窗口是否已带 <c>WS_EX_NOACTIVATE</c> 样式。</summary>
    public static bool IsNoActivate(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            return (User32.GetWindowLong(window, User32.GwlExStyle) & User32.WsExNoactivate) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
