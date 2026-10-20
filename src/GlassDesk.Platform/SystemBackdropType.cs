namespace GlassDesk.Platform;

/// <summary>
/// 本产品可以请求的 DWM 背景材质。
/// </summary>
/// <remarks>
/// 取值与 <c>DWM_SYSTEMBACKDROP_TYPE</c> 枚举一致。材质只是一次请求，
/// 不是保证：到底渲染出什么由合成器决定，底下自绘的面板
/// 必须自己单独看也成立。
/// </remarks>
public enum SystemBackdropType
{
    /// <summary>交给系统挑选。在 Windows 11 上等价于 <see cref="MainWindow"/>。</summary>
    Auto = 0,

    /// <summary>不用材质；窗口全部自绘。</summary>
    None = 1,

    /// <summary>Mica——取自壁纸，安静。默认的面板材质。</summary>
    MainWindow = 2,

    /// <summary>Acrylic——模糊窗口背后的内容。用于临时性表面。</summary>
    TransientWindow = 3,

    /// <summary>Mica Alt——对比度更高的变体，适合带选项卡的表面。</summary>
    TabbedWindow = 4,
}

/// <summary>
/// 窗口边框圆角设置，与 <c>DWM_WINDOW_CORNER_PREFERENCE</c> 对应。
/// </summary>
public enum WindowCornerPreference
{
    /// <summary>由 Windows 根据窗口样式决定。</summary>
    Default = 0,

    /// <summary>强制直角。</summary>
    DoNotRound = 1,

    /// <summary>强制使用标准的圆角半径。</summary>
    Round = 2,

    /// <summary>强制使用更小的圆角半径。</summary>
    RoundSmall = 3,
}