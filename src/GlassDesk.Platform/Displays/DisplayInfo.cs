namespace GlassDesk.Platform.Displays;

/// <summary>以物理像素表示的矩形。</summary>
/// <remarks>
/// 单独定义而不是复用 WPF 的 <c>Rect</c>：这个类型表达的是**物理像素**，而 WPF 的
/// <c>Rect</c> 表达 DIP。两者混用正是副屏定位错位的根源，用不同类型把它们分开，
/// 编译器就能帮忙拦住一部分错误。
/// </remarks>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    public override string ToString() => $"({X},{Y} {Width}×{Height})";
}

/// <summary>
/// 一台显示器在**物理像素**坐标空间中的位置、工作区与 DPI。
/// </summary>
public sealed record DisplayInfo(
    string DeviceName,
    bool IsPrimary,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi)
{
    /// <summary>缩放倍数，例如 150% 缩放对应 1.5。</summary>
    public double ScaleFactor => Dpi / 96.0;

    /// <summary>把物理像素换算成 DIP。</summary>
    public double ToDips(int physicalPixels) => physicalPixels * 96.0 / Dpi;

    /// <summary>把 DIP 换算成物理像素。</summary>
    public int ToPixels(double dips) => (int)Math.Round(dips * Dpi / 96.0);

    public override string ToString() =>
        $"{DeviceName}{(IsPrimary ? "（主屏）" : string.Empty)} 边界={Bounds} 工作区={WorkArea} DPI={Dpi}（{ScaleFactor:P0}）";
}

/// <summary>进程的 DPI 感知等级。</summary>
public enum DpiAwarenessLevel
{
    Unknown = -1,
    Unaware = 0,
    SystemAware = 1,
    PerMonitorAware = 2,
}

/// <summary>探测结果，包含粗细两级信息。</summary>
/// <remarks>
/// <see cref="DpiAwarenessLevel"/> 无法区分 PerMonitor **V1 与 V2**——两者都报告「每屏感知」。
/// 而 V1 不会在窗口跨屏时自动重算缩放，所以必须额外对比上下文句柄才能确认是 V2。
/// </remarks>
public readonly record struct DpiAwarenessInfo(
    DpiAwarenessLevel Level,
    bool IsPerMonitorV2,
    string RawContext,
    uint SystemDpi)
{
    public string Describe() => Level switch
    {
        DpiAwarenessLevel.PerMonitorAware when IsPerMonitorV2 => "每屏感知 V2（PerMonitorV2）",
        DpiAwarenessLevel.PerMonitorAware => "每屏感知 V1（PerMonitor）",
        DpiAwarenessLevel.SystemAware => "系统感知（System Aware）——跨屏不会重新缩放",
        DpiAwarenessLevel.Unaware => "DPI 不感知——坐标会被系统拉伸",
        _ => "未知",
    };
}