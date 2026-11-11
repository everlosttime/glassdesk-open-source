using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private Point _filterGrabPoint;
    private ToggleButton? _filterDragGhost;
    private TranslateTransform? _filterGhostMotion;
    private double _filterSourceOpacity;
    private bool _filterSourceHitTestVisible, _filterMotionFrames;
    private long _filterMotionTicks;
    private TimeSpan? _filterRenderingTime;
    private readonly Dictionary<ToggleButton, FilterChipMotion> _filterChipMotions = [];
    private sealed record FilterChipMotion(Transform Original, TranslateTransform Offset);

    private void CreateFilterGroupDragVisuals()
    {
        if (_filterPressedChip is not { } source) return;
        foreach (var chip in _filterOriginalChips)
        {
            var offset = new TranslateTransform();
            _filterChipMotions[chip] = new(chip.RenderTransform, offset);
            var transform = new TransformGroup();
            transform.Children.Add(chip.RenderTransform); transform.Children.Add(offset);
            chip.RenderTransform = transform;
        }
        _filterGhostMotion = new TranslateTransform(0, source.TranslatePoint(new Point(), DragOverlay).Y);
        var ghostTransform = new TransformGroup();
        ghostTransform.Children.Add(new ScaleTransform(1.12, 1.12));
        ghostTransform.Children.Add(_filterGhostMotion);
        _filterDragGhost = new ToggleButton
        {
            Content = source.Content, IsChecked = source.IsChecked,
            Width = source.ActualWidth, Height = source.ActualHeight, Margin = new Thickness(0),
            Style = (Style)FindResource("FilterGroupChip"), IsHitTestVisible = false, Focusable = false,
            RenderTransform = ghostTransform, RenderTransformOrigin = new Point(.5, .5),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 5, Opacity = .28 },
        };
        DragOverlay.Children.Add(_filterDragGhost);
        _filterSourceOpacity = source.Opacity;
        _filterSourceHitTestVisible = source.IsHitTestVisible;
        source.Opacity = 0;
        source.IsHitTestVisible = false;
    }

    private void MoveFilterGroupGhost(Point point)
    {
        if (_filterGhostMotion is null) return;
        var pointer = FilterGroupBar.TranslatePoint(point, DragOverlay);
        _filterGhostMotion.X = pointer.X - _filterGrabPoint.X;
    }

    private void ApplyFilterGroupPreview(int targetIndex)
    {
        if (_filterPressedChip is not { } source) return;
        var from = FilterGroupButtons.Children.IndexOf(source);
        if (from < 0 || from == targetIndex) return;
        var before = _filterChipMotions.ToDictionary(pair => pair.Key,
            pair => LayoutInformation.GetLayoutSlot(pair.Key).X + pair.Value.Offset.X);
        FilterGroupButtons.Children.RemoveAt(from);
        FilterGroupButtons.Children.Insert(targetIndex, source);
        FilterGroupButtons.UpdateLayout();
        var animated = ThemeService.Current?.AnimationsEnabled != false;
        foreach (var (chip, motion) in _filterChipMotions)
            motion.Offset.X = animated ? before[chip] - LayoutInformation.GetLayoutSlot(chip).X : 0;
        if (!animated) return;
        _filterMotionTicks = Stopwatch.GetTimestamp();
        if (_filterMotionFrames) return;
        _filterMotionFrames = true;
        _filterRenderingTime = null;
        CompositionTarget.Rendering += OnFilterGroupMotionFrame;
    }

    private void OnFilterGroupMotionFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (_filterRenderingTime == rendering.RenderingTime) return;
            _filterRenderingTime = rendering.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_filterMotionTicks, now).TotalSeconds;
        _filterMotionTicks = now;
        AdvanceFilterGroupAnimations(seconds);
    }

    private void AdvanceFilterGroupAnimations(double seconds)
    {
        var moving = false;
        foreach (var motion in _filterChipMotions.Values)
        {
            // Match DockHoverScale's response, retaining the current pixels on retarget.
            var next = motion.Offset.X * Math.Exp(-Math.Max(0, seconds) / .075);
            motion.Offset.X = Math.Abs(next) <= .001 ? 0 : next;
            moving |= motion.Offset.X != 0;
        }
        if (!moving) StopFilterGroupMotionFrames();
    }

    private void StopFilterGroupMotionFrames()
    {
        if (_filterMotionFrames) CompositionTarget.Rendering -= OnFilterGroupMotionFrame;
        _filterMotionFrames = false;
        _filterRenderingTime = null;
    }

    private void ClearFilterGroupDragVisuals(ToggleButton source)
    {
        StopFilterGroupMotionFrames();
        foreach (var (chip, motion) in _filterChipMotions) chip.RenderTransform = motion.Original;
        _filterChipMotions.Clear();
        if (_filterDragGhost is not null)
        {
            DragOverlay.Children.Remove(_filterDragGhost);
            source.Opacity = _filterSourceOpacity;
            source.IsHitTestVisible = _filterSourceHitTestVisible;
        }
        _filterDragGhost = null;
        _filterGhostMotion = null;
    }
}
