using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static void VerifyFilterGroupInput(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        var savedCursor = DisplayService.GetCursorPixelPoint();
        data.Settings.Current.Appearance.ReduceMotion = false;
        ThemeService.Current!.Update(data.Settings.Current.Appearance);
        var layout = LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher, data.Library.Items.Select(item => item.Id), 35);
        layout.FilterGroups = [
            new() { Id = "work", Name = "工作", ItemIds = ["wpf-00", "wpf-03"] },
            new() { Id = "games", Name = "游戏", ItemIds = ["wpf-05", "wpf-06"] },
            new() { Id = "media", Name = "媒体", ItemIds = ["wpf-35"] }];
        data.Layouts.CommitLauncher(layout, data.Layouts.LauncherVersion);
        data.Settings.Current.Launcher.GroupModeEnabled = true;
        window.ApplySettings(data.Settings.Current.Launcher);
        window.Activate(); Pump(TimeSpan.FromMilliseconds(250));
        var bar = (ScrollViewer)window.FindName("FilterGroupBar");
        var chips = (StackPanel)window.FindName("FilterGroupButtons");
        var overlay = (Canvas)window.FindName("DragOverlay");
        ToggleButton Chip(string id) => chips.Children.OfType<ToggleButton>().Single(chip => Equals(chip.Tag, id));
        Point Position(string id, double fraction = .5) => Chip(id).PointToScreen(new Point(Chip(id).ActualWidth * fraction, Chip(id).ActualHeight / 2));
        void Move(Point point) { IosInput.SetCursorPos((int)point.X, (int)point.Y); Pump(TimeSpan.FromMilliseconds(80)); }
        void Down(Point point) { Move(point); IosInput.Mouse(2); Pump(TimeSpan.FromMilliseconds(80)); }
        void Up() { IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(450)); }
        void Escape() { IosInput.Key(0x1b, false); IosInput.Key(0x1b, true); Pump(TimeSpan.FromMilliseconds(80)); }
        string Order() => string.Join(",", data.Layouts.Launcher.FilterGroups.Select(group => group.Id));
        try
        {
            Down(Position("work")); Up();
            Check("分组按钮真实点击仍可筛选", "IOS-INPUT", Chip("work").IsChecked == true && grid.Children.Count == 2,
                "选中工作，显示两个图标", $"checked={Chip("work").IsChecked}; icons={grid.Children.Count}");
            var before = File.ReadAllBytes(data.Paths.LauncherLayoutFile);
            Down(Position("media")); Pump(TimeSpan.FromMilliseconds(320));
            Check("真实长按让分组按钮浮起", "IOS-INPUT", overlay.Children.OfType<ToggleButton>().Count() == 1 && Chip("media").Opacity == 0,
                "浮动按钮一个，原位占位隐藏", $"ghosts={overlay.Children.OfType<ToggleButton>().Count()}; opacity={Chip("media").Opacity}");
            var workBefore = Chip("work").PointToScreen(new Point()).X;
            var dragDestination = Position("work", .1);
            var aboveDestination = new Point(dragDestination.X, dragDestination.Y - 80);
            Move(aboveDestination);
            var ghost = overlay.Children.OfType<ToggleButton>().SingleOrDefault();
            var cursor = DisplayService.GetCursorPixelPoint();
            var ghostCenter = ghost?.PointToScreen(new Point(ghost.ActualWidth / 2, ghost.ActualHeight / 2)) ?? new Point();
            var pointerError = cursor is { } pointer ? Math.Abs(ghostCenter.X - pointer.X) : double.MaxValue;
            Check("鼠标在分组栏上方时浮动分组横向跟手且实时让位不写盘", "IOS-INPUT", ghost is not null && pointerError < 1.5
                && string.Join(",", chips.Children.OfType<ToggleButton>().Select(chip => chip.Tag)) == "media,work,games"
                && before.SequenceEqual(File.ReadAllBytes(data.Paths.LauncherLayoutFile)),
                "浮动按钮跟手，预览media,work,games，磁盘原样", $"pointerError={pointerError:F2}; preview={string.Join(",", chips.Children.OfType<ToggleButton>().Select(chip => chip.Tag))}; saved={Order()}");
            var halfway = Chip("work").PointToScreen(new Point()).X;
            Pump(TimeSpan.FromMilliseconds(350));
            var settled = Chip("work").PointToScreen(new Point()).X;
            Check("周围分组通过真实渲染帧平滑移动到新位置", "IOS-INPUT", halfway > workBefore + .05 && halfway < settled - .05,
                "原位置 < 中间位置 < 最终位置", $"before={workBefore:F2}; halfway={halfway:F2}; settled={settled:F2}");
            var rowY = ghost!.PointToScreen(new Point()).Y;
            Move(new Point(dragDestination.X + 12, dragDestination.Y + 80));
            var driftY = ghost.PointToScreen(new Point()).Y;
            Check("鼠标上下偏移时浮动分组仍固定在原水平行", "IOS-INPUT", Math.Abs(rowY - driftY) < .1,
                "垂直位置不变", $"beforeY={rowY:F2}; afterY={driftY:F2}");
            Move(aboveDestination);
            Capture(window, Path.Combine(evidence, "group-drag-live-preview.png"));
            Up();
            Check("真实松手保存顺序并保留所选分组", "IOS-INPUT", Order() == "media,work,games"
                && Chip("work").IsChecked == true && grid.Children.Count == 2 && Mouse.Captured is null,
                "media,work,games；保持工作；释放捕获", $"{Order()}; work={Chip("work").IsChecked}; icons={grid.Children.Count}; captured={Mouse.Captured}");
            var reload = new GlassDeskDataStore(data.Paths); reload.LoadAll();
            Check("重新加载后保留拖动排序", "IOS-INPUT", string.Join(",", reload.Layouts.Launcher.FilterGroups.Select(group => group.Id)) == "media,work,games",
                "media,work,games", string.Join(",", reload.Layouts.Launcher.FilterGroups.Select(group => group.Id)));
            Capture(window, Path.Combine(evidence, "group-drag-saved.png"));
            Down(Position("media")); Up();
            Check("排序后真实点击仍能切换分组", "IOS-INPUT", Chip("media").IsChecked == true && grid.Children.Count == 1,
                "选中媒体，显示一个图标", $"media={Chip("media").IsChecked}; icons={grid.Children.Count}");

            before = File.ReadAllBytes(data.Paths.LauncherLayoutFile);
            Down(Position("games")); Move(Position("media", .1)); Escape(); Up();
            Check("真实 Esc 取消分组拖动且不切换分组", "IOS-INPUT", before.SequenceEqual(File.ReadAllBytes(data.Paths.LauncherLayoutFile))
                && Chip("media").IsChecked == true && Mouse.Captured is null && chips.Children.OfType<ToggleButton>().All(chip => chip.Opacity == 1),
                "顺序和媒体选择保留，释放捕获", $"{Order()}; media={Chip("media").IsChecked}; captured={Mouse.Captured}");

            Down(Position("games")); Move(bar.PointToScreen(new Point(-100, bar.ActualHeight + 60))); Up();
            Check("水平拖出分组栏松手取消排序", "IOS-INPUT", before.SequenceEqual(File.ReadAllBytes(data.Paths.LauncherLayoutFile))
                && Chip("media").IsChecked == true && Mouse.Captured is null,
                "顺序和媒体选择保留，释放捕获", $"{Order()}; captured={Mouse.Captured}");

            var many = LauncherLayoutRules.DeepClone(data.Layouts.Launcher);
            for (var i = 0; i < 20; i++) many.FilterGroups.Add(new() { Id = "g" + i, Name = "用于边缘滚动排序的分组" + i, ItemIds = ["wpf-00"] });
            data.Layouts.CommitLauncher(many, data.Layouts.LauncherVersion);
            window.RefreshLibrary(); Pump(TimeSpan.FromMilliseconds(120));
            before = File.ReadAllBytes(data.Paths.LauncherLayoutFile);
            Down(Position("media"));
            Move(bar.PointToScreen(new Point(bar.ActualWidth - 2, -80))); Pump(TimeSpan.FromMilliseconds(450));
            var rightOffset = bar.HorizontalOffset;
            Check("真实拖动在右边缘持续滚动", "IOS-INPUT", rightOffset > 100 && Mouse.Captured == window,
                "向右滚动超过100 DIP并保持捕获", $"offset={rightOffset}; captured={Mouse.Captured}");
            Capture(window, Path.Combine(evidence, "group-drag-edge-scroll.png"));
            Move(bar.PointToScreen(new Point(2, 12))); Pump(TimeSpan.FromMilliseconds(300));
            Check("真实拖动在左边缘反向滚动", "IOS-INPUT", bar.HorizontalOffset < rightOffset,
                "偏移小于向右滚动后的偏移", $"before={rightOffset}; after={bar.HorizontalOffset}");
            Escape(); Up();
            Check("边缘滚动后取消不保存且释放鼠标", "IOS-INPUT", before.SequenceEqual(File.ReadAllBytes(data.Paths.LauncherLayoutFile)) && Mouse.Captured is null,
                "磁盘原样、释放捕获", $"captured={Mouse.Captured}");

            bar.ScrollToHome(); Pump(TimeSpan.FromMilliseconds(80));
            Down(Position("media")); Move(bar.PointToScreen(new Point(bar.ActualWidth - 2, 12)));
            PumpUntil(() => bar.HorizontalOffset >= 900, TimeSpan.FromSeconds(4));
            Move(Position("g8", .9)); Up();
            Check("滚动到原本不可见的分组后可准确插入保存", "IOS-INPUT",
                Order().StartsWith("work,games,g0,g1,g2,g3,g4,g5,g6,g7,g8,media,g9,", StringComparison.Ordinal)
                    && Chip("media").IsChecked == true && Mouse.Captured is null,
                "media 插入 g8 后、g9 前，保持媒体选择并释放捕获", Order());
            Capture(window, Path.Combine(evidence, "group-drag-scrolled-insertion-saved.png"));
        }
        finally
        {
            IosInput.Mouse(4);
            IosInput.Key(0x1b, true);
            if (savedCursor is { } cursor) IosInput.SetCursorPos(cursor.X, cursor.Y);
        }
    }
}
