using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>投递到托盘回调窗口的消息意味着什么。</summary>
public enum TrayEvent
{
    /// <summary>不是我们关心的托盘通知。</summary>
    None = 0,

    /// <summary>左键单击：用户想要主界面。</summary>
    Activate = 1,

    /// <summary>右键单击：显示上下文菜单。</summary>
    ContextMenu = 2,

    /// <summary>双击。</summary>
    DoubleClick = 3,
}

/// <summary>
/// 持有一个通知区域图标。
/// </summary>
/// <remarks>
/// shell 把托盘输入作为普通窗口消息投递，
/// 所以调用方提供宿主窗口并把它的消息路由给 <see cref="Classify"/>。
/// 把窗口的所有权放在别处，能保持本类可测试，
/// 也让宿主可以选择一个从不显示在屏幕上的仅消息窗口。
///
/// 图标句柄归本实例所有，并在 <see cref="Dispose"/> 时销毁。
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>shell 向宿主窗口投递本图标消息时使用的标识。</summary>
    public const int CallbackMessage = Shell32.WmTrayCallback;

    private readonly nint _ownerWindow;
    private readonly int _id;

    private nint _icon;
    private string _tooltip = string.Empty;
    private bool _added;
    private bool _disposed;

    public TrayIcon(nint ownerWindow, int id = 1)
    {
        if (ownerWindow == 0)
        {
            throw new ArgumentException("托盘图标需要一个有效的窗口句柄。", nameof(ownerWindow));
        }

        _ownerWindow = ownerWindow;
        _id = id;
    }

    /// <summary>图标确实已出现在通知区域后为 true。</summary>
    public bool IsVisible => _added;

    /// <summary>
    /// 把图标加入通知区域。
    /// </summary>
    /// <param name="icon">图标句柄。所有权转移给本实例。</param>
    public bool Show(nint icon, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TakeOwnership(icon);
        _tooltip = tooltip;

        var data = CreateData();
        _added = Shell32.Shell_NotifyIcon(Shell32.NimAdd, ref data);

        if (_added)
        {
            // 版本 3 让 shell 在 lParam 中报告点击，而不是发送传统的按按钮消息，
            // 下面的 Classify 正是按此预期解析。
            var version = CreateData();
            version.VersionOrTimeout = Shell32.NotifyIconVersion3;
            Shell32.Shell_NotifyIcon(Shell32.NimSetVersion, ref version);
        }

        return _added;
    }

    /// <summary>替换已显示在屏幕上的图标的图形或提示文本。</summary>
    public bool Update(nint icon, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_added)
        {
            return Show(icon, tooltip);
        }

        TakeOwnership(icon);
        _tooltip = tooltip;

        var data = CreateData();
        return Shell32.Shell_NotifyIcon(Shell32.NimModify, ref data);
    }

    /// <summary>把图标从通知区域移除。</summary>
    public void Hide()
    {
        if (!_added || _disposed)
        {
            return;
        }

        var data = CreateData();
        Shell32.Shell_NotifyIcon(Shell32.NimDelete, ref data);
        _added = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();

        // 释放我们持有的图标句柄；shell 已经复制过它。
        if (_icon != 0)
        {
            IconFactory.DestroyIcon(_icon);
            _icon = 0;
        }

        _disposed = true;
    }

    /// <summary>
    /// 解析 shell 为托盘通知投递的 <c>lParam</c>。
    /// </summary>
    /// <remarks>
    /// 在版本 3 语义下，鼠标事件位于 lParam 的低位字。
    /// 高位字携带图标 id，这里用不到。
    /// </remarks>
    public static TrayEvent Classify(int lParam)
    {
        var mouseMessage = lParam & 0xFFFF;

        return mouseMessage switch
        {
            Shell32.WmLButtonUp => TrayEvent.Activate,
            Shell32.WmRButtonUp => TrayEvent.ContextMenu,
            Shell32.WmLButtonDblClk => TrayEvent.DoubleClick,
            _ => TrayEvent.None,
        };
    }

    private void TakeOwnership(nint icon)
    {
        if (_icon == icon)
        {
            return;
        }

        if (_icon != 0)
        {
            IconFactory.DestroyIcon(_icon);
        }

        _icon = icon;
    }

    private unsafe Shell32.NotifyIconData CreateData()
    {
        var data = new Shell32.NotifyIconData
        {
            Size = sizeof(Shell32.NotifyIconData),
            WindowHandle = _ownerWindow,
            Id = _id,
            Flags = Shell32.NifMessage | Shell32.NifIcon | Shell32.NifTip,
            CallbackMessage = CallbackMessage,
            IconHandle = _icon,
        };

        Shell32.NotifyIconData.SetText(data.Tip, Shell32.TipLength, _tooltip);
        return data;
    }
}