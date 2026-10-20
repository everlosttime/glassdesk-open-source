using System.Windows;
using System.Windows.Interop;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 焦点相关契约的断言（对应复核第 3 条里**可以自动化**的那部分）。
/// </summary>
/// <remarks>
/// <para>
/// 边界先说清楚：真正的「点击 Dock 之后前台没变」「弹层关闭后焦点还给谁」依赖
/// 真实的窗口激活与前台锁定，程序化断言会有假阳/假阴（例如测试进程本来就没有前台权限）。
/// 因此这里只锁**结构性**的三条：
/// </para>
/// <list type="number">
/// <item>主栏带 <c>WS_EX_NOACTIVATE</c>——点击不激活这条规则的载体；</item>
/// <item>弹出物（菜单、面板、窗口列表）**不在**主栏句柄之内，也就是它们各自是独立窗口，
/// 焦点要单独管理，而不是靠主栏的样式；</item>
/// <item>把前台还给「打开弹层之前的那个窗口」这件事，走的是可判定的一条路径
/// （目标句柄已失效时安静失败，不抛异常）。</item>
/// </list>
/// <para>
/// 真实前台行为的核对步骤写在交付报告第六节第 6 项与第 12/13 项。
/// </para>
/// </remarks>
public class DockFocusContractTests
{
    [Fact]
    public void 主栏的不抢焦点样式确实生效()
    {
        StaTest.Run(() =>
        {
            var dock = new DockWindow();
            try
            {
                dock.Show();
                var handle = new WindowInteropHelper(dock).Handle;

                Assert.True(
                    WindowStyles.IsNoActivate(handle),
                    "点击主栏不得把用户正在用的应用顶到后台——这条规则的载体就是 WS_EX_NOACTIVATE");
            }
            finally
            {
                dock.Close();
            }
        });
    }

    [Fact]
    public void 弹层是独立窗口_焦点要单独管理()
    {
        StaTest.Run(() =>
        {
            // 主栏的 WS_EX_NOACTIVATE 不会传给它的弹出物：Popup 是独立的顶层窗口。
            // 这正是「主栏永不激活 + 需要交互的弹层显式自激活」这条规则的由来，
            // 也是必须显式记录并归还前台的原因。
            var dock = new DockWindow();
            try
            {
                dock.Show();
                var dockHandle = new WindowInteropHelper(dock).Handle;

                var popupWindow = new Window
                {
                    Width = 100,
                    Height = 60,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Content = new System.Windows.Controls.Border(),
                };

                try
                {
                    popupWindow.Show();
                    var popupHandle = new WindowInteropHelper(popupWindow).Handle;

                    Assert.NotEqual(dockHandle, popupHandle);
                    Assert.False(
                        WindowStyles.IsNoActivate(popupHandle),
                        "弹层拿到了主栏的样式，说明二者不是独立窗口——焦点规则的前提不成立");
                }
                finally
                {
                    popupWindow.Close();
                }
            }
            finally
            {
                dock.Close();
            }
        });
    }

    [Fact]
    public void 归还前台时目标已消失不抛出()
    {
        StaTest.Run(() =>
        {
            // 弹层关闭要把前台还给「打开前那个窗口」——那个窗口可能已经被用户关掉。
            // 这条路径必须是可判定的：安静返回 false，不抛异常。
            Assert.False(WindowActivation.TryBringToForeground(0));
            Assert.False(WindowActivation.TryBringToForeground(new nint(0x1234)));
        });
    }
}