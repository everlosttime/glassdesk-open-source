using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Platform;

/// <summary>Owns temporary topmost changes and restores their original state on normal exit.</summary>
public sealed class WindowTopmostSession : IDisposable
{
    private sealed record SavedWindow(WindowTopmostTarget Original, nint Token)
    {
        internal bool Destroyed;
        internal bool Pending;
    }

    private readonly Lock _sync = new();
    private readonly Dictionary<nint, SavedWindow> _saved = [];
    private readonly string _propertyName = "GlassDesk.WindowTopmost." + Guid.NewGuid().ToString("N");
    private readonly Action<string>? _reportError;
    private readonly WindowEventWatcher? _events;
    private long _nextToken;
    private bool _disposed;

    public WindowTopmostSession(Action<string>? reportError = null)
    {
        _reportError = reportError;
        _events = WindowEventWatcher.TryStart(null, (eventType, window, objectId, childId) =>
        {
            if (eventType != User32.EventObjectDestroy || objectId != 0 || childId != 0) return;
            lock (_sync) { if (_saved.TryGetValue(window, out var saved)) saved.Destroyed = true; }
        });
    }

    public bool TryToggle(WindowTopmostTarget target, out bool isTopmost, out string? error)
    {
        isTopmost = false;
        error = null;
        lock (_sync)
        {
            if (_disposed) { error = "窗口置顶服务已关闭。"; return false; }
            var current = WindowTopmostWindows.Inspect(target.Handle);
            if (current is null || current.Handle != target.Handle || !SameIdentity(target, current))
            { error = "窗口已关闭或发生变化，请重新选择。"; return false; }
            if (WindowTopmostNative.IsHungAppWindow(target.Handle))
            { error = "窗口暂时没有响应，请稍后重试。"; return false; }
            if (_saved.TryGetValue(target.Handle, out var previous) && !IsCurrent(previous))
            {
                if (target.ObservedAt <= previous.Original.ObservedAt)
                { error = "窗口已关闭或发生变化，请重新选择。"; return false; }
                _saved.Remove(target.Handle);
            }
            if (!SaveGroup(current, out error)) return false;
            var root = _saved[target.Handle];
            if (!IsCurrent(root)) { error = "窗口已关闭或发生变化，请重新选择。"; return false; }
            var desired = !current.IsTopmost;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3 / 10;
            if (!Change(root, desired, deadline, out error)) return false;
            // Demoting an owner also demotes owned windows. Preserve those that were
            // independently topmost before this feature touched the ownership group.
            foreach (var saved in _saved.Values.Where(s => s.Original.Handle != target.Handle
                && WindowTopmostWindows.RootOwner(s.Original.Handle) == target.Handle))
            {
                if (IsCurrent(saved) && !Change(saved, desired || saved.Original.IsTopmost, deadline, out error)) return false;
            }
            isTopmost = desired;
            return true;
        }
    }

    public void Dispose()
    {
        List<string> failures = [];
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3 / 10;
            // Restore non-topmost owners first; then independently topmost owned
            // windows. Reversing this order would immediately demote them again.
            foreach (var saved in _saved.Values.OrderBy(s => s.Original.IsTopmost))
            {
                if (IsCurrent(saved) && !Change(saved, saved.Original.IsTopmost, deadline, out var error) && error is not null)
                    failures.Add(error);
            }
            foreach (var saved in _saved.Values)
                if (IsCurrent(saved)) WindowTopmostNative.RemoveProp(saved.Original.Handle, _propertyName);
            _saved.Clear();
        }
        _events?.Dispose();
        foreach (var failure in failures)
        {
            try { _reportError?.Invoke(failure); }
            catch { /* Cleanup must not be interrupted by a diagnostic sink. */ }
        }
    }

    private bool SaveGroup(WindowTopmostTarget root, out string? error)
    {
        error = null;
        var inheritsOurTopmost = _saved.TryGetValue(root.Handle, out var originalRoot) && IsCurrent(originalRoot)
            && !originalRoot.Original.IsTopmost && root.IsTopmost;
        List<WindowTopmostTarget> windows = [root];
        User32.EnumWindows((window, _) =>
        {
            if (window != root.Handle && WindowTopmostWindows.RootOwner(window) == root.Handle
                && WindowTopmostWindows.Capture(window, selectable: false) is { } owned)
                windows.Add(inheritsOurTopmost ? owned with { IsTopmost = false } : owned);
            return 1;
        }, 0);
        List<SavedWindow> additions = [];
        foreach (var window in windows)
        {
            if (_saved.TryGetValue(window.Handle, out var previous))
            {
                if (IsCurrent(previous)) continue;
                _saved.Remove(window.Handle);
            }
            var saved = new SavedWindow(window, (nint)(++_nextToken));
            if (!WindowTopmostWindows.HasIdentity(window) || !WindowTopmostNative.SetProp(window.Handle, _propertyName, saved.Token))
            {
                foreach (var added in additions)
                    if (IsCurrent(added)) WindowTopmostNative.RemoveProp(added.Original.Handle, _propertyName);
                error = "无法更改此窗口的置顶状态，可能受到权限限制。";
                return false;
            }
            additions.Add(saved);
        }
        foreach (var saved in additions) _saved.Add(saved.Original.Handle, saved);
        return true;
    }

    private bool IsCurrent(SavedWindow saved) => !saved.Destroyed && WindowTopmostWindows.HasIdentity(saved.Original)
        && WindowTopmostNative.GetProp(saved.Original.Handle, _propertyName) == saved.Token;

    private bool Change(SavedWindow saved, bool desired, long deadline, out string? error)
    {
        error = null;
        if (!IsCurrent(saved)) { error = "窗口已关闭或发生变化，请重新选择。"; return false; }
        if (!saved.Pending && WindowTopmostWindows.IsTopmost(saved.Original.Handle) == desired) return true;
        // ASYNCWINDOWPOS prevents a hung foreign message loop from blocking ours.
        const uint flags = 0x4000 | 0x0200 | 0x0010 | 0x0002 | 0x0001;
        if (!WindowTopmostNative.SetWindowPos(saved.Original.Handle, desired ? -1 : -2, 0, 0, 0, 0, flags))
        {
            error = Marshal.GetLastWin32Error() == 5 ? "无法更改此窗口的置顶状态，可能受到权限限制。" : "无法更改此窗口的置顶状态。";
            return false;
        }
        // A timed-out asynchronous raise can still be waiting in the target's
        // queue. Cleanup must enqueue the inverse even if its current flag
        // already looks restored, or that late raise would outlive the session.
        saved.Pending = true;
        do
        {
            if (!IsCurrent(saved)) { error = "窗口已关闭或发生变化，请重新选择。"; return false; }
            if (WindowTopmostWindows.IsTopmost(saved.Original.Handle) == desired) { saved.Pending = false; return true; }
            Thread.Sleep(5);
        } while (Stopwatch.GetTimestamp() < deadline);
        error = "窗口暂时没有响应，请稍后重试。";
        return false;
    }

    private static bool SameIdentity(WindowTopmostTarget first, WindowTopmostTarget second) => first.ProcessId == second.ProcessId
        && first.ThreadId == second.ThreadId && first.ProcessStarted == second.ProcessStarted;
}
