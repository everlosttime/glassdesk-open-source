using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;

internal static partial class Program
{
    private static void RecordTransitionPerformance(LauncherWindow window, string evidence)
    {
        window.Hide();
        window.PrepareToShow();
        var preparedGrid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("TileGrid")!;
        var preparedTiles = preparedGrid.Children.OfType<Button>().ToArray();
        window.Show();
        Check("打开窗口不重复重建已准备的图标控件", "performance/visual-parity",
            preparedTiles.Length > 0 && preparedTiles.SequenceEqual(preparedGrid.Children.OfType<Button>()),
            "复用同一批图标控件", "prepared=" + preparedTiles.Length);
        VerifyRepeatedPreparation(window);
        var display = DisplayService.Primary();
        if (display is null) return;
        var anchor = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
        var samples = new List<object>();
        window.Hide();
        window.ContainerAnchorProvider = () => anchor;
        try
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                Measure("open", cycle, () => window.ShowWithTransition(anchor), () => window.CanNavigatePages);
                Pump(TimeSpan.FromMilliseconds(180));
                Measure("close", cycle, window.CloseWithTransition, () => !window.IsVisible);
            }
        }
        finally
        {
            window.ContainerAnchorProvider = null;
            window.ShowWithTransition(null);
            Pump(TimeSpan.FromMilliseconds(100));
        }
        File.WriteAllText(Path.Combine(evidence, "transition-performance.json"), JsonSerializer.Serialize(new
        {
            note = "WPF Rendering cadence, unique composition timestamps; not GPU-presented FPS. Cycle 0 includes cold setup. No system input injection.",
            hardwareShader3 = RenderCapability.IsPixelShaderVersionSupported(3, 0),
            samples,
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Measure(string direction, int cycle, Action start, Func<bool> complete)
        {
            var gaps = new List<double>();
            var lastComposition = TimeSpan.MinValue;
            long previous = 0;
            var frames = 0;
            EventHandler sample = (_, args) =>
            {
                if (args is not RenderingEventArgs rendering || rendering.RenderingTime == lastComposition) return;
                lastComposition = rendering.RenderingTime;
                if (!window.IsVisible || window.CanNavigatePages) return;
                var now = Stopwatch.GetTimestamp();
                if (previous != 0) gaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now;
                frames++;
            };
            CompositionTarget.Rendering += sample;
            var watch = Stopwatch.StartNew();
            try
            {
                start();
                var dispatchMs = watch.Elapsed.TotalMilliseconds;
                var completed = PumpUntil(complete, TimeSpan.FromSeconds(2));
                gaps.Sort();
                samples.Add(new
                {
                    direction, cycle, completed, dispatchMs, elapsedMs = watch.Elapsed.TotalMilliseconds, frames,
                    medianGapMs = Percentile(.5), p95GapMs = Percentile(.95),
                    maxGapMs = gaps.Count == 0 ? 0 : gaps[^1],
                });
            }
            finally { CompositionTarget.Rendering -= sample; }
            double Percentile(double p) => gaps.Count == 0 ? 0 : gaps[(int)Math.Ceiling(p * gaps.Count) - 1];
        }
    }

    private static void VerifyRepeatedPreparation(LauncherWindow window)
    {
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = (AppServiceHost)typeof(LauncherWindow).GetField("_services", fields)!.GetValue(window)!;
        var view = (LauncherViewState)typeof(LauncherWindow).GetField("_view", fields)!.GetValue(window)!;
        var settings = (GlassDesk.Core.Models.Settings.LauncherSettings)typeof(LauncherWindow)
            .GetField("_settings", fields)!.GetValue(window)!;
        var originalGroup = view.OpenGroupId;
        var originalPage = view.Page;
        var originalLayout = LauncherLayoutRules.DeepClone(host.Layouts.Launcher);
        var originalRemember = settings.RememberLastPage;
        var grid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("TileGrid")!;
        var groupItems = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("GroupItems")!;
        var panel = (FrameworkElement)window.FindName("LauncherPanelRoot")!;
        var originalHeight = panel.Height;
        var entry = host.Library.Find("wpf-00")!;
        var originalName = entry.DisplayName;
        window.Hide();
        try
        {
            settings.RememberLastPage = true;
            window.PrepareToShow();
            var first = grid.Children.OfType<Button>().ToArray();
            window.PrepareToShow();
            Check("重复准备同一启动台视图复用真实图标控件", "performance/warm-prepare",
                first.Length > 0 && first.SequenceEqual(grid.Children.OfType<Button>()),
                "库/布局/页面/尺寸未变时同一批Button引用", $"图标数 {first.Length}");

            window.RefreshLibrary();
            Check("显式刷新仍重新生成图标控件", "performance/warm-prepare",
                !first.SequenceEqual(grid.Children.OfType<Button>()),
                "公开RefreshLibrary强制刷新", "已核对Button对象引用");

            first = grid.Children.OfType<Button>().ToArray();
            entry.DisplayName = "Warm cache changed title";
            var updated = host.Library.Update(entry);
            window.PrepareToShow();
            var changed = grid.Children.OfType<Button>().Single(button => Equals(button.Tag, entry.Id));
            var displayed = ((StackPanel)changed.Content).Children.OfType<TextBlock>().Single().Text;
            Check("应用库版本变化重建控件并显示新名称", "performance/warm-prepare",
                updated && !first.SequenceEqual(grid.Children.OfType<Button>()) && displayed.Contains(entry.DisplayName),
                "新Button和更新后的应用名称", displayed);
            entry.DisplayName = originalName;
            host.Library.Update(entry);
            window.PrepareToShow();

            first = grid.Children.OfType<Button>().ToArray();
            var reordered = LauncherLayoutEditor.Move(host.Layouts.Launcher, "wpf-00", 1, 1, view.Capacity);
            var committed = host.Layouts.CommitLauncher(reordered, host.Layouts.LauncherVersion);
            window.PrepareToShow();
            Check("布局版本变化重建控件并显示新顺序", "performance/warm-prepare",
                committed && !first.SequenceEqual(grid.Children.OfType<Button>()) && Equals(grid.Children[0].GetValue(FrameworkElement.TagProperty), "wpf-01"),
                "新的首项wpf-01", ((Button)grid.Children[0]).Tag?.ToString() ?? "无首项");
            host.Layouts.CommitLauncher(originalLayout, host.Layouts.LauncherVersion);
            window.PrepareToShow();

            first = grid.Children.OfType<Button>().ToArray();
            view.MoveToPage(1);
            window.PrepareToShow();
            Check("准备时页面变化重新呈现末页", "performance/warm-prepare",
                !first.SequenceEqual(grid.Children.OfType<Button>()) && grid.Children.Count == 1 && Equals(((Button)grid.Children[0]).Tag, "wpf-35"),
                "末页1项wpf-35", $"页面 {view.Page}；图标数 {grid.Children.Count}");
            settings.RememberLastPage = false;
            window.PrepareToShow();
            Check("不记住末页时准备重新呈现首页", "performance/warm-prepare",
                view.Page == 0 && grid.Children.Count == 35 && Equals(((Button)grid.Children[0]).Tag, "wpf-00"),
                "首页35项且首项wpf-00", $"页面 {view.Page}；图标数 {grid.Children.Count}");
            settings.RememberLastPage = true;

            first = grid.Children.OfType<Button>().ToArray();
            panel.Height = originalHeight + 16;
            window.PrepareToShow();
            Check("准备时面板尺寸变化重建图标控件", "performance/warm-prepare",
                !first.SequenceEqual(grid.Children.OfType<Button>()),
                "尺寸变化使旧控件失效", "已核对Button对象引用");
            panel.Height = originalHeight;
            window.PrepareToShow();

            var grouped = LauncherLayoutEditor.CreateGroup(host.Layouts.Launcher, "wpf-00", "wpf-01", "warm-cache-group", view.Capacity);
            host.Layouts.CommitLauncher(grouped, host.Layouts.LauncherVersion);
            window.PrepareToShow();
            grid.Children.OfType<Button>().Single(button => Equals(button.Tag, "warm-cache-group"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.PrepareToShow();
            var groupButtons = groupItems.Children.OfType<Button>().ToArray();
            first = grid.Children.OfType<Button>().ToArray();
            window.PrepareToShow();
            Check("重复准备保留分组成员和顶层控件引用", "performance/warm-prepare",
                groupButtons.Length == 2 && groupButtons.SequenceEqual(groupItems.Children.OfType<Button>()) &&
                first.SequenceEqual(grid.Children.OfType<Button>()),
                "同一分组的2个成员和顶层Button均复用", $"分组成员 {groupButtons.Length}");
        }
        finally
        {
            entry.DisplayName = originalName;
            host.Library.Update(entry);
            host.Layouts.CommitLauncher(originalLayout, host.Layouts.LauncherVersion);
            settings.RememberLastPage = originalRemember;
            view.OpenGroupId = originalGroup;
            view.MoveToPage(originalPage);
            panel.Height = originalHeight;
            window.RefreshLibrary();
            window.Show();
        }
    }
}
