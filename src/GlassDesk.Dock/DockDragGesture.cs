namespace GlassDesk.Dock;

/// <summary>一次磁贴拖拽结束时该做什么。</summary>
public enum DragEndAction
{
    /// <summary>落在栏内：排序已在 Drop 里提交，什么都不用做。</summary>
    KeepCommitted = 0,

    /// <summary>落在栏内但没有 Drop（按了 Esc）：丢弃预览，不动布局。</summary>
    CancelOnly = 1,

    /// <summary>落在栏外：丢弃预览，并作为「移出取消固定」（D-03）提交。</summary>
    CancelAndUnpin = 2,
}

/// <summary>
/// 磁贴拖拽结束时的判定。
/// </summary>
/// <remarks>
/// <para>
/// <b>判定必须在拖拽结束时做一次，而不是在 DragLeave 里。</b>
/// 早先的实现在 <c>DragLeave</c> 里达到距离阈值就立刻触发取消固定，宿主随即落盘，
/// 于是有两个真实后果：
/// </para>
/// <list type="number">
/// <item>按 Esc 想取消整次拖拽时，移除已经写下去了，撤不回来；</item>
/// <item>缓慢移出边界时，单次 <c>DragLeave</c> 的位置未必达到阈值，
/// 手势又不会被识别成移出——同一个手势的结果取决于鼠标速度。</item>
/// </list>
/// <para>
/// 现在改为：拖拽期间只更新「此刻是否在栏内」的标签，等手势结束（<c>DoDragDrop</c> 返回）
/// 再用<strong>当刻光标位置</strong>做一次权威判定。另外，
/// <strong>取消优先于移出</strong>：「拖到栏外按 Esc」是取消整次拖拽，
/// 不是「移出取消固定」——只按位置判定会把用户明确取消掉的条目删掉。
/// </para>
/// </remarks>
public static class DockDragGesture
{
    /// <summary>
    /// 按拖拽结束时的三个事实给出动作。
    /// </summary>
    /// <param name="droppedInStrip">栏内收到了 Drop（顺序已在那一刻提交）。</param>
    /// <param name="endedOutside">手势结束时指针在栏外（含超出取消固定阈值）。</param>
    /// <param name="cancelled">
    /// 本次拖拽被<strong>取消</strong>（按了 Esc，或松手前鼠标仍按着）。
    /// 取消优先：即使指针在栏外也只丢弃预览，不移除条目。
    /// </param>
    public static DragEndAction DecideEnd(bool droppedInStrip, bool endedOutside, bool cancelled)
    {
        if (droppedInStrip)
        {
            return DragEndAction.KeepCommitted;
        }

        if (cancelled)
        {
            return DragEndAction.CancelOnly;
        }

        return endedOutside ? DragEndAction.CancelAndUnpin : DragEndAction.CancelOnly;
    }
}

/// <summary>取消一次拖拽的原因。记录下来供诊断与状态文字使用。</summary>
public enum DragCancelReason
{
    /// <summary>不是取消。</summary>
    None = 0,

    /// <summary>按了 Esc。</summary>
    Escape = 1,

    /// <summary>手势结束时鼠标仍按着（Esc 取消的另一种可观测形态）。</summary>
    PrimaryButtonStillDown = 2,
}