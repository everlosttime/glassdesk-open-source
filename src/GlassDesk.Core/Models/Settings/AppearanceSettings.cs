namespace GlassDesk.Core.Models.Settings;

/// <summary>
/// 外观设置。同时作用于程序坞、启动台与设置中心——
/// <c>ApplyAppearance</c> 应当从同一份来源刷新所有已打开的窗口。
/// </summary>
public sealed class AppearanceSettings
{
    public const double DefaultGlassTintOpacity = .16;
    public const double DefaultGlassBackgroundClarity = .5;
    public const double DefaultDockGlassTransparency = .9;
    public const double DefaultLauncherGlassTransparency = .2;

    public double DockGlassTransparency { get; set; } = DefaultDockGlassTransparency;
    public double LauncherGlassTransparency { get; set; } = DefaultLauncherGlassTransparency;

    /// <summary>Dock 三个常驻功能图标的明暗，0 为黑色，1 为白色。</summary>
    public double DockBuiltinIconBrightness { get; set; } = 1;

    /// <summary>启动台九宫格采用实心方块；false 恢复原来的空心轮廓。</summary>
    public bool LaunchpadIconFilled { get; set; } = true;

    /// <summary>启动台文字的明暗，0 为黑色，1 为白色。</summary>
    public double LauncherTextBrightness { get; set; } = 1;

    /// <summary>Legacy shared value retained for older settings files.</summary>
    public double GlassBackgroundClarity { get; set; } = DefaultGlassBackgroundClarity;

    /// <summary>Only the glass body veil; never multiplies refraction, lighting or layer alpha.</summary>
    public double GlassTintOpacity { get; set; } = DefaultGlassTintOpacity;

    /// <summary>Shared liquid glass material; legacy selections normalize on load and save.</summary>
    public GlassMaterialMode GlassMaterial { get; set; } = GlassMaterialMode.AndroidLiquidGlass;

    /// <summary>
    /// 固定为深色液态玻璃；旧版主题值在加载和保存时统一为深色。
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    /// <summary>强调色，格式为 <c>#RRGGBB</c>。默认取方案要求的蓝色。</summary>
    public string AccentColor { get; set; } = "#0A84FF";

    /// <summary>
    /// 优先使用 Windows 11 的 DWM 背景材质，而不是自绘的半透明面板。
    /// 在未暴露该 API 的系统版本上忽略；参见 <c>WindowsCapabilities</c>。
    /// </summary>
    public bool UseSystemBackdrop { get; set; } = true;

    /// <summary>减弱或关闭过渡动画。无障碍的基线要求。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// 为觉得半透明难以辨读的用户降低面板不透明度，也覆盖系统层面
    /// 关闭了透明效果的情况。
    /// </summary>
    public bool ReduceTransparency { get; set; }

    /// <summary>程序坞的圆角半径，单位为 DIP。</summary>
    public double DockCornerRadius { get; set; } = 40;

    /// <summary>启动台面板的圆角半径，单位为 DIP。</summary>
    public double LauncherCornerRadius { get; set; } = 80;
}
