using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private ToggleButton? _filterPressedChip;
    private Point _filterPressPoint;
    private LauncherEditSession? _filterDragEdit;
    private bool _filterGroupDragging;
    private ToggleButton[] _filterOriginalChips = [];
    private Cursor? _cursorBeforeFilterDrag;
    private DispatcherTimer? _filterScrollTimer, _filterPressTimer;

    private void OnFilterGroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleButton chip && e.ChangedButton == MouseButton.Left)
            e.Handled = BeginFilterGroupPress(chip, e.GetPosition(FilterGroupBar));
    }

    private bool BeginFilterGroupPress(ToggleButton chip, Point point)
    {
        if (!_settings.GroupModeEnabled || _workspace is null || _logicalIconDrag || _oleIconDrag
            || _workspace.Layout.FilterGroups.Count < 2 || !FilterGroupButtons.Children.Contains(chip)) return false;
        CancelDrag();
        StopFilterTransition();
        chip.Focus();
        _filterPressedChip = chip;
        _filterPressPoint = point;
        _filterGrabPoint = FilterGroupBar.TranslatePoint(point, chip);
        _filterOriginalChips = FilterGroupButtons.Children.OfType<ToggleButton>().ToArray();
        // Hold a detached revision from press to release, just like icon sorting.
        _filterDragEdit = _workspace.BeginEdit();
        _cursorBeforeFilterDrag = Cursor;
        if (!Mouse.Capture(this, CaptureMode.SubTree))
        {
            CancelFilterGroupDrag();
            return false;
        }
        // Capture synchronizes the mouse and can raise a move before acquisition returns.
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnFilterGroupMouseMove), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnFilterGroupMouseUp), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnFilterGroupCaptureLost), true);
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnFilterGroupKeyDown), true);
        _filterPressTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(350) };
        _filterPressTimer.Tick += (_, _) =>
        {
            _filterPressTimer?.Stop();
            if (_filterPressedChip is null || Mouse.LeftButton != MouseButtonState.Pressed) return;
            StartFilterGroupDrag();
            UpdateFilterGroupDrag(Mouse.GetPosition(FilterGroupBar));
        };
        _filterPressTimer.Start();
        NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnFilterGroupMouseMove(object sender, MouseEventArgs e)
    {
        if (_filterPressedChip is null) return;
        e.Handled = true;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelFilterGroupDrag(); return; }
        UpdateFilterGroupDrag(e.GetPosition(FilterGroupBar));
    }

    private void UpdateFilterGroupDrag(Point point)
    {
        if (_filterPressedChip is null || _filterDragEdit is null) return;
        if (!_filterGroupDragging)
        {
            var distance = point - _filterPressPoint;
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            StartFilterGroupDrag();
        }
        MoveFilterGroupGhost(point);
        if (!IsInsideFilterGroupBar(point)) return;
        var center = FilterGroupBar.TranslatePoint(FilterGroupHitPoint(point), FilterGroupButtons).X;
        var targetIndex = 0;
        foreach (var chip in FilterGroupButtons.Children.OfType<ToggleButton>())
        {
            if (ReferenceEquals(chip, _filterPressedChip)) continue;
            // Compare layout slots, not animated pixels. Excluding the placeholder
            // keeps unequal-width buttons from oscillating when their neighbours move.
            var left = LayoutInformation.GetLayoutSlot(chip).X + chip.Margin.Left;
            if (center >= left + chip.ActualWidth / 2) targetIndex++;
        }
        ApplyFilterGroupPreview(targetIndex);
    }

    private void StartFilterGroupDrag()
    {
        if (_filterGroupDragging || _filterPressedChip is null) return;
        _filterPressTimer?.Stop();
        _filterGroupDragging = true;
        CreateFilterGroupDragVisuals();
        Cursor = Cursors.Hand;
        _filterScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(30) };
        _filterScrollTimer.Tick += OnFilterGroupScrollTick;
        _filterScrollTimer.Start();
    }

    private Point FilterGroupHitPoint(Point point)
        => _filterGroupDragging && _filterPressedChip is { } chip
            ? new Point(point.X - _filterGrabPoint.X + chip.ActualWidth / 2, _filterPressPoint.Y)
            : point;

    private bool IsInsideFilterGroupBar(Point point)
    {
        var hit = FilterGroupHitPoint(point);
        var halfWidth = _filterGroupDragging ? (_filterPressedChip?.ActualWidth ?? 0) / 2 : 0;
        return hit.X + halfWidth >= 0 && hit.X - halfWidth < FilterGroupBar.ActualWidth
            && hit.Y >= 0 && hit.Y < FilterGroupButtons.ActualHeight;
    }

    private void OnFilterGroupScrollTick(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) { CancelFilterGroupDrag(); return; }
        UpdateFilterGroupAutoScroll(Mouse.GetPosition(FilterGroupBar));
    }

    private void UpdateFilterGroupAutoScroll(Point point)
    {
        if (!_filterGroupDragging || !IsInsideFilterGroupBar(point)) return;
        var centerX = FilterGroupHitPoint(point).X;
        var direction = centerX < 32 ? -1 : centerX > FilterGroupBar.ActualWidth - 32 ? 1 : 0;
        if (direction == 0) return;
        FilterGroupBar.ScrollToHorizontalOffset(FilterGroupBar.HorizontalOffset + direction * 16);
        // ScrollViewer applies offsets during layout. Recalculate against the newly visible chips.
        FilterGroupBar.UpdateLayout();
        UpdateFilterGroupDrag(point);
    }

    private void OnFilterGroupMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_filterPressedChip is null || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        CompleteFilterGroupPress(e.GetPosition(FilterGroupBar));
    }

    private void CompleteFilterGroupPress(Point point)
    {
        if (_filterPressedChip is not { Tag: string id } chip || _filterDragEdit is not { } edit) return;
        var dragging = _filterGroupDragging;
        if (dragging) UpdateFilterGroupDrag(point);
        var inside = IsInsideFilterGroupBar(point);
        var overSource = new Rect(chip.RenderSize).Contains(FilterGroupBar.TranslatePoint(point, chip));
        var order = FilterGroupButtons.Children.OfType<ToggleButton>().Select(button => (string)button.Tag).ToArray();
        CancelFilterGroupDrag();
        if (!inside) return;
        if (!dragging)
        {
            if (overSource) ToggleFilterGroup(id);
            return;
        }
        var groups = edit.Draft.FilterGroups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        edit.Draft.FilterGroups = order.Select(groupId => groups[groupId]).ToList();
        if (LauncherLayoutRules.Equivalent(edit.Draft, _workspace!.Layout)) return;
        try
        {
            if (!_workspace.Commit(edit)) ShowNotice("拖动期间分组或应用库已变化，请重试排序。");
            else _notice = null;
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or ArgumentException)
        { ShowNotice("分组顺序未保存，原有顺序仍保留：" + ex.Message); }
        RenderTiles();
    }

    private void OnFilterGroupCaptureLost(object sender, MouseEventArgs e)
    {
        if (_filterPressedChip is not null && !ReferenceEquals(Mouse.Captured, this)) CancelFilterGroupDrag();
    }

    private void OnFilterGroupKeyDown(object sender, KeyEventArgs e)
    {
        if (_filterPressedChip is null || e.Key != Key.Escape) return;
        e.Handled = true;
        CancelFilterGroupDrag();
    }

    private void CancelFilterGroupDrag()
    {
        if (_filterPressedChip is not { } chip) return;
        _filterPressedChip = null;
        _filterDragEdit = null;
        _filterGroupDragging = false;
        _filterPressTimer?.Stop();
        _filterPressTimer = null;
        _filterScrollTimer?.Stop();
        _filterScrollTimer = null;
        ClearFilterGroupDragVisuals(chip);
        if (!_filterOriginalChips.SequenceEqual(FilterGroupButtons.Children.OfType<ToggleButton>()))
        {
            FilterGroupButtons.Children.Clear();
            foreach (var original in _filterOriginalChips) FilterGroupButtons.Children.Add(original);
        }
        _filterOriginalChips = [];
        RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnFilterGroupMouseMove));
        RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnFilterGroupMouseUp));
        RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnFilterGroupCaptureLost));
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnFilterGroupKeyDown));
        Cursor = _cursorBeforeFilterDrag;
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
        NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
