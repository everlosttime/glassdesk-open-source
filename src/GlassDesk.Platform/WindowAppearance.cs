using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>调用方希望窗口呈现的样子。</summary>
public sealed class WindowAppearanceRequest
{
    /// <summary>要请求的材质；取 <see cref="SystemBackdropType.None"/> 表示自绘。</summary>
    public SystemBackdropType Backdrop { get; set; } = SystemBackdropType.None;

    public WindowCornerPreference Corners { get; set; } = WindowCornerPreference.Round;

    /// <summary>深色非客户区。</summary>
    public bool DarkMode { get; set; }

    /// <summary>
    /// 当窗口本身就是每像素 32 位时，把边框延伸到客户区
    /// 能让 DWM 把材质合成到我们内容的下方。
    /// </summary>
    public bool ExtendFrameIntoClientArea { get; set; }
}

/// <summary>如实记录系统究竟接受了什么。</summary>
/// <remarks>
/// 材质请求可能被拒绝——版本不对、透明效果被关掉、远程会话，
/// 或是不支持材质的合成模式。第二阶段必须报告每台机器
/// 最终出现哪些降级，所以结果要返回出来，而不是被吞掉。
/// </remarks>
public readonly record struct WindowAppearanceResult(
    bool BackdropApplied,
    bool CornersApplied,
    bool DarkModeApplied)
{
    /// <summary>当请求的材质确实显示在屏幕上时为 true。</summary>
    public bool IsFullyApplied => BackdropApplied && CornersApplied && DarkModeApplied;
}

/// <summary>
/// 通过有文档记载的 DWM 特性，把外观请求应用到窗口句柄上。
/// </summary>
public static class WindowAppearance
{
    /// <summary>
    /// 应用请求，跳过这个 Windows 版本不支持的部分。
    /// 从不抛异常：在旧版本上遇到不支持的属性属于预期结果。
    /// </summary>
    public static WindowAppearanceResult Apply(nint hwnd, WindowAppearanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (hwnd == 0)
        {
            return default;
        }

        var backdropApplied = false;

        if (request.Backdrop != SystemBackdropType.None && WindowsCapabilities.SupportsSystemBackdrop)
        {
            backdropApplied = DwmApi.TrySetAttribute(
                hwnd,
                DwmApi.DwmwaSystemBackdropType,
                (int)request.Backdrop);
        }

        if (backdropApplied && request.ExtendFrameIntoClientArea)
        {
            // 材质是合成到边框里的，所以客户区必须是
            // 边框的一部分，材质才能透出来。
            DwmApi.TryExtendFrameIntoClientArea(hwnd, -1, -1, -1, -1);
        }

        var cornersApplied = false;
        if (WindowsCapabilities.SupportsWindowCornerPreference)
        {
            cornersApplied = DwmApi.TrySetAttribute(
                hwnd,
                DwmApi.DwmwaWindowCornerPreference,
                (int)request.Corners);
        }

        var darkApplied = false;
        if (WindowsCapabilities.SupportsImmersiveDarkMode)
        {
            darkApplied = DwmApi.TrySetAttribute(
                hwnd,
                DwmApi.DwmwaUseImmersiveDarkMode,
                request.DarkMode ? 1 : 0);
        }

        return new WindowAppearanceResult(backdropApplied, cornersApplied, darkApplied);
    }
}