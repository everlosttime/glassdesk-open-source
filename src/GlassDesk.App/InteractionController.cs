using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App;

/// <summary>Connects registered hotkeys to product actions.</summary>
internal sealed class InteractionController : IDisposable
{
    private readonly GlassDeskDataStore _data;
    private readonly Dispatcher _dispatcher;
    private readonly Action<InputAction> _execute;
    private readonly Action<string> _reportError;
    private readonly InteractionSettingsService _settings;
    private readonly DesktopIconSession _desktopIcons = new();
    private bool _disposed;
    private volatile bool _desktopAvailable;
    private volatile bool _launcherVisible;
    private volatile bool _canNavigatePages;

    public InteractionController(GlassDeskDataStore data, PrimaryDisplaySession primaryDisplay, nint hwnd, Dispatcher dispatcher,
        Func<InputAction, bool> canExecute, Action<InputAction> execute, Action<string> reportError)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _ = primaryDisplay ?? throw new ArgumentNullException(nameof(primaryDisplay));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(canExecute);
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _settings = new InteractionSettingsService(data.Settings, new NativeHotkeyRegistry(hwnd,
            id => CanExecute(_settings!.ResolveAction(id)) && canExecute(_settings.ResolveAction(id))));
    }

    public string? Initialize()
    {
        if (_disposed) return "快捷操作服务已经关闭。";
        _desktopAvailable = _desktopIcons.AreIconsVisible().HasValue;
        var before = SettingsPageEditor.Clone(_data.Settings.Current);
        var display = new DisplaySettings
        {
            DockDisplayId = before.Display.DockDisplayId ?? before.Dock.TargetDisplayId,
            LauncherFollowsPointer = before.Display.LauncherFollowsPointer,
            SuppressTriggersWhenFullscreen = before.Display.SuppressTriggersWhenFullscreen,
        };
        return ApplyCore(before.Shortcuts, display, persist: false);
    }

    public string? Apply(ShortcutSettings shortcuts, DisplaySettings display)
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return "快捷操作服务已经关闭。";
        return ApplyCore(shortcuts, display, persist: true);
    }

    private string? ApplyCore(ShortcutSettings shortcuts, DisplaySettings display, bool persist)
    {
        shortcuts = SettingsPageEditor.Clone(new GlassDeskSettings { Shortcuts = shortcuts }).Shortcuts;
        shortcuts.HotCornersEnabled = false;
        shortcuts.MouseSideButtonsEnabled = false;
        var needsDesktop = shortcuts.ToggleDesktopIcons.Enabled;
        if (needsDesktop && !_desktopAvailable) return "当前 Windows 桌面图标状态不可读，已拒绝启用相关动作。";
        var error = _settings.Apply(shortcuts, display, persist);
        if (error is not null) return error;
        RefreshDisplays();
        return null;
    }

    public void SetLauncherAvailability(bool visible, bool canNavigate)
    {
        _launcherVisible = visible;
        _canNavigatePages = canNavigate;
    }

    public bool CanExecute(InputAction action) => action switch
    {
        InputAction.None => false,
        InputAction.ToggleLauncher => true,
        InputAction.HideLauncher => _launcherVisible,
        InputAction.NextPage or InputAction.PreviousPage => _canNavigatePages,
        InputAction.ToggleDesktopIcons => _desktopAvailable,
        InputAction.ToggleDockRevealLock => true,
        _ => false,
    };

    public void HandleHotkey(int id)
    {
        if (_disposed) return;
        var action = _settings.ResolveAction(id);
        if (CanExecute(action)) _execute(action);
    }

    public bool? ReadDesktopIcons()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return null;
        var state = _desktopIcons.AreIconsVisible();
        _desktopAvailable = state.HasValue;
        return state;
    }

    public string? SetDesktopIcons(bool visible)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_desktopAvailable) return "当前 Windows 不支持切换桌面图标。";
        if (!_desktopIcons.TrySetVisible(visible, out var error))
        {
            _desktopAvailable = _desktopIcons.AreIconsVisible().HasValue;
            return error ?? _desktopIcons.Status;
        }
        _desktopAvailable = _desktopIcons.AreIconsVisible().HasValue;
        return null;
    }

    public void ToggleDesktopIcons()
    {
        var current = ReadDesktopIcons();
        if (!current.HasValue) { _reportError(_desktopIcons.Status); return; }
        var error = SetDesktopIcons(!current.Value);
        if (error is not null) _reportError(error);
    }

    public void RefreshDisplays() => _desktopAvailable = _desktopIcons.AreIconsVisible().HasValue;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Dispose();
        _desktopIcons.Dispose();
    }
}
