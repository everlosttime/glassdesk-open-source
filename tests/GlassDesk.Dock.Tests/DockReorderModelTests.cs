using System.IO;
using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 拖拽排序状态机的断言——R-08 的五条验收在这里锁住：
/// 预览期间零次落盘、提交恰好一次、Esc 取消零次落盘、提交失败恢复原序、
/// 移出取消固定不删库条目（最后一条在验收命令的服务层覆盖）。
/// </summary>
public class DockReorderModelTests
{
    private static readonly string[] Original = ["a", "b", "c", "d"];

    [Fact]
    public void 预览任意多次_不落盘()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        model.Begin(Original);
        model.PreviewMove("c", 0);
        model.PreviewMove("c", 1);
        model.PreviewMove("d", 0);
        model.PreviewMove("a", 3);

        Assert.Equal(0, saveCount);
        Assert.True(model.IsActive);
    }

    [Fact]
    public void 松手提交_恰好落盘一次_顺序正确()
    {
        var saved = new List<IReadOnlyList<string>>();
        var model = new DockReorderModel(order => saved.Add([.. order]));

        model.Begin(Original);
        model.PreviewMove("c", 0);

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.Committed, outcome);
        var save = Assert.Single(saved);
        Assert.Equal(["c", "a", "b", "d"], save);
    }

    [Fact]
    public void 放回原位_提交不落盘()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        model.Begin(Original);
        model.PreviewMove("b", 2); // 移走
        model.PreviewMove("b", 1); // 又放回去

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.Unchanged, outcome);
        Assert.Equal(0, saveCount);
    }

    [Fact]
    public void Esc取消_不落盘_顺序原样()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        model.Begin(Original);
        model.PreviewMove("d", 0);

        var outcome = model.Cancel();

        Assert.Equal(ReorderOutcome.Cancelled, outcome);
        Assert.Equal(0, saveCount);
        Assert.Equal(Original, model.Order);
    }

    [Fact]
    public void 提交失败_恢复原顺序并把失败交出来()
    {
        var model = new DockReorderModel(_ => throw new IOException("磁盘拒绝"));

        model.Begin(Original);
        model.PreviewMove("b", 3);

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.SaveFailed, outcome);
        Assert.Equal(Original, model.Order);
        Assert.False(model.IsActive);
    }

    [Fact]
    public void 未开始拖拽时提交是空操作()
    {
        var saveCount = 0;
        var model = new DockReorderModel(_ => saveCount++);

        var outcome = model.Commit();

        Assert.Equal(ReorderOutcome.Idle, outcome);
        Assert.Equal(0, saveCount);
    }

    [Fact]
    public void 预览目标索引越界时按边界处理()
    {
        var model = new DockReorderModel(_ => { });

        model.Begin(Original);
        model.PreviewMove("a", 100); // 夹到最后

        Assert.Equal(["b", "c", "d", "a"], model.Order);
    }
}
