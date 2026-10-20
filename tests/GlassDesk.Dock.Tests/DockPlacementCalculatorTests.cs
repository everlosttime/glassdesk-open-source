using GlassDesk.Core.Models.Settings;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 摆放计算的几何断言。屏幕几何用本机实测的混合 DPI 组合做构造值
/// （主屏 2560×1440 @150%、副屏竖屏 1600×2560 @125% 且带负坐标），
/// 不依赖真机枚举，离线可跑。
/// </summary>
/// <remarks>
/// 真机上的「WPF 窗口按计算结果就位」由 <c>--phase4-acceptance</c> 的实机回归覆盖；
/// 这里锁的是纯函数本身的几何性质。
/// </remarks>
public class DockPlacementCalculatorTests
{
    /// <summary>主屏工作区：2560×1440 @150%，任务栏占掉底部 48 物理像素。</summary>
    private static readonly PixelRect PrimaryWorkArea = new(0, 0, 2560, 1392);

    private const uint PrimaryDpi = 144;

    /// <summary>副屏工作区：竖屏 1600×2560 @125%，负 X 坐标（在主屏左侧）。</summary>
    private static readonly PixelRect SecondaryWorkArea = new(-1600, 0, 1600, 2512);

    private const uint SecondaryDpi = 120;

    /// <summary>一个典型 Dock 窗口的物理尺寸（约 900×90 DIP 在 150% 下的样子）。</summary>
    private const int DockWidth = 1350;

    private const int DockHeight = 130;

    [Fact]
    public void 底部居中贴主屏工作区底边且水平居中()
    {
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.Bottom, rect.Bottom);
        Assert.Equal((PrimaryWorkArea.Width - DockWidth) / 2, rect.X - PrimaryWorkArea.X);
    }

    [Fact]
    public void 底部左右贴工作区左右边缘()
    {
        var left = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomLeft, 0, 0, DockWidth, DockHeight, PrimaryDpi);
        Assert.Equal(PrimaryWorkArea.X, left.X);
        Assert.Equal(PrimaryWorkArea.Bottom, left.Bottom);

        var right = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomRight, 0, 0, DockWidth, DockHeight, PrimaryDpi);
        Assert.Equal(PrimaryWorkArea.Right, right.Right);
        Assert.Equal(PrimaryWorkArea.Bottom, right.Bottom);
    }

    [Fact]
    public void 顶部居中贴工作区顶边()
    {
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.TopCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.Y, rect.Y);
        Assert.Equal((PrimaryWorkArea.Width - DockWidth) / 2, rect.X - PrimaryWorkArea.X);
    }

    [Fact]
    public void 四种位置在副屏上都不出屏()
    {
        // 副屏负坐标 + 不同缩放——正负号算错一处的典型翻车现场。
        foreach (var placement in new[] { DockPlacement.BottomCenter, DockPlacement.BottomLeft, DockPlacement.BottomRight, DockPlacement.TopCenter })
        {
            var rect = DockPlacementCalculator.Calculate(
                SecondaryWorkArea, placement, 0, 0, DockWidth, DockHeight, SecondaryDpi);

            Assert.True(rect.X >= SecondaryWorkArea.X, $"{placement}: X {rect.X} < {SecondaryWorkArea.X}");
            Assert.True(rect.Y >= SecondaryWorkArea.Y, $"{placement}: Y {rect.Y} < {SecondaryWorkArea.Y}");
            Assert.True(rect.Right <= SecondaryWorkArea.Right, $"{placement}: 右边越界");
            Assert.True(rect.Bottom <= SecondaryWorkArea.Bottom, $"{placement}: 底边越界");
        }
    }

    [Fact]
    public void 偏移按目标显示器的DPI换算()
    {
        // 100 DIP 在 150% 主屏上是 150 物理像素，在 125% 副屏上是 125——
        // 用系统 DPI（144）去算副屏的偏移正是阶段 2 实测过的错误。
        var baseline = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 0, 0, DockWidth, DockHeight, SecondaryDpi);
        var shifted = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 100, 0, DockWidth, DockHeight, SecondaryDpi);

        Assert.Equal(125, shifted.X - baseline.X);

        var vertical = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomLeft, 0, -40, DockWidth, DockHeight, SecondaryDpi);
        Assert.Equal(baseline.Bottom - 50, vertical.Bottom);
    }

    [Fact]
    public void 偏移把窗口推出工作区时被夹回()
    {
        var pushed = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomLeft, -100_000, 100_000, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(PrimaryWorkArea.X, pushed.X);
        Assert.True(pushed.Bottom <= PrimaryWorkArea.Bottom);
        Assert.True(pushed.Right <= PrimaryWorkArea.Right);
    }

    [Fact]
    public void 窗口比工作区还宽时截到工作区尺寸()
    {
        var huge = DockPlacementCalculator.Calculate(
            SecondaryWorkArea, DockPlacement.BottomCenter, 0, 0, 4_000, 130, SecondaryDpi);

        Assert.Equal(SecondaryWorkArea.Width, huge.Width);
        Assert.Equal(SecondaryWorkArea.X, huge.X);
    }

    [Fact]
    public void 主屏任务栏上方落位_底部位置避开任务栏()
    {
        // 工作区已经把任务栏减掉了（1392 = 1440 - 48），
        // 所以「贴工作区底边」就等于「任务栏上方」。
        var rect = DockPlacementCalculator.Calculate(
            PrimaryWorkArea, DockPlacement.BottomCenter, 0, 0, DockWidth, DockHeight, PrimaryDpi);

        Assert.Equal(1392, rect.Bottom);
        Assert.Equal(1440 - 48, PrimaryWorkArea.Bottom);
    }
}
