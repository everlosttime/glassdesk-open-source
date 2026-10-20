using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlassDesk.Launcher;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static void VerifyPageSlide(LauncherWindow window, UniformGrid grid, string evidenceDirectory)
    {
        var outgoing = window.FindName("OutgoingPageGrid") as UniformGrid;
        var incomingMotion = grid.RenderTransform as TranslateTransform;
        Check("翻页提供横向过渡图层", "L-08", outgoing is not null && incomingMotion is not null,
            "真实图标同时滑出、滑入", $"旧页图层 {outgoing is not null}，横向变换 {incomingMotion is not null}");
        if (outgoing is null || incomingMotion is null) return;
        var outgoingMotion = (TranslateTransform)outgoing.RenderTransform;
        var next = (Button)window.FindName("NextPage")!;
        var previous = (Button)window.FindName("PreviousPage")!;
        var pageText = (TextBlock)window.FindName("PageText")!;
        var search = (TextBox)window.FindName("SearchBox")!;
        bool Settled() => outgoing.Children.Count == 0 && Math.Abs(incomingMotion.X) < .01;

        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var initialX = incomingMotion.X;
        Check("下一页从右侧进入且保留完整旧页", "L-08",
            initialX > 0 && outgoing.Children.Count == 35 && grid.Children.Count == 1 && pageText.Text == "2 / 2",
            "新页在右侧，旧页35项，新页1项", $"偏移 {initialX:0.0}，旧页 {outgoing.Children.Count}，新页 {grid.Children.Count}");
        Pump(TimeSpan.FromMilliseconds(80));
        Check("下一页中途双页向左移动", "L-08",
            incomingMotion.X > 0 && incomingMotion.X < initialX && outgoingMotion.X < 0,
            "新页逐渐移入，旧页向左移出", $"新页 {incomingMotion.X:0.0}，旧页 {outgoingMotion.X:0.0}");
        Capture(window, System.IO.Path.Combine(evidenceDirectory, "launcher-page-slide.png"));
        Check("翻页完成释放旧页且恢复位置", "L-08", PumpUntil(Settled, TimeSpan.FromSeconds(1)),
            "旧页释放，位移归零", $"旧页 {outgoing.Children.Count}，位移 {incomingMotion.X:0.0}");

        window.NavigatePage(1);
        Check("末页继续翻页不产生动画", "L-08", Settled() && pageText.Text == "2 / 2",
            "保持末页", pageText.Text);
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("上一页从左侧进入", "L-08", incomingMotion.X < 0 && outgoing.Children.Count == 1 && grid.Children.Count == 35,
            "新页在左侧，旧页1项", $"偏移 {incomingMotion.X:0.0}，旧页 {outgoing.Children.Count}");
        var midPageClock = System.Diagnostics.Stopwatch.StartNew();
        Pump(TimeSpan.FromMilliseconds(60));
        Check("上一页中途旧页向右退出", "L-08", incomingMotion.X < 0 && outgoingMotion.X > 0,
            "双页向右移动", $"新页 {incomingMotion.X:0.0}，旧页 {outgoingMotion.X:0.0}，采样等待 {midPageClock.Elapsed.TotalMilliseconds:F1}ms");
        PumpUntil(Settled, TimeSpan.FromSeconds(1));

        window.NavigatePage(1);
        window.NavigatePage(-1);
        Check("连续反向翻页最终内容正确且无残留", "L-08",
            PumpUntil(Settled, TimeSpan.FromSeconds(1)) && grid.Children.Count == 35 && pageText.Text == "1 / 2",
            "35项、1 / 2，无旧页残留", $"{grid.Children.Count}项，{pageText.Text}，旧页 {outgoing.Children.Count}");

        window.NavigatePage(1);
        search.Text = "qidongtai";
        Check("搜索中断翻页并立即呈现过滤结果", "L-03/L-08", Settled() && grid.Children.Count == 1 && pageText.Text == "1 / 1",
            "清除旧页并显示搜索结果", $"旧页 {outgoing.Children.Count}，{pageText.Text}");
        search.Clear();
        window.NavigatePage(-1);
        PumpUntil(Settled, TimeSpan.FromSeconds(1));
        var appearance = ThemeService.Current!.Appearance;
        var originalReduceMotion = appearance.ReduceMotion;
        try
        {
            appearance.ReduceMotion = true;
            ThemeService.Current.Refresh();
            window.NavigatePage(1);
            Check("减少动画设置下直接翻页", "L-08", Settled() && grid.Children.Count == 1 && pageText.Text == "2 / 2",
                "不保留动画图层", $"旧页 {outgoing.Children.Count}，{pageText.Text}");
            window.NavigatePage(-1);
        }
        finally
        {
            appearance.ReduceMotion = originalReduceMotion;
            ThemeService.Current.Refresh();
        }
        window.NavigatePage(1);
        window.Hide();
        Check("隐藏启动台时清除翻页动画", "L-08", Settled(),
            "旧页释放，位移归零", $"旧页 {outgoing.Children.Count}，位移 {incomingMotion.X:0.0}");
        window.Show();
        window.NavigatePage(-1);
        PumpUntil(Settled, TimeSpan.FromSeconds(1));
        Pump(TimeSpan.FromMilliseconds(60));
    }
}
