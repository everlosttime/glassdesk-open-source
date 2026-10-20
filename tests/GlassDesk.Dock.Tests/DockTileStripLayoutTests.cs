using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 磁贴条的绘制层级与视口约束——对应复核报告的缺陷 1 与缺陷 3。
/// </summary>
/// <remarks>
/// 这两条都要求<strong>读回真实生效的值</strong>：绘制层级读 <c>Panel.ZIndex</c>、
/// 视口读布局后的实际宽度。核对派生量（算出来的顺序数组、算出来的总长）
/// 正是它们上一轮溜过测试的原因。
/// </remarks>
public class DockTileStripLayoutTests
{
    private const double Size = 48;
    private const double Spacing = 8;
    private const double Peak = 1.35;

    private static List<DockTileModel> Models(int count)
        => Enumerable.Range(0, count)
            .Select(index => new DockTileModel { EntryId = $"e{index}", DisplayName = $"条目 {index}" })
            .ToList();

    [Fact]
    public void 缩放最大的磁贴绘制在最上层()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(5));

            var pointer = DockMagnification.SlotCenter(2, Size, Spacing, Peak);
            strip.PaintFrame(pointer);

            var tiles = strip.PaintedTiles;
            var topMost = tiles.OrderByDescending(tile => tile.ZIndex).First();
            var biggest = tiles.OrderByDescending(tile => tile.Scale).First();

            Assert.Equal(biggest.EntryId, topMost.EntryId);
            Assert.True(
                Math.Abs(biggest.Scale - Peak) < 0.0001,
                $"前提不成立：指针下方的磁贴没有取到峰值（{biggest.Scale}）");
        });
    }

    [Fact]
    public void 重叠区域的命中等于绘制层级最上方的那一枚()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(Size, Spacing, Peak, 8, 0);
            strip.SetTiles(Models(3));

            // 指针停在第 1 枚上：它放大后与第 0 枚重叠。
            strip.PaintFrame(DockMagnification.SlotCenter(1, Size, Spacing, Peak));

            var tiles = strip.PaintedTiles;
            var geometry = strip.PaintedGeometry.ToArray();

            // 扫一遍整行：凡是命中结果非 -1 的位置，命中的必须是
            // 「覆盖该点、且绘制层级最高」的那一枚——绘制与命中不得相反。
            var checkedOverlaps = 0;

            for (var probe = 0.0; probe <= strip.RequiredLength; probe += 1)
            {
                var hit = strip.ResolveHit(probe);

                if (hit < 0)
                {
                    continue;
                }

                var covering = Enumerable.Range(0, tiles.Count)
                    .Where(index =>
                    {
                        var (center, scale) = geometry[index];
                        var half = Size * scale / 2;
                        return probe >= center - half && probe < center + half;
                    })
                    .ToList();

                if (covering.Count > 1)
                {
                    checkedOverlaps++;
                }

                // 覆盖者里绘制层级最高的那一枚。
                var topMostCovering = covering.OrderByDescending(index => tiles[index].ZIndex).First();

                Assert.Equal(topMostCovering, hit);
            }

            Assert.True(checkedOverlaps > 0, "用例前提不成立：整行没有出现过重叠区域");
        });
    }

    [Fact]
    public void 滚动之后视口坐标只换算一次偏移()
    {
        StaTest.Run(() =>
        {
            const int Count = 200;

            var strip = new DockTileStrip();

            var host = new System.Windows.Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = strip,
            };

            try
            {
                host.Show();

                strip.SetMetrics(Size, Spacing, Peak, 8, 500);
                strip.SetTiles(Models(Count));
                host.UpdateLayout();

                // 滚动 112 DIP：视口 x=50 对应内容 162（第 2 枚），
                // 而不是 274（第 4 枚，那是把偏移加两遍的结果）。
                strip.ScrollToHorizontalOffset(112);
                host.UpdateLayout();

                Assert.Equal(112, strip.HorizontalOffset, 1);

                var expected = DockMagnification.HitTest(
                    strip.PaintedGeometry.ToArray(), Size, strip.ToContentX(50));

                Assert.Equal(2, expected);                        // 用例前提：内容 162 确实落在第 2 枚上
                Assert.Equal(2, strip.ResolveHitAtViewportX(50));  // 只加一次偏移

                // 把偏移加两遍会命中第 4 枚——明确断言这不是我们拿到的答案。
                var doubled = DockMagnification.HitTest(
                    strip.PaintedGeometry.ToArray(), Size, 50 + (112 * 2));

                Assert.Equal(4, doubled);
                Assert.NotEqual(doubled, strip.ResolveHitAtViewportX(50));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void 封顶宽度真的限制视口而不是只影响滚动标记()
    {
        StaTest.Run(() =>
        {
            const double Cap = 500;
            const int Count = 200;

            var strip = new DockTileStrip();

            var host = new System.Windows.Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = strip,
            };

            try
            {
                host.Show();

                strip.SetMetrics(Size, Spacing, Peak, 8, Cap);
                strip.SetTiles(Models(Count));
                host.UpdateLayout();

                // 期望宽度：磁贴本身需要的总长（远大于 500）。
                var required = strip.RequiredLength;
                Assert.True(required > Cap * 10, $"前提不成立：{Count} 枚磁贴只占 {required:F0} DIP");

                // 真实生效的宽度必须被限制在封顶值附近，而不是随内容无限增长。
                var actual = strip.ActualWidth;
                Assert.True(
                    actual <= Cap + 1,
                    $"封顶没有生效：视口实际宽 {actual:F1} DIP（期望 ≤ {Cap}）");

                Assert.True(strip.CanScroll, "内容超出封顶却没有进入可滚动状态");

                // 滚动条真的能滚：滚到底后末项必须在视口内，并且在内容坐标里点得中。
                strip.ScrollToEnd();
                host.UpdateLayout();

                var offset = strip.HorizontalOffset;
                var viewport = strip.ViewportWidth;

                Assert.True(offset > 0, $"滚动没有生效：偏移仍为 {offset:F1}");

                var lastCenter = DockMagnification.SlotCenter(
                    Count - 1, Size, Spacing, Peak);

                Assert.True(
                    lastCenter >= offset && lastCenter <= offset + viewport + 1,
                    $"滚到底之后末项仍不在视口内：中心 {lastCenter:F1}，视口 [{offset:F1}, {offset + viewport:F1}]");

                // 视口内的像素换算到内容坐标后必须命中末项——滚动之后仍然点得中。
                // 探测点取末项右缘内侧 4 DIP，而不是视口最右边：最右边那几 DIP
                // 落在这条磁贴行末端的放大预留区里，本来就不该命中任何磁贴。
                var lastRight = lastCenter + (Size / 2);
                var probeViewportX = lastRight - 4 - offset;

                Assert.True(
                    probeViewportX >= 0 && probeViewportX <= viewport,
                    $"探测点不在视口内：{probeViewportX:F1} 不在 [0, {viewport:F1}]");

                Assert.Equal(Count - 1, strip.ResolveHit(strip.ToContentX(probeViewportX)));
            }
            finally
            {
                host.Close();
            }
        });
    }
}