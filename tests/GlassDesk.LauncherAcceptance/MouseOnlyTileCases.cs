using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Reflection;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyMouseOnlyTiles(LauncherWindow window, UniformGrid grid, TextBox search)
    {
        var groupPanel = (PhoneBorder)window.FindName("GroupPanel")!;
        var originalTiles = grid.Children.Cast<Button>().ToArray();
        search.Focus();
        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Down)
        { RoutedEvent = Keyboard.KeyDownEvent });
        Check("搜索框方向键不进入应用图标", "L-MOUSE", Keyboard.FocusedElement == search
            && originalTiles.SequenceEqual(grid.Children.Cast<Button>()),
            "搜索框保留焦点且应用网格不重建", $"焦点 {Keyboard.FocusedElement?.GetType().Name}；网格复用 {originalTiles.SequenceEqual(grid.Children.Cast<Button>())}");
        var tile = (Button)grid.Children[0];
        Check("应用图标不能获取键盘焦点", "L-MOUSE", !tile.Focus() && !tile.IsTabStop,
            "图标不能获取焦点或通过 Tab 选中", $"Focusable {tile.Focusable}；TabStop {tile.IsTabStop}");
        foreach (var key in new[] { Key.Right, Key.Enter, Key.Space })
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Check("方向键和确认键不会打开应用或分组", "L-MOUSE", groupPanel.Visibility == Visibility.Collapsed,
            "分组保持关闭", groupPanel.Visibility.ToString());
        ((Button)grid.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RefreshLibrary();
        tile = (Button)grid.Children[0];
        tile.ApplyTemplate();
        var chrome = (PhoneBorder)tile.Template.FindName("TileChrome", tile);
        Check("鼠标点击并刷新后图标无常驻边框", "L-MOUSE", chrome.BorderBrush is SolidColorBrush { Color.A: 0 },
            "透明边框", chrome.BorderBrush?.ToString() ?? "无边框");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
        { RoutedEvent = Keyboard.KeyDownEvent });
        Check("启动台不再通过 Esc 操作分组", "L-MOUSE", groupPanel.Visibility == Visibility.Visible,
            "分组保持打开，使用鼠标返回", groupPanel.Visibility.ToString());
        FindVisual<Button>(groupPanel).Single(button => Equals(button.Content, "返回"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        tile = (Button)grid.Children[0];
        Pump(TimeSpan.FromMilliseconds(250));
        var iconHost = ((StackPanel)tile.Content).Children[0] as Border;
        var pressedKey = (DependencyPropertyKey)typeof(ButtonBase)
            .GetField("IsPressedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        tile.SetValue(pressedKey, true);
        Pump(TimeSpan.FromMilliseconds(100));
        Check("按下只轻微缩小当前图标", "L-MOUSE", iconHost?.RenderTransform is ScaleTransform { ScaleX: < 1 and >= .95, ScaleY: < 1 and >= .95 },
            "当前图标轻微缩小", iconHost?.RenderTransform.ToString() ?? "无图标容器");
        tile.SetValue(pressedKey, false);
        Pump(TimeSpan.FromMilliseconds(300));
        Check("松开后图标恢复且没有残留动画", "L-MOUSE", iconHost?.RenderTransform is ScaleTransform { ScaleX: 1, ScaleY: 1, HasAnimatedProperties: false },
            "原始大小且动画时钟结束", iconHost?.RenderTransform is ScaleTransform motion
                ? $"X {motion.ScaleX}；Y {motion.ScaleY}；Animated {motion.HasAnimatedProperties}"
                : "无图标容器");
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisual<T>(child)) yield return nested;
        }
    }
}
