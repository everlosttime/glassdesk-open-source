using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>Foreground notifications for desktop layer reconciliation, on the creating message-loop thread.</summary>
public sealed class DesktopLayerWatcher : IDisposable
{
    private readonly User32.WinEventProc _callback;
    private nint _hook;

    /// <param name="queueUpdate">Must only queue work, never change windows inside the native callback.</param>
    public DesktopLayerWatcher(Action queueUpdate)
    {
        ArgumentNullException.ThrowIfNull(queueUpdate);
        _callback = (_, _, _, _, _, _, _) => { if (_hook != 0) queueUpdate(); };
        _hook = User32.SetWinEventHook(User32.EventSystemForeground, User32.EventSystemForeground,
            0, _callback, 0, 0, User32.WineventOutOfContext);
    }

    /// <summary>Must be disposed on the creating thread.</summary>
    public void Dispose()
    {
        if (_hook == 0) return;
        _ = User32.UnhookWinEvent(_hook);
        _hook = 0;
        GC.KeepAlive(_callback);
    }
}
