using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.Core.Models;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private readonly FolderHoverIntent _folderIntent = new();
    private readonly FolderSpring _fusionSpring = new();
    private LauncherAppTileButton? _folderHoverTile;
    private Rect _folderHoverBounds;
    private SharedGlassLens? _fusionLens;
    private Point _fusionA;
    private Point _ghostIconCenterOffset;
    private string? _dropLandingId, _dragSourceGroupId;
    private LauncherLayout? _memberOrigin;
    private int _memberPreviewIndex = -1;
    private bool _memberHasLeft;

    private bool UpdateFolderHover(Point gridPoint, bool exchange)
    {
        if (exchange || _dragOrigin is null || _dragIconId is null
            || _dragOrigin.Pages.SelectMany(p => p.Slots).Any(s => s.Group?.Id == _dragIconId))
        { ClearFolderHover(); return false; }
        var pointer = TileGrid.TranslatePoint(new Point(gridPoint.X - IncomingPageMotion.X, gridPoint.Y), DragOverlay);
        var center = new Point(pointer.X - _grabPoint.X + _ghostIconCenterOffset.X,
            pointer.Y - _grabPoint.Y + _ghostIconCenterOffset.Y);
        LauncherAppTileButton? candidate = null;
        Rect bounds = default;
        double distance = double.MaxValue;
        // Retain only the logical hover identity through sorting. This never
        // changes the target's visual position or the floating icon's position.
        if (_folderHoverTile is not null && _folderHoverTile.IsVisible
            && (center - new Point(_folderHoverBounds.X + _folderHoverBounds.Width / 2, _folderHoverBounds.Y + _folderHoverBounds.Height / 2)).Length <= AppIconSize * .94)
        { candidate = _folderHoverTile; bounds = _folderHoverBounds; }
        else
        {
            foreach (var tile in TileGrid.Children.OfType<LauncherAppTileButton>())
            {
                if (Equals(tile.Tag, _dragIconId) || tile.Opacity < .1) continue;
                var box = IconBounds(tile, DragOverlay);
                var d = (center - new Point(box.X + box.Width / 2, box.Y + box.Height / 2)).Length;
                if (d < distance && d <= AppIconSize * .94) { candidate = tile; bounds = box; distance = d; }
            }
        }
        if (candidate?.Tag is not string targetId) { ClearFolderHover(); return false; }
        if (!ReferenceEquals(_folderHoverTile, candidate))
        {
            _folderIntent.Reset();
            if (_layoutBeforePlacement is not null && _dragEdit is not null)
            {
                var prior = _layoutBeforePlacement.Pages.SelectMany(p => p.Slots).ToList();
                var current = _dragEdit.Draft.Pages.SelectMany(p => p.Slots).ToList();
                int Index(List<LauncherSlot> slots) => slots.FindIndex(s => s.ItemId == targetId || s.Group?.Id == targetId);
                var column = Math.Clamp((int)(gridPoint.X / (TileGrid.ActualWidth / _view.Columns)), 0, _view.Columns - 1);
                var row = Math.Clamp((int)(gridPoint.Y / (TileGrid.ActualHeight / _view.Rows)), 0, _view.Rows - 1);
                var pointerSlot = _view.Page * _view.Capacity + row * _view.Columns + column;
                // Preserve the hover hit area just sorted out of this slot,
                // without freezing or relocating its icon. A later approach
                // starts from the icon's current visible position.
                if (Index(prior) == _previewIndex && pointerSlot == _previewIndex && Index(prior) != Index(current)
                    && _iconOffsetMotions.TryGetValue(candidate, out var motion) && motion.Active)
                {
                    var anchor = TileGrid.Children.IndexOf(candidate);
                    var old = Index(prior) - _view.Page * _view.Capacity;
                    bounds.Offset((old % _view.Columns - anchor % _view.Columns) * TileGrid.ActualWidth / _view.Columns - motion.Transform.X,
                        (old / _view.Columns - anchor / _view.Columns) * TileGrid.ActualHeight / _view.Rows - motion.Transform.Y);
                }
            }
            _folderHoverTile = candidate;
            _folderHoverBounds = bounds;
        }
        var targetCenter = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        distance = (center - targetCenter).Length;
        var overlapping = distance <= AppIconSize * .58;
        _folderIntent.Update(targetId, overlapping, Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()));
        _fusionA = targetCenter;
        _fusionSpring.Target = _folderIntent.IsReady ? 1 : 0;
        if (ThemeService.Current?.AnimationsEnabled == false) _fusionSpring.Snap(_fusionSpring.Target);
        EnsureFolderFrames();
        return true;
    }

    private bool ApplyFolderDrop(Point point, bool exchange)
    {
        if (_dragSourceGroupId is not null && !_memberHasLeft) return false;
        if (!UpdateFolderHover(point, exchange) || !_folderIntent.IsReady || _dragEdit is null || _dragIconId is null) return false;
        var targetId = _folderIntent.TargetId!;
        var target = _dragEdit.Draft.Pages.SelectMany(p => p.Slots).FirstOrDefault(s => s.ItemId == targetId || s.Group?.Id == targetId);
        if (target is null) return false;
        if (target.Kind == LauncherSlotKind.Group)
        {
            _dragEdit.Draft = LauncherLayoutEditor.AddToGroup(_dragEdit.Draft, _dragIconId, targetId, _view.Capacity);
            _dropLandingId = targetId;
        }
        else
        {
            var id = "folder-" + Guid.NewGuid().ToString("N");
            _dragEdit.Draft = LauncherLayoutEditor.CreateGroup(_dragEdit.Draft, _dragIconId, targetId, id, _view.Capacity);
            _dropLandingId = id;
        }
        return true;
    }

    private void ClearFolderHover(bool immediate = false)
    {
        _folderIntent.Reset();
        _folderHoverTile = null;
        _fusionSpring.Target = 0;
        if (immediate)
        {
            _fusionSpring.Snap(0);
            if (_fusionLens is not null) DragOverlay.Children.Remove(_fusionLens);
            _fusionLens = null;
        }
        else if (_fusionLens is not null || _fusionSpring.IsMoving) EnsureFolderFrames();
    }

    private void ApplyFusionFrame()
    {
        var strength = Math.Clamp(_fusionSpring.Value, 0, 1);
        if (strength < .002 && _fusionSpring.Target == 0)
        {
            if (_fusionLens is not null) DragOverlay.Children.Remove(_fusionLens);
            _fusionLens = null;
            return;
        }
        if (_fusionLens is null || !DragOverlay.Children.Contains(_fusionLens))
        {
            _fusionLens = new SharedGlassLens { Source = GlassSurface };
            DragOverlay.Children.Insert(0, _fusionLens);
        }
        // Confirmation changes only this outline after the dwell time. Neither
        // the target icon nor the dragged icon receives an attraction transform.
        var size = AppIconSize * (1.04 + Math.Clamp(_fusionSpring.Value, 0, 1.1) * .10);
        Canvas.SetLeft(_fusionLens, _fusionA.X - size / 2);
        Canvas.SetTop(_fusionLens, _fusionA.Y - size / 2);
        _fusionLens.Width = size; _fusionLens.Height = size;
        _fusionLens.Radius = size * .38;
        _fusionLens.Highlight = strength;
        _fusionLens.Opacity = Math.Clamp(strength * 1.5, 0, 1);
        _fusionLens.InvalidateVisual();
    }

    private bool PreviewFolderMemberInside(Point point)
    {
        if (_dragSourceGroupId is null || _memberOrigin is null || _dragEdit is null || _dragIconId is null || _memberHasLeft) return false;
        var local = TileGrid.TranslatePoint(new Point(point.X - IncomingPageMotion.X, point.Y), FolderCard);
        var inside = GroupPanel.Visibility == Visibility.Visible && !_folderClosing
            && new Rect(FolderCard.RenderSize).Contains(local);
        if (!inside)
        {
            if (!_memberHasLeft)
            {
                _memberHasLeft = true;
                _previewIndex = -1;
                _dragEdit.Draft = LauncherLayoutRules.DeepClone(_dragOrigin!);
            }
            if (!_folderClosing && GroupPanel.Visibility == Visibility.Visible) CloseFolder();
            ClearFolderHover();
            return false;
        }
        ClearFolderHover();
        var p = TileGrid.TranslatePoint(new Point(point.X - IncomingPageMotion.X, point.Y), GroupItems);
        var count = _memberOrigin.Pages.SelectMany(pg => pg.Slots).First(s => s.Group?.Id == _dragSourceGroupId).Group!.ItemIds.Count;
        var cellWidth = Math.Max(1, GroupItems.ActualWidth / GroupItems.Columns);
        var rows = Math.Ceiling(count / (double)GroupItems.Columns);
        var cellHeight = Math.Max(1, GroupItems.ActualHeight / rows);
        var index = Math.Clamp((int)(p.Y / cellHeight) * GroupItems.Columns + Math.Clamp((int)(p.X / cellWidth), 0, GroupItems.Columns - 1), 0, count - 1);
        if (_memberPreviewIndex != index)
        {
            _memberPreviewIndex = index;
            _dragEdit.Draft = LauncherLayoutEditor.MoveGroupMember(_memberOrigin, _dragSourceGroupId, _dragIconId, index);
            var ids = _dragEdit.Draft.Pages.SelectMany(pg => pg.Slots).First(s => s.Group?.Id == _dragSourceGroupId).Group!.ItemIds;
            var tiles = GroupItems.Children.OfType<Button>().ToDictionary(t => (string)t.Tag);
            var old = tiles.Values.ToDictionary(t => t, t => t.TranslatePoint(new Point(), GroupItems));
            GroupItems.Children.Clear();
            foreach (var id in ids) GroupItems.Children.Add(tiles[id]);
            GroupItems.UpdateLayout();
            foreach (var tile in tiles.Values)
            {
                var position = tile.TranslatePoint(new Point(), GroupItems);
                tile.RenderTransform = new TranslateTransform();
                var motion = (TranslateTransform)tile.RenderTransform;
                AnimateIconOffset(motion, old[tile].X - position.X, old[tile].Y - position.Y);
                tile.Opacity = Equals(tile.Tag, _dragIconId) ? 0 : 1;
            }
        }
        var scrollPoint = TileGrid.TranslatePoint(new Point(point.X - IncomingPageMotion.X, point.Y), GroupScroll);
        if (scrollPoint.Y < 30) GroupScroll.ScrollToVerticalOffset(GroupScroll.VerticalOffset - 5);
        else if (scrollPoint.Y > GroupScroll.ActualHeight - 30) GroupScroll.ScrollToVerticalOffset(GroupScroll.VerticalOffset + 5);
        return true;
    }
}
