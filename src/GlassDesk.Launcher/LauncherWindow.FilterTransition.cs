using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private bool _filterTransitionActive, _filterTransitionClosing;
    private string? _filterTransitionTarget;
    private long _filterTransitionStarted;
    private readonly List<FilterTileMotion> _filterTileMotions = [];

    private sealed record FilterTileMotion(FrameworkElement Tile, Transform OriginalTransform,
        double OriginalOpacity, TranslateTransform Offset, double StartOpacity, double StartOffset, int Row);

    private void SwitchFilterGroup(string? target)
    {
        if (!IsVisible || ThemeService.Current?.AnimationsEnabled == false)
        {
            StopFilterTransition(completeTarget: false);
            _activeFilterGroupId = target;
            _view.MoveToPage(0);
            RenderTiles();
        }
        else if (_activeFilterGroupId is not null)
            BeginFilterTransition(closing: true, target);
        else
        {
            StopFilterTransition(completeTarget: false);
            _activeFilterGroupId = target;
            _view.MoveToPage(0);
            RenderTiles();
            if (target is not null) BeginFilterTransition(closing: false, target);
        }
    }

    private void BeginFilterTransition(bool closing, string? target)
    {
        // Sample an interrupted phase before retiring its wrappers. The new phase
        // starts at those pixels instead of flashing every icon back to full opacity.
        var interrupted = _filterTileMotions.ToDictionary(m => m.Tile, m => (m.Tile.Opacity, m.Offset.Y));
        StopFilterTransition(completeTarget: false);
        var tiles = TileGrid.Children.OfType<FrameworkElement>().ToArray();
        if (tiles.Length == 0)
        {
            if (closing) CompleteFilterSwitch(target);
            return;
        }
        _filterTransitionActive = true;
        _filterTransitionClosing = closing;
        _filterTransitionTarget = target;
        _filterTransitionStarted = Stopwatch.GetTimestamp();
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            var original = tile.RenderTransform;
            var opacity = tile.Opacity;
            var start = interrupted.GetValueOrDefault(tile, (closing ? opacity : 0d, closing ? 0d : -18d));
            var offset = new TranslateTransform(0, start.Item2);
            var transform = new TransformGroup();
            transform.Children.Add(original); transform.Children.Add(offset);
            _filterTileMotions.Add(new(tile, original, opacity, offset, start.Item1, start.Item2, i / _view.Columns));
            tile.RenderTransform = transform;
            tile.Opacity = start.Item1;
        }
        PageScroll.IsHitTestVisible = false;
        RefreshFilterSelection();
        CompositionTarget.Rendering += OnFilterTransitionRendering;
        NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFilterTransitionRendering(object? sender, EventArgs e)
        => UpdateFilterTransition(Stopwatch.GetElapsedTime(_filterTransitionStarted).TotalMilliseconds);

    private void UpdateFilterTransition(double elapsedMilliseconds)
    {
        if (!_filterTransitionActive) return;
        var rows = (_filterTileMotions.Count + _view.Columns - 1) / _view.Columns;
        const double duration = 190, stagger = 35;
        foreach (var motion in _filterTileMotions)
        {
            var sequence = _filterTransitionClosing ? rows - 1 - motion.Row : motion.Row;
            var progress = Math.Clamp((elapsedMilliseconds - sequence * stagger) / duration, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            var endOpacity = _filterTransitionClosing ? 0 : motion.OriginalOpacity;
            var endOffset = _filterTransitionClosing ? -18 : 0;
            motion.Tile.Opacity = motion.StartOpacity + (endOpacity - motion.StartOpacity) * eased;
            motion.Offset.Y = motion.StartOffset + (endOffset - motion.StartOffset) * eased;
        }
        if (elapsedMilliseconds < duration + (rows - 1) * stagger) return;
        if (_filterTransitionClosing) CompleteFilterSwitch(_filterTransitionTarget);
        else StopFilterTransition(completeTarget: false);
    }

    private void CompleteFilterSwitch(string? target)
    {
        StopFilterTransition(completeTarget: false);
        _activeFilterGroupId = target;
        _view.MoveToPage(0);
        RenderTiles();
        if (target is not null && IsVisible && ThemeService.Current?.AnimationsEnabled != false)
            BeginFilterTransition(closing: false, target);
    }

    private void StopFilterTransition(bool completeTarget = true)
    {
        if (!_filterTransitionActive) return;
        if (completeTarget)
        {
            _activeFilterGroupId = _filterTransitionTarget;
            if (_filterTransitionClosing) _view.MoveToPage(0);
        }
        CompositionTarget.Rendering -= OnFilterTransitionRendering;
        foreach (var motion in _filterTileMotions)
        {
            motion.Tile.Opacity = motion.OriginalOpacity;
            motion.Tile.RenderTransform = motion.OriginalTransform;
        }
        _filterTileMotions.Clear();
        _filterTransitionActive = _filterTransitionClosing = false;
        _filterTransitionTarget = null;
        PageScroll.IsHitTestVisible = true;
        RefreshFilterSelection();
        NavigationAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
