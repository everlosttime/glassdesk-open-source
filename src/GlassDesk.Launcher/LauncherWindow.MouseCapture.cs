using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private bool _logicalIconDrag, _capturedIconDrag, _oleIconDrag, _oleTransferQueued, _resumeCaptureFromOle;
    private long _iconDragSession;
    private Cursor? _cursorBeforeIconDrag;

    private void BeginCapturedIconDrag()
    {
        _logicalIconDrag = true;
        _iconDragSession++;
        _cursorBeforeIconDrag = Cursor;
        Cursor = Cursors.Hand;
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnCapturedIconMouseUp), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnIconMouseCaptureLost), true);
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnCapturedIconKeyDown), true);
        try
        {
            InternalDragStarted?.Invoke(this, EventArgs.Empty);
            if (_logicalIconDrag && !CaptureIconMouse()) RunIconOleTransfer(returnToCapture: false);
        }
        catch { EndCapturedIconDrag(); throw; }
    }

    private bool CaptureIconMouse()
    {
        // The window survives page/tile rebuilding; capture cannot belong to a replaced tile.
        _capturedIconDrag = Mouse.Capture(this, CaptureMode.SubTree);
        return _capturedIconDrag;
    }

    private void ReleaseIconMouseCapture()
    {
        _capturedIconDrag = false;
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
    }

    private void EndCapturedIconDrag()
    {
        if (!_logicalIconDrag) return;
        _logicalIconDrag = false;
        _oleTransferQueued = false;
        RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnCapturedIconMouseUp));
        RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnIconMouseCaptureLost));
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnCapturedIconKeyDown));
        ReleaseIconMouseCapture();
        try { FinishIconDrag(); }
        finally
        {
            Cursor = _cursorBeforeIconDrag;
            Mouse.SetCursor(null);
            InternalDragFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCapturedIconMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_capturedIconDrag || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        try
        {
            if (_workspace is null || _dragEdit is null || _dragIconId is null || !PointerIsOverLauncher()) return;
            var screen = DisplayService.GetCursorPixelPoint();
            if (screen is not { } cursor) return;
            var point = new Point(cursor.X, cursor.Y);
            var trash = TrashDropTarget.PointFromScreen(point);
            if (trash.X >= 0 && trash.Y >= 0 && trash.X < TrashDropTarget.ActualWidth && trash.Y < TrashDropTarget.ActualHeight)
                _dragEdit.Draft = LauncherLayoutEditor.RemoveShortcut(_dragEdit.Draft, _dragIconId, _view.Capacity);
            else
            {
                var local = TileGrid.PointFromScreen(point);
                local.X += IncomingPageMotion.X;
                var exchange = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                if (!ApplyFolderDrop(local, exchange)) PlaceIconAtPointer(local, exchange, true);
            }
            _dropCommitted = _workspace.Commit(_dragEdit);
            if (!_dropCommitted) ShowNotice("拖拽期间布局或应用库已变化，请重试。");
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or ArgumentException)
        { ShowNotice("拖拽未保存，原布局已恢复：" + ex.Message); }
        finally { EndCapturedIconDrag(); }
    }

    private void OnCapturedIconKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturedIconDrag || e.Key != Key.Escape) return;
        e.Handled = true;
        _dropCommitted = false;
        EndCapturedIconDrag();
    }

    private void OnIconMouseCaptureLost(object sender, MouseEventArgs e)
    {
        if (!_capturedIconDrag || ReferenceEquals(Mouse.Captured, this)) return;
        _dropCommitted = false;
        EndCapturedIconDrag();
    }

    private bool PointerIsOverLauncher()
    {
        if (Handle == 0 || DisplayService.GetCursorPixelPoint() is not { } cursor) return false;
        var hit = IconDragNative.WindowFromPoint(new IconDragNative.Point { X = cursor.X, Y = cursor.Y });
        return hit != 0 && IconDragNative.GetAncestor(hit, 2 /* GA_ROOT, excludes owner */) == Handle;
    }

    private void QueueIconOleTransfer()
    {
        if (!_capturedIconDrag || _oleIconDrag || _oleTransferQueued || PointerIsOverLauncher()) return;
        _oleTransferQueued = true;
        var session = _iconDragSession;
        // Leave Rendering before entering a modal OLE segment; do not nest the render pass.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (session != _iconDragSession) return;
            _oleTransferQueued = false;
            if (!_logicalIconDrag || !_capturedIconDrag || !IsVisible || Mouse.LeftButton != MouseButtonState.Pressed || PointerIsOverLauncher()) return;
            RunIconOleTransfer();
        }));
    }

    private static class IconDragNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    }
}
