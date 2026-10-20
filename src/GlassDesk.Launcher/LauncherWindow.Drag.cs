using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private LauncherEditSession? _dragEdit;
    private string? _pressedId;
    private Point _lastDragPoint;
    private DispatcherTimer? _pressTimer;
    private Button? _pressedTile;
    private Point _grabPoint;
    private readonly LauncherEdgePager _edgePager = new(TimeSpan.FromMilliseconds(600));
    private bool _dropCommitted;
    private Button? _dropTarget;
    private Brush? _dropTargetOriginalBorderBrush;
    private Thickness _dropTargetOriginalBorderThickness;

    public event EventHandler? InternalDragStarted;
    public event EventHandler? InternalDragFinished;

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_view.IsSearching || _logicalIconDrag || _oleIconDrag || sender is not Button button) return;
        _pressedId = button.Tag as string;
        _pressedTile = button;
        _grabPoint = e.GetPosition(button);
        _pressTimer?.Stop();
        _pressTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(350) };
        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer?.Stop();
            _pressTimer = null;
            if (_pressedTile is { } tile && _pressedId is not null && Mouse.LeftButton == MouseButtonState.Pressed)
                StartIconDrag(tile);
        };
        _pressTimer.Start();
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_view.IsSearching || _pressedId is null || _workspace is null || e.LeftButton != MouseButtonState.Pressed) return;
        // Long-press owns pickup; mouse movement before the hold completes does not start an OLE drag.
    }

    private void StartIconDrag(Button tile)
    {
        var id = tile.Tag as string;
        if (id is null || _workspace is null || _view.IsSearching) return;
        PrepareIconDrag(tile, _grabPoint);
        _trackDragPointer = true;
        BeginCapturedIconDrag();
    }

    private void RunIconOleTransfer(bool returnToCapture = true)
    {
        var id = _dragIconId;
        if (id is null || !_logicalIconDrag) return;
        var session = _iconDragSession;
        _resumeCaptureFromOle = false;
        _oleIconDrag = true;
        ReleaseIconMouseCapture();
        QueryContinueDragEventHandler continueDrag = (_, e) =>
        {
            if (_dragEdit is null) { e.Action = DragAction.Cancel; e.Handled = true; }
            else if (returnToCapture && !e.EscapePressed && (e.KeyStates & DragDropKeyStates.LeftMouseButton) != 0 && PointerIsOverLauncher())
            {
                _resumeCaptureFromOle = true;
                e.Action = DragAction.Cancel;
                e.Handled = true;
            }
        };
        GiveFeedbackEventHandler feedback = (_, e) =>
        {
            UpdatePointerFromOleFeedback();
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.Hand);
            e.Handled = true;
        };
        QueryContinueDrag += continueDrag;
        GiveFeedback += feedback;
        try
        {
            // Dock accepts a Copy effect: pinning must not remove the launcher item.
            DragDrop.DoDragDrop(this, new DataObject(LauncherDragContract.SlotDataFormat, id), DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            QueryContinueDrag -= continueDrag;
            GiveFeedback -= feedback;
            _oleIconDrag = false;
            if (_logicalIconDrag && session == _iconDragSession)
            {
                if (!_resumeCaptureFromOle || !IsVisible || !CaptureIconMouse()) EndCapturedIconDrag();
            }
        }
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressTimer?.Stop();
        _pressTimer = null;
        _pressedTile = null;
        _pressedId = null;
        if (_suppressTileClick) e.Handled = true;
    }

    private void CancelDrag()
    {
        _pressTimer?.Stop();
        _pressTimer = null;
        _pressedTile = null;
        if (_logicalIconDrag)
        {
            _dropCommitted = false;
            EndCapturedIconDrag();
            return;
        }
        if (_dragOrigin is not null)
        {
            _dropCommitted = false;
            FinishIconDrag();
            return;
        }
        ClearDropTarget();
        _pressedId = null;
        _dragEdit = null;
        _edgePager.Reset();
        TrashDropTarget.Visibility = Visibility.Collapsed;
    }

    private void UpdateDragEdge()
    {
        if (_dragEdit is null) { CancelDrag(); return; }
        var width = TileGrid.ActualWidth;
        var inGrid = _lastDragPoint.X >= 0 && _lastDragPoint.X <= width && _lastDragPoint.Y >= 0 && _lastDragPoint.Y < TileGrid.ActualHeight;
        var direction = !inGrid ? 0 : _lastDragPoint.X < 42 ? -1 : _lastDragPoint.X > width - 42 ? 1 : 0;
        if (_edgePager.Tick(direction, DateTimeOffset.UtcNow) && direction != 0)
        {
            NavigateDragPage(direction);
        }
    }

    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat))
        {
            e.Effects = _view.IsSearching ? DragDropEffects.None : DragDropEffects.Move;
            _lastDragPoint = e.GetPosition(TileGrid);
            _lastDragPoint.X += IncomingPageMotion.X;
            if (_dragOrigin is not null)
            {
                QueueIconPreview(_lastDragPoint, (e.KeyStates & DragDropKeyStates.AltKey) != 0);
                e.Handled = true;
                return;
            }
            var candidate = FindButton(e.OriginalSource as DependencyObject);
            var sourceId = e.Data.GetData(LauncherDragContract.SlotDataFormat) as string;
            if (candidate is not null && candidate.Tag is string targetId
                && targetId != sourceId && !_view.IsSearching)
            {
                var p = e.GetPosition(candidate);
                var placeAfter = p.X >= candidate.ActualWidth / 2;
                SetDropTarget(candidate, new Thickness(placeAfter ? 0 : 3, 0, placeAfter ? 3 : 0, 0));
            }
            else ClearDropTarget();
        }
        else
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLauncherDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
            _edgePager.Reset();
    }

    private void OnExternalDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                ImportPaths(paths);
            e.Handled = true;
            return;
        }
        if (_dragEdit is null || _workspace is null || _view.IsSearching || e.Data.GetData(LauncherDragContract.SlotDataFormat) is not string source)
            return;
        try
        {
            if (_dragOrigin is not null)
            {
                var point = e.GetPosition(TileGrid);
                point.X += IncomingPageMotion.X;
                var exchange = (e.KeyStates & DragDropKeyStates.AltKey) != 0;
                if (!ApplyFolderDrop(point, exchange)) PlaceIconAtPointer(point, exchange, true);
                _dropCommitted = _workspace.Commit(_dragEdit);
                if (!_dropCommitted) ShowNotice("拖拽期间布局或应用库已变化，请重试。");
                e.Effects = _dropCommitted ? DragDropEffects.Move : DragDropEffects.None;
                return;
            }
            var destination = FindButton(e.OriginalSource as DependencyObject);
            var target = destination?.Tag as string;
            if (destination is null || target is null || target == source) return;
            var slots = CurrentSlots();
            var targetSlot = slots.FirstOrDefault(slot => slot.ItemId == target || slot.Group?.Id == target);
            if (targetSlot is null) return;
            if (!slots.Any(slot => slot.Kind == LauncherSlotKind.App && slot.ItemId == source)) return;
            var visibleSlots = slots.Skip(_view.Page * _view.Capacity).Take(_view.Capacity).ToList();
            var targetIndex = visibleSlots.IndexOf(targetSlot);
            if (targetIndex < 0) return;
            var placeAfter = e.GetPosition(destination).X >= destination.ActualWidth / 2;
            var index = targetIndex + (placeAfter ? 1 : 0);
            _dragEdit.Draft = LauncherLayoutEditor.Move(_dragEdit.Draft, source, _view.Page, index, _view.Capacity);
            _dropCommitted = _workspace.Commit(_dragEdit);
            if (!_dropCommitted) ShowNotice("拖拽期间布局或应用库已变化，请重试。");
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or ArgumentException)
        {
            ShowNotice("拖拽未保存，原布局已恢复：" + ex.Message);
        }
        finally { e.Handled = true; }
    }

    private void OnTrashDragOver(object sender, DragEventArgs e)
    {
        var valid = _dragEdit is not null && !_view.IsSearching && _workspace is not null
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string source
            && ContainsLayoutItem(source);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTrashDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragEdit is null || _workspace is null || _view.IsSearching
            || e.Data.GetData(LauncherDragContract.SlotDataFormat) is not string source
            || !ContainsLayoutItem(source))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        try
        {
            _dragEdit.Draft = LauncherLayoutEditor.RemoveShortcut(_dragEdit.Draft, source, _view.Capacity);
            _dropCommitted = _workspace.Commit(_dragEdit);
            if (!_dropCommitted) ShowNotice("拖拽期间布局或应用库已变化，请重试。");
            else ShowNotice("已从启动台移除，需要时可重新添加。", autoDismiss: true);
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or ArgumentException)
        {
            ShowNotice("未能从启动台移除，原布局已恢复：" + ex.Message);
        }
    }

    private bool ContainsLayoutItem(string id)
        => _workspace?.Layout.Pages.SelectMany(page => page.Slots).Any(slot =>
            slot.ItemId == id || slot.Group?.Id == id || slot.Group?.ItemIds.Contains(id, StringComparer.Ordinal) == true) == true;

    private void SetDropTarget(Button target, Thickness indicatorThickness)
    {
        if (!ReferenceEquals(_dropTarget, target))
        {
            ClearDropTarget();
            _dropTarget = target;
            _dropTargetOriginalBorderBrush = target.BorderBrush;
            _dropTargetOriginalBorderThickness = target.BorderThickness;
        }

        target.BorderBrush = Brushes.DodgerBlue;
        target.BorderThickness = indicatorThickness;
    }

    private void ClearDropTarget()
    {
        if (_dropTarget is null) return;
        _dropTarget.BorderBrush = _dropTargetOriginalBorderBrush ?? Brushes.Transparent;
        _dropTarget.BorderThickness = _dropTargetOriginalBorderThickness;
        _dropTarget = null;
        _dropTargetOriginalBorderBrush = null;
    }

    private static Button? FindButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button button) return button;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
