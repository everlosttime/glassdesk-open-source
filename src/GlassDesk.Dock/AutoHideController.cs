using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock;

/// <summary>自动隐藏的瞬时状态。</summary>
public enum AutoHideState
{
    /// <summary>可见。指针在栏上，或离开后还没到延迟。</summary>
    Shown = 0,

    /// <summary>等待收起：指针已离开，延迟计时中。此时再进来就回到 <see cref="Shown"/>。</summary>
    WaitingToHide = 1,

    /// <summary>已收起。热区唤出或别的触发才能回到 <see cref="Shown"/>。</summary>
    Hidden = 2,
}

/// <summary>
/// 自动隐藏的状态机（D-19）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意做成纯状态机：所有输入都带显式时间戳，内部不读时钟、不碰鼠标、
/// 不动窗口——「指针掠过边缘不抖动」这样的时序行为因此可以离线用
/// 任意时间序列回放测试。真正的指针探测与窗口位移在 <see cref="DockWindow"/>。
/// </para>
///
/// <para>
/// 交互规则（修订计划第 4 条，逐条落地）：
/// <list type="number">
/// <item>弹出物（右键菜单、文件夹面板、窗口选择列表）打开期间<strong>不隐藏</strong>
/// ——<see cref="PushSuppress"/>/<see cref="PopSuppress"/>；</item>
/// <item>拖拽进行中不隐藏——同一对抑制口；</item>
/// <item>被覆盖/全屏时<strong>唤出被抑制</strong>（<see cref="DockSettings.BlockRevealWhenCovered"/>
/// 默认开）：全屏应用前缘不该被 Dock 盖住；抑制优先于一切唤出；</item>
/// <item>收起延迟约 300 ms（D-19 的默认值）：指针掠过边缘时,
/// 「离开→重新进入」发生在延迟内，状态机根本不会收起——这就是不抖动的机制。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AutoHideController
{
    private readonly double _leaveDelayMilliseconds;

    private int _suppressCount;
    private DateTime _leftAtUtc;
    private bool _waiting;

    /// <summary>状态变化时触发（含「为什么」），供宿主决定是否移动窗口。</summary>
    public event EventHandler<AutoHideState>? StateChanged;

    public AutoHideState State { get; private set; } = AutoHideState.Shown;

    /// <summary>当前是否有抑制生效（弹出物打开或拖拽进行中）。</summary>
    public bool Suppressed => _suppressCount > 0;

    public AutoHideController(double leaveDelayMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leaveDelayMilliseconds);
        _leaveDelayMilliseconds = leaveDelayMilliseconds;
    }

    /// <summary>指针进入栏内（或热区唤出成功）。</summary>
    public void PointerEnter(DateTime utcNow)
    {
        _ = utcNow;
        _waiting = false;
        Transition(AutoHideState.Shown);
    }

    /// <summary>
    /// 指针离开栏。进入延迟计时；被抑制或在计时中时保持原状。
    /// </summary>
    /// <remarks>
    /// <b>只有首次离开才启动计时。</b>宿主每帧都会调用这里（指针在栏外时），
    /// 若每次都把离开时刻往后推，计时器永远到不了点——实测按产品调用序重放
    /// 近 2 秒仍停在 <see cref="AutoHideState.WaitingToHide"/>，
    /// 表现为自动隐藏彻底失效，而且不报任何错。
    /// </remarks>
    public void PointerLeave(DateTime utcNow)
    {
        if (State == AutoHideState.Hidden || Suppressed)
        {
            return;
        }

        if (_waiting)
        {
            // 已经在计时：这是同一段离开的后续帧，不得重置起点。
            return;
        }

        _leftAtUtc = utcNow;
        _waiting = true;
        Transition(AutoHideState.WaitingToHide);
    }

    /// <summary>周期性检查：延迟已过且未被抑制则收起。</summary>
    /// <remarks>
    /// 由宿主按节拍调用（约 60 Hz 走在指针/动画的既有循环里即可，
    /// 不是独立的轮询线程）。真正的延迟计时只在这里结算一次。
    /// </summary>
    public void Poll(DateTime utcNow)
    {
        if (!_waiting || Suppressed)
        {
            return;
        }

        if ((utcNow - _leftAtUtc).TotalMilliseconds >= _leaveDelayMilliseconds)
        {
            _waiting = false;
            Transition(AutoHideState.Hidden);
        }
    }

    /// <summary>热区唤出。<paramref name="isCovered"/> 为 true 表示 Dock 区域被别的窗口盖住或全屏中。</summary>
    /// <remarks>
    /// 被覆盖禁止唤出（<see cref="DockSettings.BlockRevealWhenCovered"/>）由调用方探测后
    /// 传进来：状态机自己不做窗口查询。抑制优先——被覆盖时热区唤出直接被拒。
    /// </remarks>
    public bool TryReveal(bool isCovered, DateTime utcNow)
    {
        if (State == AutoHideState.Shown)
        {
            return true;
        }

        if (State == AutoHideState.WaitingToHide)
        {
            // 延迟计时中的「唤出」= 指针又回来了：必须真正回到可见并取消等待，
            // 否则计时器到点照样把窗口收走——指针明明就在栏上。
            PointerEnter(utcNow);
            return true;
        }

        if (isCovered)
        {
            return false;
        }

        PointerEnter(utcNow);
        return true;
    }

    /// <summary>弹出物打开或拖拽开始：隐藏计时暂停。可重入（面板叠面板）。</summary>
    public void PushSuppress(DateTime utcNow)
    {
        _ = utcNow;
        _suppressCount++;
    }

    /// <summary>弹出物关闭或拖拽结束。最后一层解除后，重新按延迟收起。</summary>
    /// <remarks>
    /// 指针此刻多半已经不在栏上，所以解除时按「刚刚离开」重新计时，
    /// 而不是立刻收起——否则面板一关 Dock 闪一下就没了。
    /// </remarks>
    public void PopSuppress(DateTime utcNow)
    {
        if (_suppressCount == 0)
        {
            return;
        }

        _suppressCount--;

        if (_suppressCount == 0 && State == AutoHideState.Shown)
        {
            PointerLeave(utcNow);
        }
    }

    /// <summary>禁用自动隐藏时的初始状态。</summary>
    public void ForceShown(DateTime utcNow) => PointerEnter(utcNow);

    /// <summary>忽略普通离开延迟立即收起，供全屏/遮挡和显式唤出锁定使用。</summary>
    public void ForceHide(DateTime utcNow)
    {
        _leftAtUtc = utcNow;
        _waiting = false;
        Transition(AutoHideState.Hidden);
    }

    private void Transition(AutoHideState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, next);
    }
}
