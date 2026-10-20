using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    // Render transforms move native tile visuals without rebuilding them every frame.
    private void SlideToPage(int direction)
    {
        var previousPage = _view.Page;
        _view.MoveToPage(previousPage + direction);
        if (_view.Page == previousPage) return;

        StopPageSlide();
        var distance = PageViewport.ActualWidth;
        var animate = ThemeService.Current?.AnimationsEnabled != false && distance > 0;
        var oldTiles = animate ? TileGrid.Children.Cast<UIElement>().ToArray() : [];
        var oldHeight = TileGrid.ActualHeight;
        var oldScrollOffset = PageScroll.VerticalOffset;
        RenderTiles(reusePageTiles: true);
        if (!animate || oldTiles.Length == 0) return;

        OutgoingPageGrid.Rows = TileGrid.Rows;
        OutgoingPageGrid.Columns = TileGrid.Columns;
        OutgoingPageGrid.Width = TileGrid.Width;
        OutgoingPageGrid.Height = oldHeight;
        foreach (var tile in oldTiles) OutgoingPageGrid.Children.Add(tile);
        OutgoingPageScroll.Visibility = Visibility.Visible;
        OutgoingPageScroll.ScrollToVerticalOffset(oldScrollOffset);
        // Moving tiles must not become accidental click or drag targets.
        PageScroll.IsHitTestVisible = false;

        var offset = Math.Sign(direction) * distance;
        // The animation clock starts on the next render tick; position the new
        // page now so that it cannot flash at the destination for one frame.
        IncomingPageMotion.X = offset;
        var duration = TimeSpan.FromMilliseconds(240);
        var incoming = new DoubleAnimation(offset, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        var outgoing = new DoubleAnimation(0, -offset, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        incoming.Completed += (_, _) => StopPageSlide();
        IncomingPageMotion.BeginAnimation(TranslateTransform.XProperty, incoming, HandoffBehavior.SnapshotAndReplace);
        OutgoingPageMotion.BeginAnimation(TranslateTransform.XProperty, outgoing, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopPageSlide()
    {
        IncomingPageMotion.BeginAnimation(TranslateTransform.XProperty, null);
        OutgoingPageMotion.BeginAnimation(TranslateTransform.XProperty, null);
        IncomingPageMotion.X = OutgoingPageMotion.X = 0;
        OutgoingPageScroll.Visibility = Visibility.Collapsed;
        OutgoingPageGrid.Children.Clear();
        PageScroll.IsHitTestVisible = true;
    }
}
