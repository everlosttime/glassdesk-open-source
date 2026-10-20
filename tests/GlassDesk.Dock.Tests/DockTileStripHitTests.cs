using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 磁贴条的绘制帧与命中契约（缺陷复现与回归）。
/// </summary>
public class DockTileStripHitTests
{
    private const double Size = 48;
    private const double Spacing = 8;
    private const double Peak = 1.35;

    private static List<DockTileModel> Models(int count)
        => Enumerable.Range(0, count)
            .Select(index => new DockTileModel { EntryId = $"e{index}", DisplayName = $"条目 {index}" })
            .ToList();

    [Fact]
    public void 点击按最后一次绘制帧判定_不在点击时重算几何()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(3));

            // 绘制一帧：指针停在第 1 枚上（动画已经跑到那里）。
            strip.PaintFrame(DockMagnification.SlotCenter(1, Size, Spacing, Peak));

            // 点击坐标 56 是两枚磁贴的重叠区。
            const double ClickAt = 56;

            // 若在点击时用即时指针重算几何，会得出与画面不同的答案。
            var fresh = DockMagnification.Compute(3, Size, Spacing, Peak, ClickAt);
            var freshHit = DockMagnification.HitTest(fresh, Size, ClickAt);

            var painted = strip.ResolveHit(ClickAt);

            Assert.True(
                freshHit != painted,
                $"用例前提不成立：重算与绘制帧给出了同一个答案（都是 {painted}）");

            var expected = DockMagnification.HitTest(strip.PaintedGeometry.ToArray(), Size, ClickAt);
            Assert.Equal(expected, painted);
        });
    }

    [Fact]
    public void 绘制帧的z序与命中顺序一致()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(5));

            var pointer = DockMagnification.SlotCenter(2, Size, Spacing, Peak) - 6;
            strip.PaintFrame(pointer);

            var geometry = strip.PaintedGeometry.ToArray();
            var order = DockMagnification.TopToBottomOrder(geometry);

            // 命中给出的索引，必须是 z 序里第一个覆盖该点的磁贴。
            foreach (var probe in new[] { geometry[0].Center, geometry[2].Center, pointer })
            {
                var hit = strip.ResolveHit(probe);

                if (hit < 0)
                {
                    continue;
                }

                var firstCoveringInOrder = order.FirstOrDefault(index =>
                {
                    var (center, scale) = geometry[index];
                    var half = Size * scale / 2;
                    return probe >= center - half && probe < center + half;
                }, -1);

                Assert.Equal(firstCoveringInOrder, hit);
            }
        });
    }

    [Fact]
    public void 放大后首枚磁贴在画布内不被裁切()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(3));

            strip.PaintFrame(DockMagnification.SlotCenter(0, Size, Spacing, Peak));

            var geometry = strip.PaintedGeometry.ToArray();
            var first = geometry[0];
            var left = first.Center - (Size * first.Scale / 2);

            Assert.True(left >= -0.001, $"首枚磁贴左边缘 {left:F2} 为负，会被容器裁切");
            Assert.True(
                geometry[2].Center + (Size * geometry[2].Scale / 2) <= strip.RequiredLength + 0.001,
                "末枚磁贴右边缘超出画布总长");
        });
    }
}