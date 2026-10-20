using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 自动隐藏状态机的时序断言（D-19）。时间用显式时间戳回放——
/// 「指针掠过边缘不抖动」这类行为靠时间序列锁住，不碰真实时钟与鼠标。
/// </summary>
public class AutoHideControllerTests
{
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>D-19 的默认值：约 300 ms。</summary>
    private const double DelayMs = 300;

    [Fact]
    public void 指针掠过边缘_300毫秒内重新进入_不收起()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0.AddMilliseconds(80));   // 掠过边缘
        controller.Poll(T0.AddMilliseconds(200));          // 延迟未到
        controller.PointerEnter(T0.AddMilliseconds(250));  // 又回来了

        controller.Poll(T0.AddSeconds(5));

        Assert.Equal(AutoHideState.Shown, controller.State);
    }

    [Fact]
    public void 指针离开超过延迟后才收起()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0);

        controller.Poll(T0.AddMilliseconds(299));
        Assert.Equal(AutoHideState.WaitingToHide, controller.State);

        controller.Poll(T0.AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void 按产品的每帧调用序_指针离开后约300毫秒真的收起()
    {
        // 复现方式：按 DockWindow.OnAutoHideFrame 的真实调用顺序，
        // 每 16 毫秒重放「探测 → PointerLeave/PointerEnter → Poll」。
        //
        // 这个形态曾经出过一个不报错的缺陷：一帧一次 PointerLeave 会把
        // 离开时刻一直往后推，计时器永远到不了点——实测重放近 2 秒仍停在
        // WaitingToHide。修法是「只有首次离开才启动计时」。
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);

        // 指针在第二帧离开，此后一直不在栏上。
        var hiddenAtMs = -1;

        for (var milliseconds = 0; milliseconds <= 2000; milliseconds += 16)
        {
            var now = T0.AddMilliseconds(milliseconds);

            if (milliseconds < 16)
            {
                controller.PointerEnter(now);
            }
            else
            {
                controller.PointerLeave(now);
            }

            controller.Poll(now);

            if (controller.State == AutoHideState.Hidden)
            {
                hiddenAtMs = milliseconds;
                break;
            }
        }

        Assert.Equal(AutoHideState.Hidden, controller.State);
        Assert.InRange(hiddenAtMs, 300, 500); // 16 毫秒的帧粒度 + 一点余量
    }

    [Fact]
    public void 每帧重复离开不重置计时_但重新进入后再离开要重新计时()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);

        // 连续 10 帧「离开」：计时从第一帧算起。
        for (var milliseconds = 0; milliseconds < 160; milliseconds += 16)
        {
            controller.PointerLeave(T0.AddMilliseconds(milliseconds));
        }

        controller.Poll(T0.AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);

        // 重新进入再离开：这是新的一次离开，必须重新计时满 300 毫秒。
        controller.PointerEnter(T0.AddSeconds(1));
        controller.PointerLeave(T0.AddSeconds(1).AddMilliseconds(10));
        controller.Poll(T0.AddSeconds(1).AddMilliseconds(200));
        Assert.Equal(AutoHideState.WaitingToHide, controller.State);

        controller.Poll(T0.AddSeconds(1).AddMilliseconds(310));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void 弹出物打开期间不隐藏_关闭后重新计时()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PushSuppress(T0);                       // 右键菜单打开

        controller.PointerLeave(T0.AddMilliseconds(100));
        controller.Poll(T0.AddSeconds(10));
        Assert.Equal(AutoHideState.Shown, controller.State);

        controller.PopSuppress(T0.AddSeconds(10));         // 菜单关闭
        controller.Poll(T0.AddSeconds(10).AddMilliseconds(150));
        Assert.Equal(AutoHideState.WaitingToHide, controller.State); // 重新计时，不闪没

        controller.Poll(T0.AddSeconds(10).AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void 抑制可重入_面板叠面板时最外层关闭才算数()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PushSuppress(T0);                       // 文件夹面板
        controller.PushSuppress(T0);                       // 面板上又开了窗口选择列表

        controller.PointerLeave(T0);
        controller.PopSuppress(T0.AddSeconds(1));          // 关了一层
        controller.Poll(T0.AddSeconds(30));
        Assert.Equal(AutoHideState.Shown, controller.State);

        controller.PopSuppress(T0.AddSeconds(30));         // 最后一层关闭
        controller.Poll(T0.AddSeconds(30).AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void 拖拽抑制与弹出物抑制走同一对口子()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PushSuppress(T0);                       // 拖拽开始
        controller.PointerLeave(T0.AddMilliseconds(10));   // 拖着图标离开了栏

        controller.Poll(T0.AddMinutes(1));
        Assert.Equal(AutoHideState.Shown, controller.State);

        controller.PopSuppress(T0.AddMinutes(1));          // 松手
        controller.Poll(T0.AddMinutes(1).AddMilliseconds(300));
        Assert.Equal(AutoHideState.Hidden, controller.State);
    }

    [Fact]
    public void 被覆盖时热区唤出被拒_抑制优先()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0);
        controller.Poll(T0.AddSeconds(1));
        Assert.Equal(AutoHideState.Hidden, controller.State);

        // 全屏应用在前：热区唤出被拒绝（DockSettings.BlockRevealWhenCovered 的语义）。
        Assert.False(controller.TryReveal(isCovered: true, T0.AddSeconds(2)));
        Assert.Equal(AutoHideState.Hidden, controller.State);

        // 回到桌面：唤出成功。
        Assert.True(controller.TryReveal(isCovered: false, T0.AddSeconds(3)));
        Assert.Equal(AutoHideState.Shown, controller.State);
    }

    [Fact]
    public void 等待期内热区唤出维持可见()
    {
        var controller = new AutoHideController(DelayMs);

        controller.PointerEnter(T0);
        controller.PointerLeave(T0);
        controller.Poll(T0.AddMilliseconds(100));          // WaitingToHide

        // 指针又碰到了热区（还没走远）：直接回到可见，不重新闪现。
        Assert.True(controller.TryReveal(isCovered: false, T0.AddMilliseconds(150)));
        Assert.Equal(AutoHideState.Shown, controller.State);
    }
}
