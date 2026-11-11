using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>Native desktop ownership without changing a window's DPI, coordinates or top-level rendering.</summary>
public sealed class DesktopWindowAttachment : IDisposable
{
    private static readonly object MembershipGate = new();
    private static readonly HashSet<nint> RegisteredWindows = [];
    public static bool HasAttachedWindows { get { lock (MembershipGate) return RegisteredWindows.Count > 0; } }
    private readonly nint _window, _originalOwner;
    private readonly Action<nint> _setOwner;
    private readonly Func<nint> _desktopProvider;
    private bool _disposed;

    public DesktopWindowAttachment(nint window, Action<nint> setOwner)
        : this(window, setOwner, FindDesktopHost) { }

    internal DesktopWindowAttachment(nint window, Action<nint> setOwner, Func<nint> desktopProvider)
    {
        _window = window;
        _originalOwner = User32.GetWindow(window, 4);
        _setOwner = setOwner ?? throw new ArgumentNullException(nameof(setOwner));
        _desktopProvider = desktopProvider ?? throw new ArgumentNullException(nameof(desktopProvider));
    }

    public nint DesktopHandle { get; private set; }
    public bool IsAttached => !_disposed && DesktopHandle != 0 && User32.IsWindow(DesktopHandle)
        && User32.IsWindow(_window) && User32.GetWindow(_window, 4) == DesktopHandle;

    public bool TryAttach()
    {
        if (_disposed || !User32.IsWindow(_window)) return false;
        var desktop = _desktopProvider();
        if (desktop == 0 || desktop == _window || !User32.IsWindow(desktop)) return false;
        if (desktop == DesktopHandle && IsAttached) return true;
        try { _setOwner(desktop); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { return false; }
        if (User32.GetWindow(_window, 4) != desktop) return false;
        DesktopHandle = desktop;
        lock (MembershipGate) RegisteredWindows.Add(_window);
        return true;
    }

    public void Reconcile(bool presenting = false)
    {
        if (!TryAttach() || !presenting || !User32.IsWindowVisible(_window)) return;
        // Settle initial presentation only. Explorer owns subsequent desktop-group
        // transitions, including its list of windows to restore on the next Win+D.
        // A foreground callback must not reorder that group during the transition.
        nint lastApplication = 0;
        var members = new List<nint>();
        HashSet<nint> registered;
        lock (MembershipGate) registered = [.. RegisteredWindows];
        _ = User32.EnumWindows((window, parameter) =>
        {
            if (BelongsToDesktop(window, DesktopHandle))
            {
                // Interactive popovers can be owned by a bar without becoming a
                // desktop surface. Only registered bars and shadows are relayered.
                if (registered.Contains(window)) members.Add(window);
                return 1;
            }
            if (window == DesktopHandle || !User32.IsWindowVisible(window) || User32.IsIconic(window)
                || DwmApi.IsCloaked(window)) return 1;
            var style = User32.GetWindowLong(window, User32.GwlExStyle);
            if ((style & (8 | 0x80 | 0x08000000)) != 0) return 1;
            if (DisplayService.GetWindowPixelBounds(window) is not { Width: > 0, Height: > 0 }) return 1;
            if (User32.ReadClassName(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return 1;
            lastApplication = window;
            return 1;
        }, 0);
        // Move the desktop group as a unit. Moving an owned bar alone cannot put it
        // below an application that its desktop owner currently covers.
        if (lastApplication != 0)
        {
            _ = Dpi32.SetWindowPos(DesktopHandle, lastApplication, 0, 0, 0, 0, 0x0613);
            foreach (var member in members)
            {
                _ = Dpi32.SetWindowPos(member, lastApplication, 0, 0, 0, 0, 0x0613);
                lastApplication = member;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (MembershipGate) RegisteredWindows.Remove(_window);
        if (DesktopHandle != 0 && User32.IsWindow(_window) && User32.GetWindow(_window, 4) == DesktopHandle)
        {
            var restore = User32.IsWindow(_originalOwner) ? _originalOwner : 0;
            try { _setOwner(restore); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        }
        DesktopHandle = 0;
    }

    public static nint FindDesktopHost()
    {
        var shell = User32.GetShellWindow();
        if (shell == 0) return 0;
        _ = User32.GetWindowThreadProcessId(shell, out var shellProcess);
        nint host = 0;
        _ = User32.EnumWindows((window, parameter) =>
        {
            _ = User32.GetWindowThreadProcessId(window, out var process);
            if (process != shellProcess || User32.FindWindowEx(window, 0, "SHELLDLL_DefView", null) == 0) return 1;
            host = window;
            return 0;
        }, 0);
        return host != 0 ? host : shell;
    }

    public static bool IsDesktopShowing(nint desktop)
    {
        if (desktop == 0 || !User32.IsWindowVisible(desktop)) return false;
        var bounds = DisplayService.GetWindowPixelBounds(desktop);
        var showing = false;
        _ = User32.EnumWindows((window, parameter) =>
        {
            if (window == desktop) { showing = true; return 0; }
            if (!User32.IsWindowVisible(window) || User32.IsIconic(window) || DwmApi.IsCloaked(window)) return 1;
            var style = User32.GetWindowLong(window, User32.GwlExStyle);
            if ((style & 8) != 0 || (style & 0x08000080) == 0x08000080 || BelongsToDesktop(window, desktop)) return 1;
            if (bounds is { } area && DisplayService.GetWindowPixelBounds(window) is { } candidate
                && (candidate.Width <= 0 || candidate.Height <= 0 || candidate.Right <= area.X
                    || candidate.X >= area.Right || candidate.Bottom <= area.Y || candidate.Y >= area.Bottom)) return 1;
            return 0;
        }, 0);
        return showing;
    }

    private static bool BelongsToDesktop(nint window, nint desktop)
    {
        for (var owner = User32.GetWindow(window, 4); owner != 0; owner = User32.GetWindow(owner, 4))
            if (owner == desktop) return true;
        return false;
    }
}
