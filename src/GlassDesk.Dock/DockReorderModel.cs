namespace GlassDesk.Dock;

/// <summary>排序提交的结果。</summary>
public enum ReorderOutcome
{
    /// <summary>从未开始过拖拽，无需提交。</summary>
    Idle = 0,

    /// <summary>顺序有变化，已提交保存。</summary>
    Committed = 1,

    /// <summary>顺序没有变化（放回原位），无需保存。</summary>
    Unchanged = 2,

    /// <summary>被取消（Esc 或拖出栏外），顺序原样保留。</summary>
    Cancelled = 3,

    /// <summary>提交失败：顺序已恢复原样，由调用方提示用户。</summary>
    SaveFailed = 4,
}

/// <summary>
/// Dock 磁贴拖拽排序的状态机——「预览只改内存，松手才落盘」的唯一裁决者（R-08）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独成一个类。</b>R-08 的验收是「拖拽期间不得写盘；操作完成后
/// 统一保存一次」。把「预览 → 提交/取消」做成显式状态机，保存次数就成了
/// 可断言的量：预览任意多次、取消任意多次，落盘只可能发生在
/// <see cref="Commit"/> 里的一次。界面层（<see cref="DockTileStrip"/>）只负责
/// 把拖拽手势翻译成这里的三个动作。
/// </para>
///
/// <para>
/// <b>提交失败的行为。</b>保存抛出异常时<strong>恢复原始顺序</strong>并把失败
/// 交给调用方提示——不能出现「界面上的顺序与磁盘上不一致」的静默状态。
/// </para>
/// </remarks>
public sealed class DockReorderModel
{
    private readonly List<string> _originalOrder = [];
    private readonly List<string> _previewOrder = [];
    private readonly Action<IReadOnlyList<string>> _save;

    private bool _active;

    /// <summary>排序进行中为 true（拖拽未松手且未取消）。</summary>
    public bool IsActive => _active;

    /// <summary>当前预览顺序（拖拽进行中）或原始顺序（未拖拽时）。</summary>
    public IReadOnlyList<string> Order => _active ? _previewOrder : _originalOrder;

    /// <param name="save">
    /// 落盘委托：提交时以最终顺序调用一次。实现应当只写一次文件
    /// （<c>LayoutStore</c>：改 <c>PinnedItemIds</c> 后 <c>SaveDock()</c>）。
    /// </param>
    public DockReorderModel(Action<IReadOnlyList<string>> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        _save = save;
    }

    /// <summary>开始一次拖拽。记住当前顺序作为回滚基准。</summary>
    public void Begin(IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(order);

        _originalOrder.Clear();
        _originalOrder.AddRange(order);
        _previewOrder.Clear();
        _previewOrder.AddRange(order);
        _active = true;
    }

    /// <summary>把被拖的条目预览移到目标索引。只改内存；索引越界按边界处理。</summary>
    public void PreviewMove(string itemId, int targetIndex)
    {
        if (!_active)
        {
            return;
        }

        var current = _previewOrder.IndexOf(itemId);

        if (current < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _previewOrder.Count - 1);

        if (targetIndex == current)
        {
            return;
        }

        _previewOrder.RemoveAt(current);
        _previewOrder.Insert(targetIndex, itemId);
    }

    /// <summary>提交：顺序有变化时保存一次。失败时恢复原始顺序并报告。</summary>
    public ReorderOutcome Commit()
    {
        if (!_active)
        {
            return ReorderOutcome.Idle;
        }

        _active = false;

        if (SameOrder(_previewOrder, _originalOrder))
        {
            return ReorderOutcome.Unchanged;
        }

        var finalOrder = _previewOrder.ToArray();

        try
        {
            _save(finalOrder);
            return ReorderOutcome.Committed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 保存失败：内存预览作废，回到原始顺序。调用方负责把失败说出口。
            _previewOrder.Clear();
            _previewOrder.AddRange(_originalOrder);
            _ = ex;
            return ReorderOutcome.SaveFailed;
        }
    }

    /// <summary>取消（Esc、拖出栏外、失焦）：顺序原样保留，绝不落盘。</summary>
    public ReorderOutcome Cancel()
    {
        if (!_active)
        {
            return ReorderOutcome.Idle;
        }

        _active = false;
        _previewOrder.Clear();
        _previewOrder.AddRange(_originalOrder);
        return ReorderOutcome.Cancelled;
    }

    private static bool SameOrder(List<string> left, List<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
