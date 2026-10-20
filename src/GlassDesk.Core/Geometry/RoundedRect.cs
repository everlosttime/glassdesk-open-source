namespace GlassDesk.Core.Geometry;

/// <summary>
/// 圆角矩形的形状判定。
/// </summary>
/// <remarks>
/// 放在 Core 而不是界面层，是因为它是纯数学：既是绘制圆角的依据，
/// 也是命中测试的依据，两处必须用同一份判定。放在这里还能被单元测试直接覆盖，
/// 不必启动界面。
///
/// 之所以需要它：阶段 2 实测发现，投影会在圆角被切掉的那块区域渲染出半透明像素，
/// 于是窗口矩形四角虽然视觉上是空的，仍然会拦截鼠标。判定交给这个函数，
/// 就能在命中测试里把那些点放行。
/// </remarks>
public static class RoundedRect
{
    /// <summary>
    /// 判断点是否落在圆角矩形内部。
    /// </summary>
    /// <param name="x">相对矩形左上角的横坐标。</param>
    /// <param name="y">相对矩形左上角的纵坐标。</param>
    /// <param name="width">矩形宽度。</param>
    /// <param name="height">矩形高度。</param>
    /// <param name="radius">圆角半径。超过短边一半时按短边一半处理。</param>
    /// <remarks>
    /// 边界上的点算内部：差一像素的边缘不该让点击掉到别的窗口去。
    /// </remarks>
    public static bool Contains(double x, double y, double width, double height, double radius)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (x < 0 || y < 0 || x > width || y > height)
        {
            return false;
        }

        // 半径不能超过短边一半，否则两个圆角会互相穿透，判定也就不再是圆角矩形。
        var effective = Math.Min(radius, Math.Min(width, height) / 2);
        if (effective <= 0)
        {
            return true;
        }

        // 落在四个角的方形区域内才需要做圆弧判定，其余位置一定在内部。
        var left = x < effective;
        var right = x > width - effective;
        var top = y < effective;
        var bottom = y > height - effective;

        if ((!left && !right) || (!top && !bottom))
        {
            return true;
        }

        var centerX = left ? effective : width - effective;
        var centerY = top ? effective : height - effective;

        var dx = x - centerX;
        var dy = y - centerY;

        // 加一点点容差，避免浮点误差把弧线上的点判成外部。
        const double Tolerance = 0.5;

        return (dx * dx) + (dy * dy) <= ((effective + Tolerance) * (effective + Tolerance));
    }
}