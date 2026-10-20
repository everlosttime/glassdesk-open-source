using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private LauncherLayout? _dragOrigin;
    private string? _dragIconId;
    private Button? _dragGhost;
    private bool _suppressTileClick;
    private int _previewIndex = -1;
    private bool _previewExchange;
    private LauncherLayout? _layoutBeforePlacement;
    private int _placementCandidateIndex = -1;
    private long _placementCandidateStarted;
    private DateTimeOffset _pagePreviewAfter;
    private readonly Dictionary<string, Button> _dragTiles = new(StringComparer.Ordinal);
    private (Point Point, bool Exchange)? _pendingIconPreview;
    private bool _iconPreviewQueued;
    private bool _trackDragPointer;
    private long _lastOlePointerSampleTicks;
    private TimeSpan? _lastIconRenderingTime;
    private TranslateTransform? _dragGhostMotion;
    private long _dragPreviewCommits;
    private double _lastDragPreviewMilliseconds;
    private readonly Dictionary<Button, Point> _dragTileOffsets = new();
    private readonly Dictionary<Button, IconOffsetMotion> _iconOffsetMotions = new();

    private sealed class IconOffsetMotion(TranslateTransform transform)
    {
        public readonly TranslateTransform Transform = transform;
        public Point Start, Target;
        public long Started;
        public bool Active;
    }

    private void PrepareIconDrag(Button source, Point grabPoint)
    {
        if (_workspace is null || source.Tag is not string id) return;
        ClearFolderHover(immediate: true);
        _dropLandingId = null;
        _dragSourceGroupId = GroupItems.Children.Contains(source) ? _view.OpenGroupId : null;
        _memberOrigin = null;
        _memberPreviewIndex = -1;
        _layoutBeforePlacement = null;
        _placementCandidateIndex = -1;
        _memberHasLeft = false;
        var sourceSlot = CurrentSlots().FirstOrDefault(s => s.ItemId == id || s.Group?.Id == id) ?? new LauncherSlot { ItemId = id };
        _ghostIconCenterOffset = source is LauncherAppTileButton appTile
            ? appTile.IconHost.TranslatePoint(new Point(appTile.IconHost.ActualWidth / 2, appTile.IconHost.ActualHeight / 2), source)
            : new Point(source.ActualWidth / 2, AppIconSize / 2);
        StopPageSlide();
        StopIconDragFrames();
        _dragPreviewCommits = 0;
        ClearDragGhost();
        _pressedId = null;
        _pressedTile = null;
        _dropCommitted = false;
        _suppressTileClick = true;
        _dragEdit = _workspace.BeginEdit();
        _dragOrigin = LauncherLayoutRules.DeepClone(_dragEdit.Draft);
        if (_dragSourceGroupId is not null)
        {
            _memberOrigin = LauncherLayoutRules.DeepClone(_dragOrigin);
            var groupIndex = _dragOrigin.Pages.SelectMany(p => p.Slots).ToList().FindIndex(s => s.Group?.Id == _dragSourceGroupId);
            _dragOrigin = LauncherLayoutEditor.RemoveFromGroup(_dragOrigin, id, _dragSourceGroupId, 0, groupIndex + 1, _view.Capacity);
        }
        _retainedTilePages.Clear();
        _dragIconId = id;
        _grabPoint = grabPoint;
        _previewIndex = _view.Page * _view.Capacity + TileGrid.Children.IndexOf(source);
        _previewExchange = false;
        _pagePreviewAfter = DateTimeOffset.MinValue;
        _edgePager.Reset();
        _dragTiles.Clear();
        _dragTileOffsets.Clear();
        _iconOffsetMotions.Clear();
        foreach (var button in TileGrid.Children.OfType<Button>())
            if (button.Tag is string key) _dragTiles[key] = button;
        _dragGhost = CreateTile(sourceSlot);
        _dragGhost.Width = source.ActualWidth;
        _dragGhost.Height = source.ActualHeight;
        _dragGhost.Margin = new Thickness(0);
        _dragGhost.IsHitTestVisible = false;
        _dragGhost.ToolTip = null;
        _dragGhostMotion = new TranslateTransform();
        var ghostTransforms = new TransformGroup();
        ghostTransforms.Children.Add(new ScaleTransform(1.06, 1.06));
        ghostTransforms.Children.Add(_dragGhostMotion);
        _dragGhost.RenderTransform = ghostTransforms;
        _dragGhost.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 12, ShadowDepth = 4, Opacity = .22 };
        DragOverlay.Children.Add(_dragGhost);
        var position = source.TranslatePoint(new Point(0, 0), DragOverlay);
        Canvas.SetLeft(_dragGhost, position.X);
        Canvas.SetTop(_dragGhost, position.Y);
        source.Opacity = 0;
        source.IsHitTestVisible = false;
        TrashDropTarget.Visibility = Visibility.Visible;
        EnsureIconDragFrames();
    }

    private void UpdateDragPointer()
    {
        if (_dragOrigin is null || DisplayService.GetCursorPixelPoint() is not { } cursor) return;
        var screen = new Point(cursor.X, cursor.Y);
        var overlay = DragOverlay.PointFromScreen(screen);
        if (_dragGhost is { } ghost && _dragGhostMotion is { } motion)
        {
            motion.X = overlay.X - _grabPoint.X - Canvas.GetLeft(ghost);
            motion.Y = overlay.Y - _grabPoint.Y - Canvas.GetTop(ghost);
        }
        _lastDragPoint = TileGrid.PointFromScreen(screen);
        _lastDragPoint.X += IncomingPageMotion.X;
        var viewport = PageViewport.PointFromScreen(screen);
        var trash = TrashDropTarget.PointFromScreen(screen);
        if (viewport.X < 0 || viewport.Y < 0 || viewport.X >= PageViewport.ActualWidth || viewport.Y >= PageViewport.ActualHeight
            || (trash.X >= 0 && trash.Y >= 0 && trash.X < TrashDropTarget.ActualWidth && trash.Y < TrashDropTarget.ActualHeight))
        { ClearFolderHover(); _placementCandidateIndex = -1; return; }
        QueueIconPreview(_lastDragPoint, (Keyboard.Modifiers & ModifierKeys.Alt) != 0);
    }

    private void UpdatePointerFromOleFeedback()
    {
        if (!_trackDragPointer || _dragOrigin is null) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastOlePointerSampleTicks != 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(_lastOlePointerSampleTicks, now).TotalMilliseconds < 8) return;
        _lastOlePointerSampleTicks = now;
        // Continuous native input can keep WPF's Input-priority render request
        // waiting. A real ghost-transform update requests Render priority through
        // the public visual pipeline, breaking the wait for Rendering to update
        // that same transform. Reordering still runs once per RenderingTime.
        UpdateDragPointer();
    }

    private void QueueIconPreview(Point point, bool exchange)
    {
        if (_dragOrigin is null) return;
        _pendingIconPreview = (point, exchange);
        EnsureIconDragFrames();
    }

    private void EnsureIconDragFrames()
    {
        if (_iconPreviewQueued) return;
        _iconPreviewQueued = true;
        CompositionTarget.Rendering += CommitQueuedIconPreview;
    }

    private void CommitQueuedIconPreview(object? sender, EventArgs e)
    {
        if (_dragOrigin is null) return;
        // WPF can invoke Rendering again after layout in the same frame. Keep
        // one input/preview submission per rendering timestamp, preserving any
        // newer queued position for the following timestamp.
        if (e is RenderingEventArgs frame)
        {
            if (_lastIconRenderingTime == frame.RenderingTime) return;
            _lastIconRenderingTime = frame.RenderingTime;
        }
        AdvanceIconMotions();
        if (_trackDragPointer)
        {
            UpdateDragPointer();
            UpdateDragEdge();
            QueueIconOleTransfer();
        }
        var pending = _pendingIconPreview;
        CancelQueuedIconPreview();
        if (pending is not { } preview || _dragOrigin is null) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        PreviewIconPlacement(preview.Point, preview.Exchange);
        _lastDragPreviewMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _dragPreviewCommits++;
    }

    private void CancelQueuedIconPreview()
    {
        _pendingIconPreview = null;
    }

    private void StopIconDragFrames()
    {
        if (_iconPreviewQueued) CompositionTarget.Rendering -= CommitQueuedIconPreview;
        _iconPreviewQueued = false;
        _trackDragPointer = false;
        _lastOlePointerSampleTicks = 0;
        _lastIconRenderingTime = null;
        _pendingIconPreview = null;
    }

    private void PreviewIconPlacement(Point point, bool exchange)
        => PlaceIconAtPointer(point, exchange, false);

    private void PlaceIconAtPointer(Point point, bool exchange, bool snap)
    {
        if (snap) { CancelQueuedIconPreview(); _placementCandidateIndex = -1; }
        if (_dragOrigin is null || _dragEdit is null || _dragIconId is null || TileGrid.ActualWidth <= 0 || TileGrid.ActualHeight <= 0
            || (!snap && DateTimeOffset.UtcNow < _pagePreviewAfter)) return;
        if (point.X < 0 || point.Y < 0 || point.X >= TileGrid.ActualWidth || point.Y >= TileGrid.ActualHeight)
        { _placementCandidateIndex = -1; return; }
        if (PreviewFolderMemberInside(point)) return;
        if (!snap)
        {
            UpdateFolderHover(point, exchange);
            // Center overlap reserves this icon for the full folder dwell.
            // Sorting it away first makes the user chase a moving target.
            if (_folderIntent.TargetId is not null) { _placementCandidateIndex = -1; return; }
        }
        var cellWidth = TileGrid.ActualWidth / _view.Columns;
        var cellHeight = TileGrid.ActualHeight / _view.Rows;
        var column = Math.Clamp((int)(point.X / cellWidth), 0, _view.Columns - 1);
        var row = Math.Clamp((int)(point.Y / cellHeight), 0, _view.Rows - 1);
        var count = _dragOrigin.Pages.Sum(p => p.Slots.Count);
        var index = Math.Min(_view.Page * _view.Capacity + row * _view.Columns + column, count - 1);
        if (index == _previewIndex && exchange == _previewExchange) { _placementCandidateIndex = -1; return; }
        // Crossing into a new cell needs a few pixels of intent, so boundary jitter cannot shuttle icons back and forth.
        if (!snap && _previewIndex >= 0 && index != _previewIndex)
        {
            var x = point.X - column * cellWidth;
            var y = point.Y - row * cellHeight;
            if (x < 8 || x > cellWidth - 8 || y < 8 || y > cellHeight - 8)
            { _placementCandidateIndex = -1; return; }
        }
        if (!snap && !exchange)
        {
            // Briefly entering a new cell should not reorder the grid before
            // the pointer can reach its icon. Alt and the final drop stay direct.
            if (_placementCandidateIndex != index)
            {
                _placementCandidateIndex = index;
                _placementCandidateStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                return;
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_placementCandidateStarted).TotalMilliseconds < 160) return;
        }
        _placementCandidateIndex = -1;
        _previewIndex = index;
        _previewExchange = exchange;
        _layoutBeforePlacement = LauncherLayoutRules.DeepClone(_dragEdit.Draft);
        _dragEdit.Draft = LauncherLayoutEditor.PlaceAtSlot(_dragOrigin, _dragIconId, index, _view.Capacity, exchange);
        if (snap) StopPageSlide();
        RenderIconPreview();
    }

    private void NavigateDragPage(int direction)
    {
        if (_dragOrigin is null) return;
        CancelQueuedIconPreview();
        ClearFolderHover();
        var previous = _view.Page;
        _view.MoveToPage(previous + direction);
        if (_view.Page == previous) return;
        _previewIndex = -1;
        _placementCandidateIndex = -1;
        // Finish the slide before evaluating the new grid. The floating icon stays under the mouse throughout.
        _pagePreviewAfter = DateTimeOffset.UtcNow.AddMilliseconds(250);
        SlideToDragPage(previous, direction);
    }

    private void SlideToDragPage(int previousPage, int direction)
    {
        _view.MoveToPage(previousPage);
        // Materialize the preview once at the page boundary so outgoing tiles
        // retain their slot positions when they move into the slide layer.
        RenderIconPreview(materialize: true);
        SlideToPage(direction);
        foreach (var button in TileGrid.Children.OfType<Button>())
        {
            if (button.Tag is not string id) continue;
            _dragTiles[id] = button;
            if (id == _dragIconId) { button.Opacity = 0; button.IsHitTestVisible = false; }
        }
    }

    private void RenderIconPreview(bool materialize = false)
    {
        // A delayed WPF clock can still own the dragged tile on the outgoing page,
        // even after the wall-clock preview delay has elapsed. Retire that page first.
        if (OutgoingPageGrid.Children.Count > 0) StopPageSlide();
        var indices = TileGrid.Children.OfType<Button>().Select((tile, index) => (tile, index))
            .ToDictionary(pair => pair.tile, pair => pair.index);
        var slots = CurrentSlots();
        _view.SetItemCount(slots.Count);
        var desired = new List<Button>();
        var moving = new List<Button>();
        foreach (var slot in slots.Skip(_view.Page * _view.Capacity).Take(_view.Capacity))
        {
            var id = slot.Kind == LauncherSlotKind.Group ? slot.Group!.Id : slot.ItemId!;
            if (!_dragTiles.TryGetValue(id, out var tile))
                _dragTiles[id] = tile = CreateTile(slot);
            tile.Opacity = id == _dragIconId ? 0 : 1;
            tile.IsHitTestVisible = id != _dragIconId;
            desired.Add(tile);
        }
        // The grid remains measured and arranged at its original anchors during
        // same-page pickup. Only render offsets change, even if every icon shifts.
        if (!materialize && _dragOrigin is not null && desired.Count == indices.Count
            && desired.All(indices.ContainsKey))
        {
            var cellWidth = TileGrid.ActualWidth / _view.Columns;
            var cellHeight = TileGrid.ActualHeight / _view.Rows;
            for (var i = 0; i < desired.Count; i++)
            {
                var tile = desired[i];
                var anchor = indices[tile];
                MoveIconToOffset(tile, (i % _view.Columns - anchor % _view.Columns) * cellWidth,
                    (i / _view.Columns - anchor / _view.Columns) * cellHeight);
            }
            _lastTilesRenderKey = null;
            return;
        }
        var positions = TileGrid.Children.OfType<Button>().Where(b => b.Tag is string)
            .ToDictionary(b => (string)b.Tag, b => b.TranslatePoint(new Point(0, 0), TileGrid), StringComparer.Ordinal);
        _dragTileOffsets.Clear();
        _iconOffsetMotions.Clear();
        var visible = desired.ToHashSet();
        for (var i = TileGrid.Children.Count - 1; i >= 0; i--)
            if (!visible.Contains((Button)TileGrid.Children[i])) TileGrid.Children.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var tile = desired[i];
            if (i >= TileGrid.Children.Count || !ReferenceEquals(TileGrid.Children[i], tile))
            {
                TileGrid.Children.Remove(tile);
                TileGrid.Children.Insert(i, tile);
            }
            if (!indices.TryGetValue(tile, out var previousIndex) || previousIndex != i
                || tile.RenderTransform is TranslateTransform)
            {
                tile.RenderTransform = new TranslateTransform();
                moving.Add(tile);
            }
        }
        TileGrid.UpdateLayout();
        foreach (var tile in moving)
        {
            if (tile.Tag is not string id || !positions.TryGetValue(id, out var old)) continue;
            var next = tile.TranslatePoint(new Point(0, 0), TileGrid);
            var motion = (TranslateTransform)tile.RenderTransform;
            AnimateIconOffset(motion, old.X - next.X, old.Y - next.Y);
        }
        PageText.Text = $"{_view.Page + 1} / {_view.PageCount}";
        PreviousPage.IsEnabled = _view.Page > 0;
        NextPage.IsEnabled = _view.Page < _view.PageCount - 1;
        _lastTilesRenderKey = null;
    }

    private void MoveIconToOffset(Button tile, double x, double y)
    {
        if (tile.RenderTransform is not TranslateTransform motion)
        {
            if (Math.Abs(x) < .1 && Math.Abs(y) < .1) return;
            tile.RenderTransform = motion = new TranslateTransform();
        }
        if (_dragTileOffsets.TryGetValue(tile, out var target)
            && Math.Abs(target.X - x) < .1 && Math.Abs(target.Y - y) < .1) return;
        _dragTileOffsets[tile] = new Point(x, y);
        // Reuse one state per tile. Whole-page reversals must not tear down and
        // recreate two WPF animation clocks for every displaced icon.
        var oldX = motion.X;
        var oldY = motion.Y;
        if (!_iconOffsetMotions.TryGetValue(tile, out var state) || !ReferenceEquals(state.Transform, motion))
        {
            motion.BeginAnimation(TranslateTransform.XProperty, null);
            motion.BeginAnimation(TranslateTransform.YProperty, null);
            _iconOffsetMotions[tile] = state = new IconOffsetMotion(motion);
        }
        motion.X = oldX;
        motion.Y = oldY;
        if (tile.Opacity == 0 || ThemeService.Current?.AnimationsEnabled == false)
        {
            state.Active = false;
            motion.X = x;
            motion.Y = y;
            return;
        }
        state.Start = new Point(oldX, oldY);
        state.Target = new Point(x, y);
        state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        state.Active = true;
        EnsureIconDragFrames();
    }

    private void AdvanceIconMotions()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var state in _iconOffsetMotions.Values)
        {
            if (!state.Active) continue;
            var progress = Math.Min(1, System.Diagnostics.Stopwatch.GetElapsedTime(state.Started, now).TotalMilliseconds / 210);
            if (ThemeService.Current?.AnimationsEnabled == false) progress = 1;
            var remaining = 1 - progress;
            var eased = 1 - remaining * remaining * remaining;
            state.Transform.X = state.Start.X + (state.Target.X - state.Start.X) * eased;
            state.Transform.Y = state.Start.Y + (state.Target.Y - state.Start.Y) * eased;
            if (progress == 1) state.Active = false;
        }
    }

    private static void AnimateIconOffset(TranslateTransform motion, double x, double y)
    {
        if (ThemeService.Current?.AnimationsEnabled == false) return;
        var duration = TimeSpan.FromMilliseconds(210);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (Math.Abs(x) > .1) motion.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        if (Math.Abs(y) > .1) motion.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void FinishIconDrag()
    {
        StopIconDragFrames();
        var hadPreview = _dragOrigin is not null;
        var ghost = _dragGhost;
        var id = _dragIconId;
        var memberSource = _dragSourceGroupId;
        var merged = _dropLandingId is not null;
        var folderEdit = merged || memberSource is not null;
        var landingId = _dropLandingId ?? id;
        ClearFolderHover();
        _pressTimer?.Stop();
        _pressTimer = null;
        _edgePager.Reset();
        ClearDropTarget();
        _dragEdit = null;
        _dragOrigin = null;
        _dragIconId = null;
        _pressedId = null;
        _pressedTile = null;
        _dragSourceGroupId = null;
        _memberOrigin = null;
        _dropLandingId = null;
        TrashDropTarget.Visibility = Visibility.Collapsed;
        StopPageSlide();
        if (!_dropCommitted && memberSource is not null) { _view.OpenGroupId = memberSource; _folderClosing = false; }
        if (folderEdit) { _folderItemsKey = null; RenderTiles(); }
        else if (hadPreview) RenderIconPreview();
        else if (_dropCommitted) RenderTiles();
        // Folder edits rebuild the buttons. Their layout offsets are still zero
        // until arranged; reading the destination earlier flies the ghost to the
        // grid origin before the real icon appears at its actual saved slot.
        if (folderEdit) TileGrid.UpdateLayout();
        _dragTiles.Clear();
        _dragTileOffsets.Clear();
        _iconOffsetMotions.Clear();
        var landing = TileGrid.Children.OfType<Button>().FirstOrDefault(b => Equals(b.Tag, landingId));
        if (_dropCommitted && ghost is not null && landing is not null && IsVisible && ThemeService.Current?.AnimationsEnabled != false)
        {
            landing.Opacity = merged ? 1 : 0;
            if (merged && landing is LauncherAppTileButton folderTile) folderTile.AnimateFolderArrival();
            var destination = landing.TranslatePoint(new Point(0, 0), DragOverlay);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(180);
            if (merged) ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
            var motion = _dragGhostMotion!;
            var x = new DoubleAnimation(motion.X, destination.X - Canvas.GetLeft(ghost), duration) { EasingFunction = ease };
            var y = new DoubleAnimation(motion.Y, destination.Y - Canvas.GetTop(ghost), duration) { EasingFunction = ease };
            y.Completed += (_, _) => { landing.Opacity = 1; if (ReferenceEquals(_dragGhost, ghost)) ClearDragGhost(); };
            motion.BeginAnimation(TranslateTransform.XProperty, x);
            motion.BeginAnimation(TranslateTransform.YProperty, y);
            if (ghost.RenderTransform is TransformGroup transforms && transforms.Children.OfType<ScaleTransform>().FirstOrDefault() is { } scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(merged ? .3 : 1, duration) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(merged ? .3 : 1, duration) { EasingFunction = ease });
            }
        }
        else ClearDragGhost();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => _suppressTileClick = false));
    }

    private void ClearDragGhost()
    {
        ClearFolderHover(immediate: true);
        DragOverlay.Children.Clear();
        _dragGhost = null;
        _dragGhostMotion = null;
        foreach (var tile in TileGrid.Children.OfType<Button>()) { tile.Opacity = 1; tile.IsHitTestVisible = true; }
    }
}
