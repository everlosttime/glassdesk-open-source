namespace GlassDesk.Core.Models.Settings;

/// <summary>
/// 单个全局热键绑定。
/// </summary>
/// <remarks>
/// 键以文本而不是 <c>System.Windows.Forms.Keys</c> 值保存，这样文件保持可读、
/// 能跨框架版本存活，也不会为了一个热键就把 UI 依赖拖进
/// 本程序集。
/// </remarks>
public sealed class HotkeyDefinition
{
    public bool Enabled { get; set; }

    public bool Control { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    /// <summary>Windows 键。命名为 "Win" 而不是 "Windows"，以免与平台名称冲突。</summary>
    public bool Win { get; set; }

    /// <summary>键名，例如 <c>F2</c> 或 <c>Space</c>。留空表示未绑定。</summary>
    public string Key { get; set; } = string.Empty;

    public bool IsBound => Enabled && !string.IsNullOrWhiteSpace(Key);

    /// <summary>供设置界面展示的可读形式，例如 <c>Ctrl+Shift+Space</c>。</summary>
    public string Describe()
    {
        if (!IsBound)
        {
            return "未设置";
        }

        var parts = new List<string>(5);
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}

/// <summary>可触发启动台的屏幕角落。</summary>
public enum HotCorner
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 3,
    BottomRight = 4,
}

/// <summary>键盘与指针触发方式。</summary>
public sealed class ShortcutSettings
{
    public GlassDesk.Core.Input.InputAction MouseBackAction { get; set; } = GlassDesk.Core.Input.InputAction.ToggleLauncher;

    public GlassDesk.Core.Input.InputAction MouseForwardAction { get; set; }

    public HotkeyDefinition ToggleLauncher { get; set; } = new();

    public HotkeyDefinition HideLauncher { get; set; } = new();

    public HotkeyDefinition NextPage { get; set; } = new() { Enabled = true, Key = "WheelDown" };

    public HotkeyDefinition PreviousPage { get; set; } = new() { Enabled = true, Key = "WheelUp" };

    public HotkeyDefinition ToggleDesktopIcons { get; set; } = new();

    /// <summary>切换 Dock 被遮挡时的鼠标唤出锁；开启后禁止唤出，关闭后允许鼠标移到 Dock 原位置唤出。</summary>
    public HotkeyDefinition ToggleDockRevealLock { get; set; } = new();

    /// <summary>
    /// 热区默认关闭。它很容易被误触发，因此需要用户显式
    /// 开启才会生效。
    /// </summary>
    public bool HotCornersEnabled { get; set; }

    public HotCorner HotCornerTarget { get; set; } = HotCorner.None;

    /// <summary>指针需要在角落停留多久才触发。</summary>
    public int HotCornerDwellMs { get; set; } = 300;

    /// <summary>
    /// 鼠标侧键默认关闭；只有在启用、且按下确实映射到我们的
    /// 某个动作时才会被拦截。
    /// </summary>
    public bool MouseSideButtonsEnabled { get; set; }

    public HotkeyDefinition MouseBackButton { get; set; } = new();

    public HotkeyDefinition MouseForwardButton { get; set; } = new();

    internal void Normalize()
    {
        ToggleLauncher ??= new HotkeyDefinition();
        HideLauncher ??= new HotkeyDefinition();
        NextPage ??= new HotkeyDefinition();
        PreviousPage ??= new HotkeyDefinition();
        ToggleDesktopIcons ??= new HotkeyDefinition();
        ToggleDockRevealLock ??= new HotkeyDefinition();
        MouseBackButton ??= new HotkeyDefinition();
        MouseForwardButton ??= new HotkeyDefinition();
    }
}
