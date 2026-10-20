namespace GlassDesk.Core.Models.Settings;

/// <summary>程序坞停在哪里、以及它如何呈现。</summary>
public enum DockPlacement
{
    BottomCenter = 0,
    BottomLeft = 1,
    BottomRight = 2,
    TopCenter = 3,
}

public sealed class DockSettings
{
    public const double FixedIconSize = 48;
    public const double FixedIconSpacing = 8;
    public const double FixedMagnificationPeak = 1.15;
    public const double MinimumFrameLengthDip = 180;
    public const double MaximumFrameLengthDip = 10000;
    public const double MinimumSizeScale = 2d / 3;
    public const double MaximumSizeScale = 1.5;

    /// <summary>整体尺寸倍率，同时调整图标、间距与栏厚度。旧配置默认保持原尺寸。</summary>
    public double SizeScale { get; set; } = 1;

    /// <summary>内置入口固定在应用左侧；旧配置默认显示全部入口。</summary>
    public bool ShowLauncherEntry { get; set; } = true;
    public bool ShowThisPcEntry { get; set; } = true;
    public bool ShowDesktopIconsEntry { get; set; } = true;

    /// <summary>
    /// Dock 玻璃外框的横向长度，单位为 DIP。零表示继续按内容自动调整，
    /// 正数表示固定外框长度；图标尺寸与 Dock 厚度不受此值影响。
    /// </summary>
    public double FrameLengthDip { get; set; }

    /// <summary>图标边长，单位为 DIP。</summary>
    public double IconSize { get; set; } = FixedIconSize;

    /// <summary>图标之间的间隙，单位为 DIP。</summary>
    public double IconSpacing { get; set; } = FixedIconSpacing;

    /// <summary>光标处的放大峰值，以 <see cref="IconSize"/> 为基准的倍数。</summary>
    public double MagnificationPeak { get; set; } = 1.35;

    public DockPlacement Placement { get; set; } = DockPlacement.BottomCenter;

    /// <summary>相对停靠锚点的水平微调量，单位为 DIP。</summary>
    public double OffsetX { get; set; }

    /// <summary>相对停靠锚点的垂直微调量，单位为 DIP。</summary>
    public double OffsetY { get; set; }

    /// <summary>平时自动收起；鼠标移到程序坞原位置时唤出，离开后延迟收起。</summary>
    public bool AutoHide { get; set; }

    /// <summary>
    /// 自动隐藏的程序坞收起前的缓冲时间。没有它，指针划过边缘时程序坞
    /// 会来回闪烁；方案要求约 300 ms。
    /// </summary>
    public int AutoHideLeaveDelayMs { get; set; } = 300;

    /// <summary>把指针顶到屏幕边缘即可唤出程序坞。</summary>
    public bool EdgeRevealEnabled { get; set; }

    /// <summary>边缘触发条的厚度，单位为 DIP。</summary>
    public int EdgeHotzoneSize { get; set; } = 14;

    /// <summary>
    /// 当其他窗口盖住程序坞区域时，鼠标移入程序坞原位置属于明确悬停操作，仍会唤出。
    /// 该设置原字段保留兼容既有配置；显式边缘唤出锁仍拥有最高优先级。
    /// </summary>
    public bool BlockRevealWhenCovered { get; set; } = true;

    /// <summary>
    /// 旧版本 Dock 显示器选择器的兼容字段。生产 Dock 始终使用进程启动时的 Primary Monitor，
    /// 不读取此字段进行放置。
    /// </summary>
    public string? TargetDisplayId { get; set; }

    /// <summary>在拥有运行中窗口的图标下方显示圆点。</summary>
    public bool ShowRunningIndicator { get; set; } = true;
}
