namespace GlassDesk.Dock;

/// <summary>一枚磁贴在 Dock 轴向上的瞬时几何。</summary>
/// <param name="Center">磁贴中心沿 Dock 轴的位置（DIP，内容坐标系）。</param>
/// <param name="Scale">当前缩放。1 = 不放大。</param>
public readonly record struct TileGeometry(double Center, double Scale);

/// <summary>
/// 放大动画的几何计算——绘制与命中共用的唯一来源（D-05）。
/// </summary>
/// <remarks>
/// <para>
/// <b>槽位模型：固定槽位。</b>未放大时每个磁贴的中心位置固定；指针附近的磁贴
/// 原地放大（四向膨胀），把相邻磁贴压在身下。两种候选模型曾在修订计划里对比：
/// 动态排布（相邻磁贴被推开）不重叠、但指针移动时整行都在动，拖拽与点击的
/// 落点随之漂移；固定槽位下未放大槽位是常量，命中区域虽有重叠，按「视觉即所得」
/// 规则归属即可。两条路线的取舍已经确认（见阶段 4 计划修订 3）。
/// </para>
///
/// <para>
/// <b>视觉即所得。</b>命中判定使用与绘制<strong>同一份</strong>几何结果
/// （<see cref="Compute"/> 的输出同时喂绘制与 <see cref="HitTest"/>），
/// 重叠区域归<strong>缩放最大的磁贴</strong>——它绘制在最上层，看见谁点中谁。
/// 动画过程中两者也保持一致：几何是「当前指针位置」的纯函数，
/// 每一帧绘制与命中都用同一帧的计算结果。
/// </para>
///
/// <para>
/// 所有坐标沿 Dock 轴向（横排 Dock 是 X，竖排是 Y），单位 DIP。
/// 与垂直轴无关——垂直方向的放大膨胀由容器预留的厚度承担
/// （见 <see cref="RequiredThickness"/>）。
/// </para>
/// </remarks>
public static class DockMagnification
{
    /// <summary>
    /// 计算每个磁贴的瞬时几何。返回的中心已含两端预留（见 <see cref="EndPadding"/>），
    /// 因此可直接当作画布坐标用于摆放与命中。
    /// </summary>
    /// <param name="count">磁贴数量。</param>
    /// <param name="iconSize">未放大图标边长，DIP。</param>
    /// <param name="spacing">图标间距，DIP。</param>
    /// <param name="peak">放大峰值倍数（D-17）。1 表示不放大。</param>
    /// <param name="pointerPosition">指针沿 Dock 轴的位置（画布坐标，DIP）；指针不在栏上时传 null。</param>
    public static TileGeometry[] Compute(
        int count,
        double iconSize,
        double spacing,
        double peak,
        double? pointerPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var geometry = new TileGeometry[count];
        ComputeInto(geometry, iconSize, spacing, peak, pointerPosition);
        return geometry;
    }

