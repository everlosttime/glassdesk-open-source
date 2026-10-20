using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.UI.Settings;

/// <summary>
/// 程序坞设置的唯一事实来源与变更通知口。
/// </summary>
/// <remarks>
/// 仿 <see cref="GlassDesk.UI.Theming.ThemeService"/> 的先例，刻意做成进程级：
/// 生效中的程序坞设置只有一个，Dock 窗口与之后的设置界面必须看到同一份。
///
/// 为什么需要它：设置即时生效（功能清单 D-16/D-17）意味着设置一变，
/// 已经打开的 Dock 就得跟着变。通知从这里**推**过去，
/// Dock 不得自己去轮询设置文件。
/// </remarks>
public sealed class DockSettingsHost
{
    private readonly Dispatcher _dispatcher;

    public DockSettingsHost(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        Settings = new DockSettings();
    }

    /// <summary>本进程唯一的程序坞设置宿主。</summary>
    public static DockSettingsHost? Current { get; private set; }

    /// <summary>创建并安装进程级实例。</summary>
    public static DockSettingsHost Initialize(Dispatcher dispatcher)
    {
        Current = new DockSettingsHost(dispatcher);
        return Current;
    }

    /// <summary>当前生效的设置快照。只在 <see cref="Update"/> 时整体替换。</summary>
    public DockSettings Settings { get; private set; }

    /// <summary>设置变化后在 UI 线程上触发。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 采用新的设置并通知每个监听者。
    /// </summary>
    /// <remarks>
    /// 与主题不同，这里没有任何派生状态需要先重算——设置值本身即生效，
    /// 所以无论内容有没有变都直接通知，由监听者决定重刷什么。
    /// </remarks>
    public void Update(DockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        NotifyChanged();
    }

    /// <summary>
    /// 设置对象被就地修改后调用：不替换引用，只补一次通知。
    /// </summary>
    public void Refresh() => NotifyChanged();

    private void NotifyChanged()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        // 已在 UI 线程上就直接触发，省掉一次无谓的调度往返；
        // 从其它线程调用时转投到 UI 线程，监听者因此可以放心碰界面。
        if (_dispatcher.CheckAccess())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(NotifyChanged);
        }
    }
}
