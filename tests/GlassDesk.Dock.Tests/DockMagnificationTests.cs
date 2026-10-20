using GlassDock = GlassDesk.Dock.DockMagnification;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 放大几何的断言：固定槽位、峰值生效、命中与绘制同源（视觉即所得）。
/// 用默认参数 48 DIP / 8 DIP / 1.35×（D-21）做基线。
/// </summary>
public class DockMagnificationTests
{
    private const double Size = 48;
    private const double Spacing = 8;
    private const double Peak = 1.35;

    [Fact]
    public void 未放大时槽位中心等距排列()
    {
        var geometry = GlassDock.Compute(4, Size, Spacing, Peak, pointerPosition: null);

        Assert.Equal(4, geometry.Length);
        Assert.All(geometry, tile => Assert.Equal(1.0, tile.Scale));

        // 中心含两端预留 8.4（峰值 1.35、48 DIP 图标），节距 56。
        var padding = GlassDock.EndPadding(Size, Peak);
        Assert.Equal(padding + 24, geometry[0].Center);
        Assert.Equal(padding + 24 + Size + Spacing, geometry[1].Center);
        Assert.Equal(padding + 24 + (2 * (Size + Spacing)), geometry[2].Center);
    }

    [Fact]
    public void 峰值放大时首尾磁贴都不越出画布()
    {
        // 缺陷复现：只把总长度算大、磁贴不向内让位时，首枚磁贴的左边缘
        // 仍是负坐标（默认峰值下 −8.4 DIP），而容器裁切，放大后就被切掉一条边。
        var count = 4;

        foreach (var pointer in new double?[] { null, 0, GlassDock.SlotCenter(0, Size, Spacing, Peak), 5_000 })
        {
            var geometry = GlassDock.Compute(count, Size, Spacing, Peak, pointer);
            var length = GlassDock.RequiredLength(count, Size, Spacing, Peak);

            for (var index = 0; index < count; index++)
            {
                var (center, scale) = geometry[index];
                var half = Size * scale / 2;

                Assert.True(
                    center - half >= -0.001,
                    $"指针 {pointer}：第 {index} 枚左边缘 {center - half:F2} 为负，会被裁切");

                Assert.True(
                    center + half <= length + 0.001,
                    $"指针 {pointer}：第 {index} 枚右边缘 {center + half:F2} 超出总长 {length:F2}");
            }
        }
    }

    [Fact]
    public void 绘制顺序与命中顺序是同一份()
    {
        // 重叠归属「视觉即所得」要求绘制 z 序与命中判定顺序一致；
        // 两者共用 TopToBottomOrder，这条用例锁住的是「共用」本身。
        var pointer = GlassDock.SlotCenter(2, Size, Spacing, Peak) - 6;
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);

        var order = GlassDock.TopToBottomOrder(geometry);

        // 顺序 = 缩放降序。
        for (var index = 1; index < order.Length; index++)
        {
            Assert.True(
                geometry[order[index - 1]].Scale >= geometry[order[index]].Scale,
                "z 序不是按缩放降序");
        }

        // 命中取的就是这个顺序里的第一个命中者：构造一个被两枚磁贴同时覆盖的点，
        // 断言命中结果等于「该顺序中第一个覆盖它的索引」。
        var probe = geometry[2].Center;
        var expected = order.First(index =>
        {
            var (center, scale) = geometry[index];
            var half = Size * scale / 2;
            return probe >= center - half && probe < center + half;
        });

