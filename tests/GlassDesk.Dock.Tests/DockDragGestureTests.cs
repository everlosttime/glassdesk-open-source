using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 拖拽结束判定与固定合并的断言——对应复核报告的两条缺陷。
/// </summary>
public class DockDragGestureTests
{
    [Fact]
    public void 按Esc结束时不动布局_更不触发移出()
    {
        // Esc 时指针还在栏内、没有 Drop。早先的实现已在 DragLeave
        // 里把「移出」写下去了，Esc 撤不回来。
        Assert.Equal(
            DragEndAction.CancelOnly,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: false, cancelled: true));
    }

    [Fact]
    public void 拖到栏外按Esc仍然只取消_不移除条目()
    {
        // 复核报告的缺陷：判定只区分「栏内收到 Drop」和「指针在栏外」，
        // 于是「拖到栏外按 Esc」被当成移出取消固定——用户明确取消掉的条目被删了。
        // 取消优先于移出。
        Assert.Equal(
            DragEndAction.CancelOnly,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: true, cancelled: true));
    }

    [Fact]
    public void 落在栏外且正常松手时才算移出取消固定()
    {
        Assert.Equal(
            DragEndAction.CancelAndUnpin,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: true, cancelled: false));
    }

    [Fact]
    public void 栏内落下时排序已提交_不再额外动作()
    {
        Assert.Equal(
            DragEndAction.KeepCommitted,
            DockDragGesture.DecideEnd(droppedInStrip: true, endedOutside: false, cancelled: false));
    }

    [Fact]
    public void 栏内落下但指针停在边界外时_以栏内落下为准()
    {
        // 边界抖动：Drop 已经在栏内发生，就不能再当成移出。
        Assert.Equal(
            DragEndAction.KeepCommitted,
            DockDragGesture.DecideEnd(droppedInStrip: true, endedOutside: true, cancelled: false));
    }

    [Fact]
    public void 栏内落下且Esc被记为取消时_已提交的排序保持不动()
    {
        // Drop 已经发生 = 用户明确松手放下了；此时即使有取消信号也不再回退。
        Assert.Equal(
            DragEndAction.KeepCommitted,
            DockDragGesture.DecideEnd(droppedInStrip: true, endedOutside: true, cancelled: true));
    }

    [Fact]
    public void 栏内松手结束但鼠标仍按着_按取消处理而不误判()
    {
        // 取消信号来自「鼠标仍按着」这一路兜底时，也不得触发移除。
        Assert.Equal(
            DragEndAction.CancelOnly,
            DockDragGesture.DecideEnd(droppedInStrip: false, endedOutside: true, cancelled: true));
    }
}

/// <summary>拖入导入后固定列表的合并规则。</summary>
public class DockPinMergeTests
{
    [Fact]
    public void 已在库中的条目拖回来会重新固定()
    {
        // 缺陷复现：只看「新增」时，把移出 Dock 的应用再拖回来，导入报「重复 1」，
        // 手势没有任何反应。
        var merged = DockPinMerge.Merge(["a", "b"], added: [], duplicates: ["c"]);

        Assert.Equal(["a", "b", "c"], merged);
    }

    [Fact]
    public void 新增与重复混合时都进固定列表()
    {
        var merged = DockPinMerge.Merge(["a"], added: ["b"], duplicates: ["c"]);

        Assert.Equal(["a", "b", "c"], merged);
    }

    [Fact]
    public void 已经固定的条目不会被重复追加()
    {
        var merged = DockPinMerge.Merge(["a", "b"], added: ["b"], duplicates: ["a"]);

        Assert.Equal(["a", "b"], merged);
    }

    [Fact]
    public void 同一次导入里同一id出现两次也只固定一次()
    {
        var merged = DockPinMerge.Merge([], added: ["x"], duplicates: ["x"]);

        Assert.Equal(["x"], merged);
    }

    [Fact]
    public void 统计本次真正新增的固定项()
    {
        Assert.Equal(1, DockPinMerge.CountNewlyPinned(["a"], added: ["b"], duplicates: ["a"]));
        Assert.Equal(0, DockPinMerge.CountNewlyPinned(["a"], added: [], duplicates: ["a"]));
    }
}