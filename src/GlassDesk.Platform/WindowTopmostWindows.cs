using System.Diagnostics;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>A physical-pixel snapshot of an application's original top-level window.</summary>
public sealed record WindowTopmostTarget(nint Handle, uint ProcessId, uint ThreadId, long ProcessStarted,
    string Title, PixelRect Bounds, bool IsTopmost)
{
    internal long ObservedAt { get; } = Stopwatch.GetTimestamp();

    public bool Equals(WindowTopmostTarget? other) => ReferenceEquals(this, other) || other is not null
        && Handle == other.Handle && ProcessId == other.ProcessId && ThreadId == other.ThreadId
        && ProcessStarted == other.ProcessStarted && Title == other.Title && Bounds == other.Bounds && IsTopmost == other.IsTopmost;

    public override int GetHashCode() => HashCode.Combine(Handle, ProcessId, ThreadId, ProcessStarted, Title, Bounds, IsTopmost);
}

/// <summary>Queries live windows without activating them or capturing their contents.</summary>
public static class WindowTopmostWindows
{
    public static WindowTopmostTarget? FindAt(int x, int y)
    {
        WindowTopmostTarget? result = null;
        User32.EnumWindows((window, _) =>
        {
            if (!IsVisibleExternal(window) || !TryBounds(window, out var bounds) || !bounds.Contains(x, y)) return 1;
            // Our overlays are ignored above, but foreign tool windows, menus and
            // shell surfaces block the point. Never select an obscured app below.
            if (IsSelectable(window)) result = Inspect(window);
            return 0;
        }, 0);
        return result;
    }

    /// <summary>Owned popups resolve to their root owner, so native ownership remains intact.</summary>
    public static WindowTopmostTarget? Inspect(nint handle)
    {
        if (!IsSelectable(handle)) return null;
        return Capture(RootOwner(handle), selectable: true);
    }

    internal static WindowTopmostTarget? Capture(nint handle, bool selectable)
    {
        if (handle == 0 || !User32.IsWindow(handle) || (selectable && !IsSelectable(handle))) return null;
        var thread = User32.GetWindowThreadProcessId(handle, out var process);
        if (thread == 0 || process == 0 || process == (uint)Environment.ProcessId || !TryProcessStarted(process, out var started)
            || !TryBounds(handle, out var bounds)) return null;
        return new(handle, process, thread, started, User32.ReadWindowText(handle), bounds, IsTopmost(handle));
    }

    internal static bool HasIdentity(WindowTopmostTarget target)
    {
        var thread = User32.GetWindowThreadProcessId(target.Handle, out var process);
        return thread == target.ThreadId && process == target.ProcessId
            && TryProcessStarted(process, out var started) && started == target.ProcessStarted;
    }

    internal static bool IsTopmost(nint handle) => (User32.GetWindowLong(handle, User32.GwlExStyle) & 8) != 0;

    internal static nint RootOwner(nint handle)
    {
        var root = WindowTopmostNative.GetAncestor(handle, 2); // GA_ROOT
        if (root == 0) return handle;
        // GA_ROOTOWNER follows GetParent, which omits the owner of an owned
        // WS_OVERLAPPED window. Explicit GW_OWNER also handles WinForms dialogs.
        for (var depth = 0; depth < 64; depth++)
        {
            var owner = User32.GetWindow(root, User32.GwOwner);
            if (owner == 0 || owner == root) break;
            root = owner;
        }
        return root;
    }

    private static bool IsSelectable(nint handle)
    {
        if (!IsVisibleExternal(handle)) return false;
        if ((User32.GetWindowLong(handle, User32.GwlExStyle) & 0x80) != 0) return false; // WS_EX_TOOLWINDOW
        return User32.ReadClassName(handle) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "#32768" or "tooltips_class32");
    }

    private static bool IsVisibleExternal(nint handle)
    {
        if (handle == 0 || !User32.IsWindowVisible(handle) || User32.IsIconic(handle) || DwmApi.IsCloaked(handle)) return false;
        User32.GetWindowThreadProcessId(handle, out var process);
        return process != 0 && process != (uint)Environment.ProcessId;
    }

    private static bool TryBounds(nint handle, out PixelRect bounds)
    {
        bounds = default;
        if (!DwmApi.TryGetExtendedFrameBounds(handle, out var rect) && !Display32.GetWindowRect(handle, out rect)) return false;
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        bounds = new(rect.Left, rect.Top, rect.Width, rect.Height);
        return true;
    }

    private static bool TryProcessStarted(uint processId, out long started)
    {
        started = 0;
        var process = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return false;
        try { return WindowTopmostNative.GetProcessTimes(process, out started, out _, out _, out _); }
        finally { Kernel32.CloseHandle(process); }
    }
}
