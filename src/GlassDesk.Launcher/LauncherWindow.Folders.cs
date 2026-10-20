using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.Core.Models;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private readonly FolderSpring _folderOpen = new();
    private Rect _folderOrigin, _folderDestination;
    private string? _displayedFolderId;
    private string? _folderItemsKey;
    private bool _folderClosing, _folderFramesAttached;
    private long _folderFrameTicks;
    private TimeSpan _folderRenderingTime = TimeSpan.MinValue;

    private Grid CreateFolderIcon(LauncherGroup group)
    {
        var size = AppIconSize;
        var root = new Grid { Width = size, Height = size };
        root.Children.Add(new SharedGlassLens { Source = GlassSurface, Radius = size * .38 });
        var count = Math.Min(9, group.ItemIds.Count);
        var columns = count <= 4 ? 2 : 3;
        var rows = (int)Math.Ceiling(count / (double)columns);
        var previews = new System.Windows.Controls.Primitives.UniformGrid
        { Columns = columns, Rows = Math.Max(1, rows), Margin = new Thickness(size * .13), VerticalAlignment = VerticalAlignment.Center };
        var small = (size * .74 - (columns - 1) * 2) / columns;
        foreach (var id in group.ItemIds.Take(9))
        {
            var entry = _services?.Library.Find(id);
            var host = new Border { Width = small, Height = small, Margin = new Thickness(1),
                CornerRadius = new CornerRadius(small * .24), Background = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)) };
            if (entry is not null) RequestIcon(entry, host, small);
            previews.Children.Add(host);
        }
        root.Children.Add(previews);
        return root;
    }

    private static void AnimateFolderHighlight(SharedGlassLens lens, double value)
    {
        if (ThemeService.Current?.AnimationsEnabled == false) { lens.Highlight = value; return; }
        lens.BeginAnimation(SharedGlassLens.HighlightProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(180)));
    }

    private void UpdateFolderPanel()
    {
        var group = _workspace?.Layout.Pages.SelectMany(p => p.Slots)
            .FirstOrDefault(s => s.Group?.Id == _view.OpenGroupId)?.Group;
        if (group is null)
        {
            if (_displayedFolderId is not null && GroupPanel.Visibility == Visibility.Visible) CloseFolder();
            else GroupPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var opening = _displayedFolderId != group.Id || GroupPanel.Visibility != Visibility.Visible;
        _displayedFolderId = group.Id;
        _folderClosing = false;
        GroupPanel.Visibility = Visibility.Visible;
        FolderGlass.Source = GlassSurface;
        GroupTitle.Text = group.Name;
        var key = group.Id + "\0" + string.Join("\0", group.ItemIds) + "\0" + _services?.Library.Version;
        if (_folderItemsKey != key)
        {
            _folderItemsKey = key;
            GroupItems.Children.Clear();
            GroupItems.Columns = Math.Clamp(_view.Columns, 2, 4);
            foreach (var id in group.ItemIds) GroupItems.Children.Add(CreateTile(new LauncherSlot { ItemId = id }));
        }
        GroupPanel.UpdateLayout();
        var width = Math.Max(180, Math.Min(600, FolderCanvas.ActualWidth - 24));
        var naturalHeight = 104 + Math.Ceiling(group.ItemIds.Count / (double)GroupItems.Columns) * (AppIconSize + 48);
        var height = Math.Max(150, Math.Min(Math.Min(460, naturalHeight), FolderCanvas.ActualHeight - 24));
        FolderHint.Text = $"{group.ItemIds.Count} 个应用 · " + (naturalHeight > height + 1 ? "滚轮查看更多 · " : "") + "长按可整理或拖出";
        _folderDestination = new Rect(Math.Max(0, (FolderCanvas.ActualWidth - width) / 2),
            Math.Max(0, (FolderCanvas.ActualHeight - height) / 2), width, height);
        GroupContent.Width = Math.Max(100, width - 36);
        GroupContent.Height = Math.Max(100, height - 36);
        GroupContent.HorizontalAlignment = HorizontalAlignment.Left;
        GroupContent.VerticalAlignment = VerticalAlignment.Top;
        if (opening)
        {
            var tile = TileGrid.Children.OfType<LauncherAppTileButton>().FirstOrDefault(t => Equals(t.Tag, group.Id));
            _folderOrigin = tile is not null ? IconBounds(tile, FolderCanvas)
                : new Rect(_folderDestination.X + width / 2 - AppIconSize / 2, _folderDestination.Y + height / 2 - AppIconSize / 2, AppIconSize, AppIconSize);
            _folderOpen.Snap(0);
            GroupScroll.ScrollToTop();
        }
        _folderOpen.Target = 1;
        if (ThemeService.Current?.AnimationsEnabled == false) _folderOpen.Snap(1);
        ApplyFolderFrame();
        EnsureFolderFrames();
    }

    private static Rect IconBounds(LauncherAppTileButton tile, Visual relativeTo)
    {
        var host = tile.IconHost;
        return host.TransformToVisual(relativeTo).TransformBounds(new Rect(new Size(Math.Max(1, host.ActualWidth), Math.Max(1, host.ActualHeight))));
    }

    private void CloseFolder()
    {
        if (_displayedFolderId is null) { GroupPanel.Visibility = Visibility.Collapsed; _view.OpenGroupId = null; return; }
        var tile = TileGrid.Children.OfType<LauncherAppTileButton>().FirstOrDefault(t => Equals(t.Tag, _displayedFolderId));
        if (tile is not null) _folderOrigin = IconBounds(tile, FolderCanvas);
        _folderClosing = true;
        _folderOpen.Target = 0;
        if (ThemeService.Current?.AnimationsEnabled == false) _folderOpen.Snap(0);
        EnsureFolderFrames();
    }

    private void ResetFolderPresentation()
    {
        _view.OpenGroupId = null;
        _displayedFolderId = null;
        _folderItemsKey = null;
        _folderClosing = false;
        _folderOpen.Snap(0);
        GroupPanel.Visibility = Visibility.Collapsed;
        GroupItems.Children.Clear();
        TileGrid.Opacity = 1;
        foreach (var tile in TileGrid.Children.OfType<Button>()) tile.Opacity = 1;
        StopFolderFrames();
        ClearFolderHover(immediate: true);
    }

    private void OnFolderOutsideDown(object sender, MouseButtonEventArgs e) { if (!_logicalIconDrag) CloseFolder(); e.Handled = true; }
    private void OnRenameOpenFolder(object sender, RoutedEventArgs e) { if (_view.OpenGroupId is { } id) RenameGroup(id); }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_logicalIconDrag && e.Key == Key.Escape && _displayedFolderId is not null)
        { CloseFolder(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!_logicalIconDrag && !_folderClosing && _displayedFolderId is not null && e.ChangedButton == MouseButton.Left
            && !new Rect(FolderCard.RenderSize).Contains(e.GetPosition(FolderCard))) CloseFolder();
        base.OnPreviewMouseDown(e);
    }

    private void EnsureFolderFrames()
    {
        if (_folderFramesAttached) return;
        _folderFramesAttached = true;
        _folderFrameTicks = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFolderFrame;
    }

    private void StopFolderFrames()
    {
        if (_folderFramesAttached) CompositionTarget.Rendering -= OnFolderFrame;
        _folderFramesAttached = false;
        _folderRenderingTime = TimeSpan.MinValue;
    }

    private void OnFolderFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        { if (_folderRenderingTime == rendering.RenderingTime) return; _folderRenderingTime = rendering.RenderingTime; }
        if (!IsVisible) { ResetFolderPresentation(); return; }
        var now = Stopwatch.GetTimestamp();
        var dt = Stopwatch.GetElapsedTime(_folderFrameTicks, now).TotalSeconds;
        _folderFrameTicks = now;
        if (ThemeService.Current?.AnimationsEnabled == false) { _folderOpen.Snap(_folderOpen.Target); _fusionSpring.Snap(_fusionSpring.Target); }
        else { _folderOpen.Advance(dt); _fusionSpring.Advance(dt); }
        if (_displayedFolderId is not null) ApplyFolderFrame();
        ApplyFusionFrame();
        if (!_folderOpen.IsMoving && !_fusionSpring.IsMoving && _folderIntent.TargetId is null) StopFolderFrames();
    }

    private void ApplyFolderFrame()
    {
        var t = _folderOpen.Value;
        var opacity = Math.Clamp(t, 0, 1);
        var origin = _folderOrigin;
        var end = _folderDestination;
        var bounds = new Rect(origin.X + (end.X - origin.X) * t, origin.Y + (end.Y - origin.Y) * t,
            Math.Max(1, origin.Width + (end.Width - origin.Width) * t), Math.Max(1, origin.Height + (end.Height - origin.Height) * t));
        Canvas.SetLeft(FolderCard, bounds.X); Canvas.SetTop(FolderCard, bounds.Y);
        FolderCard.Width = bounds.Width; FolderCard.Height = bounds.Height;
        var radius = AppIconSize * .38 + (44 - AppIconSize * .38) * opacity;
        FolderGlass.Radius = radius;
        FolderGlass.InvalidateVisual();
        FolderCard.Clip = SquircleGlassGeometry.CreateCompact(new Rect(bounds.Size), radius);
        FolderShadow.Data = FolderCard.Clip;
        Canvas.SetLeft(FolderShadow, bounds.X); Canvas.SetTop(FolderShadow, bounds.Y + 5);
        FolderShadow.Opacity = opacity;
        GroupContent.Opacity = Math.Clamp((opacity - .24) / .58, 0, 1);
        FolderDismissArea.Opacity = opacity;
        TileGrid.Opacity = 1 - opacity * .30;
        foreach (var tile in TileGrid.Children.OfType<LauncherAppTileButton>())
        {
            if (_logicalIconDrag && !_folderClosing) break;
            if (_logicalIconDrag && Equals(tile.Tag, _dragIconId)) { tile.Opacity = 0; continue; }
            var icon = IconBounds(tile, FolderCanvas);
            tile.Opacity = bounds.IntersectsWith(icon) ? 1 - opacity : 1;
        }
        var index = 0;
        foreach (var tile in GroupItems.Children.OfType<Button>())
        {
            if (_logicalIconDrag) break;
            var p = Math.Clamp((opacity - .32 - Math.Min(index++, 11) * .025) / .35, 0, 1);
            var ease = 1 - Math.Pow(1 - p, 3);
            tile.Opacity = p;
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(.84 + .16 * ease, .84 + .16 * ease));
            transform.Children.Add(new TranslateTransform(0, (1 - ease) * 15));
            tile.RenderTransform = transform;
        }
        if (_folderClosing && !_folderOpen.IsMoving && _folderOpen.Value == 0)
        {
            _displayedFolderId = null; _folderItemsKey = null; _view.OpenGroupId = null;
            GroupPanel.Visibility = Visibility.Collapsed; GroupItems.Children.Clear(); TileGrid.Opacity = 1;
            // The close can finish while a member is still held outside. Its
            // desktop slot stays hidden until FinishIconDrag hands the ghost back.
            foreach (var tile in TileGrid.Children.OfType<Button>())
                tile.Opacity = _dragOrigin is not null && Equals(tile.Tag, _dragIconId) ? 0 : 1;
        }
    }
}
