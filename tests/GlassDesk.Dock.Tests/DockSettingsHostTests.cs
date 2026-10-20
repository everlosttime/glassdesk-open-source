using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 程序坞设置通知口的契约：换快照必须换引用、必须通知，
/// 就地修改后的补充通知不得换引用。
/// </summary>
public class DockSettingsHostTests
{
    [Fact]
    public void Update_换上新快照并在同线程触发Changed()
    {
        var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);

        var fired = 0;
        object? notifiedBy = null;
        host.Changed += (sender, _) =>
        {
            fired++;
            notifiedBy = sender;
        };

        var next = new DockSettings { IconSize = 56, MagnificationPeak = 1.5 };
        host.Update(next);

        Assert.Equal(1, fired);
        Assert.Same(host, notifiedBy);
        Assert.Same(next, host.Settings);
        Assert.Equal(56, host.Settings.IconSize);
    }

    [Fact]
    public void Refresh_不替换引用只补通知()
    {
        var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
        var original = host.Settings;

        var fired = 0;
        host.Changed += (_, _) => fired++;

        // 设置界面就地改值的用法：改完调 Refresh，快照引用保持不变。
        host.Settings.IconSize = 64;
        host.Refresh();

        Assert.Equal(1, fired);
        Assert.Same(original, host.Settings);
        Assert.Equal(64, host.Settings.IconSize);
    }

    [Fact]
    public void Initialize_装上进程级实例()
    {
        try
        {
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            Assert.Same(host, DockSettingsHost.Current);
        }
        finally
        {
            // 静态实例不能泄漏进别的用例。Current 的 setter 是私有的，
            // 反射时必须显式取非公开的 set 方法，PropertyInfo.SetValue 会拒绝。
            var current = typeof(DockSettingsHost).GetProperty(nameof(DockSettingsHost.Current))!;
            current.GetSetMethod(true)!.Invoke(null, new DockSettingsHost?[] { null });
        }
    }
}
