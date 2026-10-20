using System.Windows.Interop;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Dock;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// Dock 窗口样式的集成冒烟：创建真实的窗口与句柄，读回真实样式。
/// </summary>
/// <remarks>
/// 这是 Windows 集成测试，只证明「样式位挂上了」。
/// 「点击后前台窗口不变」的真实焦点行为属验收清单里的人工/SendInput 项目，
/// 不能用本测试替代。
/// </remarks>
public class DockWindowStyleTests
{
    [Fact]
    public void 显示之后带WS_EX_NOACTIVATE()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                window.Show();

                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);
                Assert.True(
                    WindowStyles.IsNoActivate(handle),
                    "点击 Dock 时不得把用户正在使用的应用顶到后台（阶段 1 记录的缺口）");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void 未附加宿主时设置是默认值()
    {
        StaTest.Run(() =>
        {
            var window = new DockWindow();
            try
            {
                Assert.Equal(DockSettings.FixedIconSize, window.Settings.IconSize);
                Assert.Equal(DockSettings.FixedIconSpacing, window.Settings.IconSpacing);
                Assert.Equal(1.35, window.Settings.MagnificationPeak);
                Assert.Equal(40, window.PanelCornerRadius.TopLeft);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
