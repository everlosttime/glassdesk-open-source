using System.Windows.Interop;
using System.Runtime.InteropServices;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private UnifiedTransitionHost? _unifiedHost;
    private HwndSource? _unifiedOwnerSource;
    private nint _unifiedCloakHook, _unifiedReorderHook, _unifiedOwnerHandle;
    private bool _unifiedOrderQueued;
    private UnifiedWinEvent? _unifiedWinEvent;
    private nint EnsureUnifiedHost()
    {
        _unifiedHost ??= new(Handle);
        if (_unifiedOwnerSource is null && HwndSource.FromHwnd(Handle) is { } source)
        {
            _unifiedOwnerSource = source; _unifiedOwnerHandle = Handle; source.AddHook(OnUnifiedOwnerMessage);
            _unifiedWinEvent = (_, eventId, hwnd, _, _, _, _) =>
            {
                if (eventId == 0x8004)
                {
                    if (_unifiedHost is null || _unifiedOrderQueued) return;
                    _unifiedOrderQueued = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _unifiedOrderQueued = false;
                        try { UpdateUnifiedHostVisibility(reconcileOrder: true); }
                        catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); }
                    }));
                    return;
                }
                if (_unifiedHost is null || hwnd != _unifiedOwnerHandle) return;
                try { UpdateUnifiedHostVisibility(); }
                catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); }
            };
            _unifiedCloakHook = UnifiedSetWinEventHook(0x8017, 0x8018, 0, _unifiedWinEvent, (uint)Environment.ProcessId, 0, 0);
            _unifiedReorderHook = UnifiedSetWinEventHook(0x8004, 0x8004, 0, _unifiedWinEvent, 0, 0, 0);
            if (_unifiedCloakHook == 0 || _unifiedReorderHook == 0) throw new System.ComponentModel.Win32Exception();
        }
        _unifiedHost.Update(_transformHostBounds, false);
        return _unifiedHost.Handle;
    }
    private void UpdateUnifiedHostVisibility(bool reconcileOrder = false)
    {
        if (_unifiedHost is null) return;
        Marshal.ThrowExceptionForHR(UnifiedDwmGetWindowAttribute(Handle, 14, out var value, sizeof(int)));
        var cloaked = value != 0;
        var visible = WindowShadowStyles.IsOwnerVisible(Handle) && !cloaked && (_unifiedActive || _unifiedShadowOnly || _unifiedTransferPending);
        _unifiedHost.Update(DisplayService.GetWindowPixelBounds(Handle) ?? _transformHostBounds, visible, reconcileOrder);
    }
    private nint OnUnifiedOwnerMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x0047 or 0x0018 or 0x02E0)
        {
            try { UpdateUnifiedHostVisibility(reconcileOrder: true); }
            catch (Exception error) when (IsUnifiedFailure(error)) { FailUnifiedTransition(error); }
        }
        return 0;
    }
    private void DisposeUnifiedHost()
    {
        if (_unifiedOwnerSource is { IsDisposed: false }) _unifiedOwnerSource.RemoveHook(OnUnifiedOwnerMessage);
        if (_unifiedCloakHook != 0) { _ = UnifiedUnhookWinEvent(_unifiedCloakHook); _unifiedCloakHook = 0; }
        if (_unifiedReorderHook != 0) { _ = UnifiedUnhookWinEvent(_unifiedReorderHook); _unifiedReorderHook = 0; }
        _unifiedWinEvent = null; _unifiedOwnerHandle = 0;
        _unifiedOwnerSource = null; _unifiedHost?.Dispose(); _unifiedHost = null;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UnifiedWinEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", EntryPoint = "SetWinEventHook")] private static extern nint UnifiedSetWinEventHook(uint first, uint last, nint module, UnifiedWinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", EntryPoint = "UnhookWinEvent")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnifiedUnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int UnifiedDwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
}
