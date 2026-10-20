using GlassDesk.Core.Geometry;

namespace GlassDesk.Core.Tests;

public sealed class RoundedRectTests
{
    // 一个 200×100、圆角半径 20 的矩形，够典型也够小，便于推算期望值。
    private const double Width = 200;
    private const double Height = 100;
    private const double Radius = 20;

    [Fact]
    public void Contains_AcceptsTheCentre()
    {
        Assert.True(RoundedRect.Contains(100, 50, Width, Height, Radius));
    }

    [Fact]
    public void Contains_RejectsPointsOutsideTheRectangle()
    {
        Assert.False(RoundedRect.Contains(-1, 50, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(50, -1, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(Width + 1, 50, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(50, Height + 1, Width, Height, Radius));
    }

    [Fact]
    public void Contains_RejectsTheVeryCorner()
    {
        // 这正是实测中被投影像素「填住」、从而误拦鼠标的那个点。
        Assert.False(RoundedRect.Contains(0, 0, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(Width, 0, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(0, Height, Width, Height, Radius));
        Assert.False(RoundedRect.Contains(Width, Height, Width, Height, Radius));
    }

    [Fact]
    public void Contains_AcceptsTheEdgeMidpoints()
    {
        // 四条边的中点落在直线段上，必须算内部——否则面板边缘会点不动。
        Assert.True(RoundedRect.Contains(Width / 2, 0, Width, Height, Radius));
        Assert.True(RoundedRect.Contains(Width / 2, Height, Width, Height, Radius));
        Assert.True(RoundedRect.Contains(0, Height / 2, Width, Height, Radius));
        Assert.True(RoundedRect.Contains(Width, Height / 2, Width, Height, Radius));
    }

    [Fact]
    public void Contains_TreatsTheArcBoundaryAsInside()
    {
        // 圆心在 (20,20)，弧上一点距圆心正好一个半径。
        // 边界算内部：差一像素的边缘不该让点击掉到别的窗口去。
        var onArc = Radius - (Radius * Math.Sqrt(0.5));

        Assert.True(RoundedRect.Contains(onArc, onArc, Width, Height, Radius));
    }

    [Fact]
    public void Contains_IsMonotonicAlongTheCornerDiagonal()
    {
        // 沿对角线从角尖往内走，应当先是外部、越过弧线后变成内部，且只翻转一次。
        var flipped = 0;
        var previous = false;

        for (var step = 0; step <= Radius; step++)
        {
            var current = RoundedRect.Contains(step, step, Width, Height, Radius);
            if (current != previous)
            {
                flipped++;
                previous = current;
            }
        }

        Assert.Equal(1, flipped);
    }

    [Fact]
    public void Contains_WithZeroRadius_BehavesLikeARectangle()
    {
        Assert.True(RoundedRect.Contains(0, 0, Width, Height, 0));
        Assert.True(RoundedRect.Contains(Width, Height, Width, Height, 0));
        Assert.False(RoundedRect.Contains(-0.5, 0, Width, Height, 0));
    }

    [Fact]
    public void Contains_ClampsAnOversizedRadiusToHalfTheShortSide()
    {
        // 半径大于短边一半时，两个圆角会互相穿透，判定也就不再是圆角矩形。
        // 这里按短边一半夹住，同时中心区域必须仍然可点。
        Assert.True(RoundedRect.Contains(Width / 2, Height / 2, Width, Height, 5000));
        Assert.False(RoundedRect.Contains(0, 0, Width, Height, 5000));
    }

    [Fact]
    public void Contains_RejectsEverything_ForADegenerateRectangle()
    {
        Assert.False(RoundedRect.Contains(0, 0, 0, 100, Radius));
        Assert.False(RoundedRect.Contains(0, 0, 100, 0, Radius));
        Assert.False(RoundedRect.Contains(0, 0, 0, 0, Radius));
    }

    [Fact]
    public void Contains_WorksForTheDockAndLaunchpadRadii()
    {
        // 计划书规定的两个半径，各取一组实际尺寸验证四角放行、中心可点。
        // Dock：圆角 22，条高约 90。
        Assert.False(RoundedRect.Contains(0, 0, 400, 90, 22));
        Assert.True(RoundedRect.Contains(200, 45, 400, 90, 22));

        // 启动台：圆角 28，面板约 1127×602 DIP。
        Assert.False(RoundedRect.Contains(0, 0, 1127, 602, 28));
        Assert.True(RoundedRect.Contains(563, 301, 1127, 602, 28));
    }
}