        Assert.Equal(expected, GlassDock.HitTest(geometry, Size, probe));
    }

    [Fact]
    public void 指针正下方的磁贴取峰值_D17()
    {
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointerPosition: GlassDock.SlotCenter(2, Size, Spacing, Peak));

        Assert.Equal(Peak, geometry[2].Scale, 9);

        // 当前有限范围曲线在一个槽位节距外归1，外围可相等。
        Assert.Equal(1, geometry[0].Scale);
        Assert.True(geometry[0].Scale <= geometry[1].Scale);
        Assert.True(geometry[1].Scale < geometry[2].Scale);
        Assert.True(geometry[3].Scale < geometry[2].Scale);
        Assert.True(geometry[4].Scale <= geometry[3].Scale);
        var midpoint = (GlassDock.SlotCenter(1, Size, Spacing, Peak) + GlassDock.SlotCenter(2, Size, Spacing, Peak)) / 2;
        var between = GlassDock.Compute(5, Size, Spacing, Peak, midpoint);
        Assert.Equal(between[1].Scale, between[2].Scale, 9);
        Assert.InRange(between[1].Scale, 1.01, Peak - .01);
        Assert.Equal(1, between[0].Scale);
    }

    [Fact]
    public void 峰值可调_改为2时中心磁贴翻倍()
    {
        var geometry = GlassDock.Compute(5, Size, Spacing, 2.0, pointerPosition: GlassDock.SlotCenter(1, Size, Spacing, 2.0));

        Assert.Equal(2.0, geometry[1].Scale, 9);
    }

    [Fact]
    public void 命中落在指针正下方的磁贴上()
    {
        var pointer = GlassDock.SlotCenter(3, Size, Spacing, Peak);
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);

        Assert.Equal(3, GlassDock.HitTest(geometry, Size, pointer));
    }

    [Fact]
    public void 重叠区域归缩放最大的磁贴_视觉即所得()
    {
        // 指针位于第 1 枚与第 2 枚槽位之间偏第 2 枚：
        // 第 2 枚离指针近、放得更大，膨胀后盖过第 1 枚的右缘。
        var pointer = GlassDock.SlotCenter(2, Size, Spacing, Peak) - 6;
        var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);

        // 断言此刻确实重叠（第 2 枚的左缘越过了第 1 枚的右缘），
        // 否则这条用例什么都没锁住。
        var overlapStart = geometry[2].Center - Size * geometry[2].Scale / 2;
        var previousEnd = geometry[1].Center + Size * geometry[1].Scale / 2;
        Assert.True(overlapStart < previousEnd, "用例前提不成立：两枚磁贴没有重叠");

        Assert.Equal(2, GlassDock.HitTest(geometry, Size, pointer));
    }

    [Fact]
    public void 命中与绘制用同一帧几何_动画中途也不偏移()
    {
        // 模拟动画中途：指针在两个槽位之间连续取 21 个位置。
        // 每一帧先算几何再命中——这正是运行时每帧做的事；
        // 若命中结果跳出「指针两侧最近的两枚磁贴」就说明几何与命中脱节。
        for (var step = 0; step <= 20; step++)
        {
            var pointer = GlassDock.SlotCenter(2, Size, Spacing, Peak)
                + (GlassDock.SlotCenter(3, Size, Spacing, Peak) - GlassDock.SlotCenter(2, Size, Spacing, Peak)) * step / 20.0;

            var geometry = GlassDock.Compute(5, Size, Spacing, Peak, pointer);
            var hit = GlassDock.HitTest(geometry, Size, pointer);

            Assert.True(hit is 2 or 3, $"指针 {pointer:F1} 命中了第 {hit} 枚（期望 2 或 3）");
        }
    }

    [Fact]
    public void 命中落在磁贴间隙时返回无()
    {
        // 指针拉到最右端之外：没有任何磁贴接住。
        var geometry = GlassDock.Compute(3, Size, Spacing, Peak, pointerPosition: null);
        Assert.Equal(-1, GlassDock.HitTest(geometry, Size, 1000));
    }

    [Fact]
    public void 总长度与首尾放大后的边缘严丝合缝()
    {
        // 未放大总长：3 枚 = 3*48 + 2*8 = 160。
        Assert.Equal(160, GlassDock.RequiredLength(3, Size, Spacing, 1.0));

        // 峰值 1.35 时两端各留 (1.35-1)*48/2 = 8.4：总长 = 160 + 16.8。
        Assert.Equal(160 + 2 * 8.4, GlassDock.RequiredLength(3, Size, Spacing, Peak), 9);

        // 关键性质：某端磁贴放大时，它的外缘恰好落在画布边界上——
// 总长不是「大致够用」，而是恰好等于该端放大后的边界。
        // （两端不会同时放大：指针只可能在一处。）
        var length = GlassDock.RequiredLength(3, Size, Spacing, Peak);

        var atFirst = GlassDock.Compute(3, Size, Spacing, Peak, GlassDock.SlotCenter(0, Size, Spacing, Peak));
        Assert.Equal(0, atFirst[0].Center - (Size * atFirst[0].Scale / 2), 6);

        var atLast = GlassDock.Compute(3, Size, Spacing, Peak, GlassDock.SlotCenter(2, Size, Spacing, Peak));
        Assert.Equal(length, atLast[2].Center + (Size * atLast[2].Scale / 2), 6);
    }

    [Fact]
    public void 厚度预留放大空间_不被窗口裁切()
    {
        // 48 DIP 图标在 1.35 峰值下膨胀到 64.8；上下各留 6 DIP 内边距时
        // 容器厚度必须 >= 64.8 + 12。
        var thickness = GlassDock.RequiredThickness(Size, Peak, 6);

        Assert.True(thickness >= Size * Peak, "容器厚度没有覆盖峰值放大尺寸——图标会被裁切");
        Assert.Equal(Size * Peak + 12, thickness, 9);
    }
}
