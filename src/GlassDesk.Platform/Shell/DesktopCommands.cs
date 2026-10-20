using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 桌面级批量窗口动作：「显示桌面」（D-13）。
/// </summary>
/// <remarks>
/// 实现依据阶段 4 探针 D 的实测结论（<c>docs/evidence/阶段4-探针实测输出.txt</c>）：
/// 逐窗口 <c>SW_MINIMIZE</c> 是唯一可靠路径——
/// <c>WM_SYSCOMMAND/SC_MINIMIZE</c> 经投递实测最小化不了靶子，
/// 外壳「显示桌面」动词影响用户整个桌面且不可控，均不采用。
///
/// <para>
/// 候选窗口复用 <see cref="WindowLocator"/> 的候选规则（可见、有标题、非工具窗口），
/// 因此桌面（<c>Progman</c>）与各类辅助窗口天然被排除；本进程自己的窗口
/// （Dock、启动台、弹出物）也必须排除——「显示桌面」不该把 Dock 自己收掉。
/// 已最小化的窗口跳过：它们已经在桌面上看不见了。
/// </para>
/// </remarks>
public static class DesktopCommands
{
    /// <summary>
    /// 把当前所有可见的应用窗口最小化，返回被最小化的数量。
    /// </summary>
    /// <remarks>
    /// 焦点在最后一条 <c>ShowWindow</c> 之后由系统落到下一个合适窗口；
    /// 探针实测这一行为正常，无需额外干预。
    /// </remarks>
    public static int ShowDesktop()
    {
        var minimized = 0;
        var ownProcessId = (uint)Environment.ProcessId;

        foreach (var window in WindowLocator.EnumerateTopLevelWindows())
        {
            if (window.ProcessId == ownProcessId || window.IsMinimized)
            {
                continue;
            }

            if (WindowControl.Minimize(window.Handle))
            {
                minimized++;
            }
        }

        return minimized;
    }
}
