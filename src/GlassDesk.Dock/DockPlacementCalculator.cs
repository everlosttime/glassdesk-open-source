using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock;

/// <summary>
/// 「程序坞摆在哪」的唯一计算者。
/// </summary>
/// <remarks>
/// 输入是<strong>物理像素</strong>的显示器工作区与窗口尺寸，输出是物理像素矩形；
/// 偏移量（DIP）在内部按<strong>目标显示器自己的 DPI</strong> 换算，不用系统 DPI——
/// 跨屏换算用错 DPI 正是阶段 2 实测的副屏偏移根源。
///
/// <para>
/// 计算是纯函数：不碰窗口、不碰互操作，同一个输入永远得到同一个矩形。
/// 因此四种位置 × 多屏的正确性可以离线单测；真窗口的实机回归只负责验证
/// 「WPF 窗口确实按算出的矩形就位」这一层。
/// </para>
///
/// <para>
/// 结果保证<strong>完全落在工作区内</strong>（D-15）：屏幕装不下时窗口被夹回
/// 工作区边缘，绝不越界。固定项多到一行放不下的溢出，靠内容滚动解决
/// （批次 3 的滚动容器），不靠把窗口摆出屏。
/// </para>
/// </remarks>
public static class DockPlacementCalculator
{
    /// <summary>
    /// 算出 Dock 窗口的目标矩形（物理像素）。
    /// </summary>
    /// <param name="workArea">目标显示器的工作区（避开任务栏），物理像素。</param>
    /// <param name="placement">停靠位置。</param>
    /// <param name="offsetXDips">水平微调，DIP。正值向右，负值向左。</param>
    /// <param name="offsetYDips">垂直微调，DIP。正值向下，负值向上。</param>
    /// <param name="windowWidthPixels">窗口宽，物理像素。</param>
    /// <param name="windowHeightPixels">窗口高，物理像素。</param>
    /// <param name="dpi">目标显示器的 DPI。偏移量的 DIP→像素换算用它。</param>
    public static PixelRect Calculate(
        PixelRect workArea,
        DockPlacement placement,
        double offsetXDips,
        double offsetYDips,
        int windowWidthPixels,
        int windowHeightPixels,
        uint dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowWidthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHeightPixels);

        var offsetX = ToPixels(offsetXDips, dpi);
        var offsetY = ToPixels(offsetYDips, dpi);

        var (anchorX, anchorY) = placement switch
        {
            // 锚点语义：水平先按位置落位，垂直贴工作区边缘。
            // BottomCenter 水平居中；BottomLeft/Right 贴工作区左右边缘；TopCenter 顶部居中。
            DockPlacement.BottomCenter => (workArea.X + ((workArea.Width - windowWidthPixels) / 2), workArea.Bottom - windowHeightPixels),
            DockPlacement.BottomLeft => (workArea.X, workArea.Bottom - windowHeightPixels),
            DockPlacement.BottomRight => (workArea.Right - windowWidthPixels, workArea.Bottom - windowHeightPixels),
            DockPlacement.TopCenter => (workArea.X + ((workArea.Width - windowWidthPixels) / 2), workArea.Y),
            _ => (workArea.X + ((workArea.Width - windowWidthPixels) / 2), workArea.Bottom - windowHeightPixels),
        };

        var x = anchorX + offsetX;
        var y = anchorY + offsetY;

        return ClampToWorkArea(new PixelRect(x, y, windowWidthPixels, windowHeightPixels), workArea);
    }

    /// <summary>
    /// 把矩形夹回工作区：完全落在工作区内（D-15）。
    /// </summary>
    /// <remarks>
    /// 装不下的极端情形（窗口比工作区还宽/高）以工作区原点为准对齐，
    /// 并截到工作区尺寸——绝不把窗口摆出屏。
    /// </remarks>
    public static PixelRect ClampToWorkArea(PixelRect rect, PixelRect workArea)
    {
        var width = Math.Min(rect.Width, workArea.Width);
        var height = Math.Min(rect.Height, workArea.Height);

        var x = rect.X;
        var y = rect.Y;

        if (x < workArea.X)
        {
            x = workArea.X;
        }
        else if (x + width > workArea.Right)
        {
            x = workArea.Right - width;
        }

        if (y < workArea.Y)
        {
            y = workArea.Y;
        }
        else if (y + height > workArea.Bottom)
        {
            y = workArea.Bottom - height;
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>偏移量的 DIP → 物理像素换算。按目标显示器自己的 DPI。</summary>
    private static int ToPixels(double dips, uint dpi)
        => (int)Math.Round(dips * dpi / 96.0, MidpointRounding.AwayFromZero);
}
