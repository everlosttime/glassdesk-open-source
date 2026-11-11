using System.Diagnostics;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 运行前的环境门禁。
/// </summary>
/// <remarks>
/// 这个驱动会真的移动鼠标、真的按下按键。做错的环境里跑它，轻则测不出东西，
/// 重则打扰正在用这台机器的人。所以先回答一个问题：
/// <strong>此刻有没有一个只有测试在用的交互式桌面？</strong>
/// 答不上来就记「阻塞」并退出，一个输入事件都不发。
///
/// 三条独立的判据，缺一条都算阻塞：
/// <list type="number">
/// <item>调用方明确声明「我在隔离桌面上」（不声明就绝不注入输入）；</item>
/// <item>进程连着一个可见的窗口站，而不是服务会话；</item>
/// <item>进程所在的桌面就是当前的输入桌面，且不是锁屏桌面。</item>
/// </list>
/// </remarks>
internal static class DesktopGate
{
    internal const string OptInFlag = "--i-am-on-isolated-desktop";

    internal const string OptInEnvironmentVariable = "GLASSDESK_ISOLATED_DESKTOP";

    /// <summary>回到 null 表示环境满足；否则是要写进报告的原因。</summary>
    internal static string? BlockedReason(bool optedIn)
    {
        if (!optedIn)
        {
            return "没有声明在隔离桌面上。这个驱动会真实移动鼠标并按键，"
                + $"必须在独立测试账户或虚拟机里运行，并显式传入 {OptInFlag}"
                + $"（或设置环境变量 {OptInEnvironmentVariable}=1）。"
                + "没有声明时它一个输入事件都不会发。";
        }

        if (IntPtr.Size != 8)
        {
            return $"驱动运行在 {IntPtr.Size * 8} 位进程里，输入结构布局与注入坐标只在 64 位下验证过。";
        }

        if (Win32.Monitors().Count == 0)
        {
            return "枚举不到任何显示器，没有可以定位控件的桌面。";
        }

        if (!Win32.WindowStationIsVisible())
        {
            return "本进程的窗口站不可见：这是服务会话，不是用户正在看的交互式桌面。"
                + "窗口画不出来，鼠标键盘语义也不成立。";
        }

        var current = Win32.CurrentThreadDesktopName();
        if (string.IsNullOrEmpty(current))
        {
            return "读不出本进程所在的桌面名。";
        }

        var input = Win32.InputDesktopName();
        if (string.IsNullOrEmpty(input))
        {
            return $"打不开输入桌面（本进程在「{current}」）。会话很可能已锁屏或已断开。";
        }

        if (!string.Equals(current, input, StringComparison.OrdinalIgnoreCase))
        {
            return $"本进程在桌面「{current}」，而输入桌面是「{input}」。"
                + (input.Equals("Winlogon", StringComparison.OrdinalIgnoreCase)
                    ? "输入桌面是 Winlogon，说明会话处于锁屏状态，此时注入输入不会到达任何应用窗口。"
                    : "两者不是同一个桌面，注入的输入不会到达被测窗口。");
        }

        if (!current.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return $"输入桌面名是「{current}」而不是 Default，无法确认这是一个可用的交互式桌面。";
        }

        return null;
    }

    /// <summary>本次会话是否已经有一个 GlassDesk 在跑。有的话窗口归属无法归因，只能阻塞。</summary>
    internal static string? ExistingInstanceReason()
    {
        var running = Process.GetProcessesByName("GlassDesk");

        try
        {
            var others = running.Where(process => process.Id != Environment.ProcessId).ToList();
            if (others.Count == 0)
            {
                return null;
            }

            var described = string.Join(
                "；",
                others.Select(process => $"pid {process.Id}"));

            return $"本会话已经有 GlassDesk 在运行（{described}）。"
                + "独立桌面要求一个干净会话：否则新建的窗口归谁所有无法归因，"
                + "单实例转发还会让新进程直接退出。请先结束它，或在新的测试账户里跑。";
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }
}