    internal static void ComputeInto(Span<TileGeometry> geometry, double iconSize, double spacing,
        double peak, double? pointerPosition)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);

        for (var index = 0; index < geometry.Length; index++)
        {
            var center = SlotCenter(index, iconSize, spacing, peak);
            var scale = 1.0;

            if (pointerPosition is { } pointer)
            {
                var distance = Math.Abs(pointer - center);
                scale = 1 + (peak - 1) * Falloff(distance, iconSize, spacing);
            }

            geometry[index] = new TileGeometry(center, scale);
        }

    }

    internal static int CompareDrawOrder(TileGeometry[] geometry, int left, int right)
    {
        var scaleOrder = geometry[right].Scale.CompareTo(geometry[left].Scale);
        return scaleOrder != 0 ? scaleOrder : left.CompareTo(right);
    }

    /// <summary>
    /// 绘制与命中<strong>共用</strong>的 z 序：缩放大的在上，同缩放时按索引。
    /// </summary>
    /// <remarks>
    /// 绘制侧拿它设 <c>Panel.ZIndex</c>，命中侧按同一顺序找第一个接住指针的磁贴。
    /// 两边共用这个函数，而不是各自写一遍排序——「看见谁就点中谁」才是结构上成立的，
    /// 而不是靠两处约定一致。
    /// </remarks>
    public static int[] TopToBottomOrder(TileGeometry[] geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var order = Enumerable.Range(0, geometry.Length).ToArray();
        Array.Sort(order, Comparer<int>.Create((left, right) => CompareDrawOrder(geometry, left, right)));
        return order;
    }

    /// <summary>
    /// 命中测试：指针落在哪枚磁贴上（视觉即所得）。
    /// </summary>
    /// <remarks>
    /// 按 <see cref="TopToBottomOrder"/> 的顺序找第一个包含指针的磁贴。
    /// <paramref name="geometry"/> 必须是<strong>最后一次实际绘制</strong>的那一份
    /// （<c>DockTileStrip</c> 缓存绘制帧正是为此）——拿即时指针位置重算一份新的，
    /// 会在动画中途与屏幕上的画面分叉，出现「看见 A 点中 B」。
    /// 没有任何磁贴接住指针（落在间隙上）返回 -1。
    /// </remarks>
    public static int HitTest(TileGeometry[] geometry, double iconSize, double pointerPosition)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        foreach (var index in TopToBottomOrder(geometry))
        {
            var (center, scale) = geometry[index];
            var half = iconSize * scale / 2;

            if (pointerPosition >= center - half && pointerPosition < center + half)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// 沿 Dock 轴两端的预留量（DIP）：峰值放大时首尾磁贴各向外膨胀这么多。
    /// </summary>
    /// <remarks>
    /// 必须真的把磁贴<strong>向内</strong>让出这份余量，而不只是把总长度算大一点。
    /// 只加总长时首枚磁贴的左边缘仍在负坐标上（默认峰值下实测 −8.4 DIP），
    /// 而容器是裁切的，放大后就被切掉一条边。
    /// </remarks>
    public static double EndPadding(double iconSize, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);

        return (peak - 1) * iconSize / 2;
    }

    /// <summary>
    /// 一行磁贴需要的总长度（沿 Dock 轴，DIP）。
    /// </summary>
    /// <remarks>
    /// 等于「未放大总长 + 两端预留」，与 <see cref="Compute"/> 的坐标完全对齐：
    /// 首枚磁贴放大后的左边缘恰好在 0，末枚的右边缘恰好等于这个长度。
    /// </remarks>
    public static double RequiredLength(int count, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count == 0)
        {
            return 0;
        }

        return count * iconSize + (count - 1) * spacing + (2 * EndPadding(iconSize, peak));
    }

    /// <summary>
    /// 垂直于 Dock 轴需要的总厚度（DIP）：未放大厚度 + 放大膨胀。
    /// </summary>
    /// <remarks>
    /// 容器按这个厚度预留空间（修订 3：封顶时预留放大空间），
    /// 保证峰值放大时图标不被窗口裁切。
    /// </remarks>
    public static double RequiredThickness(double iconSize, double peak, double verticalPadding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iconSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(peak, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(verticalPadding, 0);

        return iconSize * peak + verticalPadding * 2;
    }

    /// <summary>
    /// 第 <paramref name="index"/> 枚磁贴的静止中心（<strong>画布坐标</strong>，DIP）。
    /// </summary>
    /// <remarks>
    /// 含两端预留（<see cref="EndPadding"/>）：先把整行向内让 `端部预留`，
    /// 首尾磁贴峰值放大后才不会越出画布。绘制与命中都以这个坐标为准，
    /// 因此调用方不需要（也不应该）自己再加偏移。
    /// </remarks>
    public static double SlotCenter(int index, double iconSize, double spacing, double peak)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return EndPadding(iconSize, peak) + (index * (iconSize + spacing)) + (iconSize / 2);
    }

    /// <summary>
    /// 缩放衰减曲线：距离 0 处取 1（全额放大），一个节距外平滑衰减到 0。
    /// </summary>
    /// <remarks>
    /// 用余弦钟形而不是线性三角：衰减平滑，动画不会在相邻磁贴之间出现折角。
    /// 影响半径定为一个节距——目标移动到槽位中心时，紧邻磁贴回到原尺寸，避免遮挡。
    /// </remarks>
    private static double Falloff(double distance, double iconSize, double spacing)
    {
        var pitch = iconSize + spacing;
        var radius = pitch;

        if (distance >= radius)
        {
            return 0;
        }

        var t = distance / radius;
        return 0.5 * (1 + Math.Cos(t * Math.PI));
    }
}
