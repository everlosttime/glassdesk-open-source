using System.ComponentModel;
using System.Runtime.InteropServices;
using GlassDesk.Platform;
using GlassDesk.UI;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private WindowTopmostPicker? _windowTopmostPicker;
    private WindowTopmostSession? _windowTopmostSession;
    private volatile bool _windowTopmostSessionSuspended;

    private bool CanSelectWindowTopmost => !_disposed && !_shuttingDown
        && !_windowTopmostSessionSuspended && Volatile.Read(ref _displayRefreshPending) == 0
        && _performance?.AllowsWindowSelection != false;

    private void ToggleWindowTopmostSelection()
    {
        if (_windowTopmostPicker?.IsSelecting == true)
        {
            _windowTopmostPicker.Cancel();
            return;
        }
        if (!CanSelectWindowTopmost) return;
        try
        {
            if (_launcher?.IsVisible == true) HideLauncher();
            _windowTopmostPicker ??= new WindowTopmostPicker();
            _windowTopmostPicker.Begin(OnWindowTopmostSelected);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ExternalException)
        {
            _windowTopmostPicker?.Cancel();
            ReportInteractionError("无法开始选择窗口：" + error.Message);
        }
    }

    private void OnWindowTopmostSelected(WindowTopmostTarget target)
    {
        if (!CanSelectWindowTopmost) return;
        try
        {
            _windowTopmostSession ??= new WindowTopmostSession(ReportInteractionError);
            if (_windowTopmostSession.TryToggle(target, out var topmost, out var error))
                _windowTopmostPicker?.ShowFeedback(topmost ? "窗口已置顶" : "已取消窗口置顶");
            else
                ReportWindowTopmostFailure(error ?? "无法更改窗口置顶状态，请重试。");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ExternalException)
        {
            ReportWindowTopmostFailure("无法更改窗口置顶状态：" + error.Message);
        }
    }

    private void ReportWindowTopmostFailure(string message)
    {
        ReportInteractionError(message);
        _windowTopmostPicker?.ShowFeedback(message, isError: true);
    }

    private void DisposeWindowTopmost()
    {
        var picker = _windowTopmostPicker;
        var session = _windowTopmostSession;
        _windowTopmostPicker = null;
        _windowTopmostSession = null;
        try { picker?.Dispose(); }
        finally { session?.Dispose(); }
    }
}
