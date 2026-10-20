using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 键盘与鼠标的即时状态。
/// </summary>
/// <remarks>
/// 用 <c>GetAsyncKeyState</c> 读**物理**状态，而不是 WPF 的 <c>Mouse.LeftButton</c>：
/// 后者由窗口消息驱动，而拖放模态循环（<c>DoDragDrop</c>）期间界面线程不在正常派发消息，
/// 那时读到的是过期值。拖拽结束的判定正需要「此刻鼠标还按着吗」这个即时事实。
/// </remarks>
public static class InputState
{
    /// <summary>鼠标主键（左键）此刻是否被按下。</summary>
    /// <remarks>
    /// 用途：区分「松手结束」与「按 Esc 取消」——取消时用户手还按在键上。
    /// 读不到时返回 false（按「已松手」处理），这会走「正常结束」那条路，
    /// 而调用方还有 Esc 事件这一路独立信号兜底。
    /// </remarks>
    public static bool IsPrimaryMouseButtonDown()
    {
        try
        {
            // 高序位表示「此刻按下」。返回值是 short，取符号位即可。
            return (User32.GetAsyncKeyState(User32.VirtualKeyLeftButton) & 0x8000) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}