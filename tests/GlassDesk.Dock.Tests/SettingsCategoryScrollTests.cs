using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public class SettingsCategoryScrollTests
{
    [Fact]
    public void SidebarScrollsIndependentlyAndFollowsSelectedContent()
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow { Height = 365, ShowActivated = false };
            try
            {
                window.Show();
                FlushLayout(window);
                var pages = Assert.IsType<ListBox>(window.FindName("SettingsPageList"));
                var content = Assert.IsType<ScrollViewer>(window.FindName("SettingsScroll"));
                var sidebar = Assert.IsType<ScrollViewer>(FindScrollViewer(pages));
                Assert.True(sidebar.ScrollableHeight > 0);
                var initialPage = pages.SelectedItem;
                var initialOffset = content.VerticalOffset;

                sidebar.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = UIElement.MouseWheelEvent });
                FlushLayout(window);
                Assert.True(sidebar.VerticalOffset > 0);
                Assert.Equal(initialPage, pages.SelectedItem);
                Assert.Equal(initialOffset, content.VerticalOffset);

                sidebar.ScrollToTop();
                FlushLayout(window);
                content.ScrollToEnd();
                FlushLayout(window);
                Assert.Equal("关于", pages.SelectedItem);
                Assert.True(sidebar.VerticalOffset > 0);

                pages.SelectedItem = "回收站";
                FlushLayout(window);
                var heading = Assert.IsType<TextBlock>(window.FindName("NaiwaSectionHeading"));
                Assert.InRange(heading.TranslatePoint(new Point(), content).Y, 0, 18);
            }
            finally { window.Close(); }
        });
    }

    private static void FlushLayout(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer) return scrollViewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } child)
                return child;
        }
        return null;
    }
}
