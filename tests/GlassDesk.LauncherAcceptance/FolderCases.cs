using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyFolderInteractions(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence, string? iconsDirectory)
    {
        data.Layouts.CommitLauncher(LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher,
            data.Library.Items.Select(e => e.Id), 35), data.Layouts.LauncherVersion);
        var icons = iconsDirectory is not null && Directory.Exists(iconsDirectory)
            ? Directory.GetFiles(iconsDirectory, "*.png").Take(36).ToArray() : [];
        for (var i = 0; i < icons.Length; i++) data.Library.Find($"wpf-{i:00}")!.CustomIconPath = icons[i];
        window.RefreshLibrary(); window.Activate(); Pump(TimeSpan.FromMilliseconds(1000));
        var stamps = new List<double>();
        TimeSpan? last = null;
        EventHandler measure = (_, e) =>
        {
            if (e is not RenderingEventArgs frame || frame.RenderingTime == last) return;
            if (last is { } previous) stamps.Add((frame.RenderingTime - previous).TotalMilliseconds);
            last = frame.RenderingTime;
        };
        CompositionTarget.Rendering += measure;
        Button Tile(string id) => grid.Children.OfType<Button>().First(t => Equals(t.Tag, id));
        Point Center(Button tile)
        {
            var host = (FrameworkElement)tile.GetType().GetProperty("IconHost", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tile)!;
            return host.PointToScreen(new Point(host.ActualWidth / 2, host.ActualHeight / 2));
        }
        void Move(Point p) => IosInput.SetCursorPos((int)p.X, (int)p.Y);
        void Drag(Button from, Point target, int hover, string? snapshot = null, Button? revisitTarget = null, Action? beforeDrop = null, bool verifyLanding = false)
        {
            var start = Center(from); Move(start); IosInput.Mouse(2); Pump(TimeSpan.FromMilliseconds(440));
            File.AppendAllText(Path.Combine(evidence, "folder-input-trace.log"),
                $"{DateTimeOffset.Now:O} source={from.Tag} active={window.IsActive} mouse={System.Windows.Input.Mouse.LeftButton} captured={System.Windows.Input.Mouse.Captured is not null} logical={typeof(LauncherWindow).GetField("_logicalIconDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)}\n");
            for (var i = 1; i <= 8; i++)
            { Move(new Point(start.X + (target.X - start.X) * i / 8, start.Y + (target.Y - start.Y) * i / 8)); Pump(TimeSpan.FromMilliseconds(16)); }
            if (revisitTarget is not null)
            {
                Pump(TimeSpan.FromMilliseconds(100));
                var draft = (LauncherEditSession)typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var displacement = (Center(revisitTarget) - target).Length;
                Check("中心悬停确认前不抢先挤走文件夹目标", "FOLDER-18",
                    displacement < 1 && LauncherLayoutRules.Equivalent(data.Layouts.Launcher, draft.Draft),
                    "目标保持原位且排序草稿不变化", $"displacement={displacement:F1}");
                // Keep holding the mouse while leaving a displaced icon, then
                // approach its new visible position, as in the user's recording.
                Move(grid.PointToScreen(new Point(4, 4))); Pump(TimeSpan.FromMilliseconds(300));
                var visibleCenter = Center(revisitTarget);
                Move(visibleCenter); Pump(TimeSpan.FromMilliseconds(65));
                var anchor = (Rect)typeof(LauncherWindow).GetField("_folderHoverBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var overlay = (Canvas)window.FindName("DragOverlay");
                var actualAnchor = overlay.PointToScreen(new Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2));
                var anchorError = (actualAnchor - visibleCenter).Length;
                Check("挤位后移开再靠近使用当前图标位置", "FOLDER-12", anchorError < 8,
                    "悬停锚点不回退旧格子，偏差 < 8px", anchorError.ToString("F1"));
            }
            Pump(TimeSpan.FromMilliseconds(hover));
            if (snapshot is not null)
            {
                var targetTile = (Button?)typeof(LauncherWindow).GetField("_folderHoverTile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var ghost = (Button?)typeof(LauncherWindow).GetField("_dragGhost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var overlay = (Canvas)window.FindName("DragOverlay");
                var targetHost = (FrameworkElement?)targetTile?.GetType().GetProperty("IconHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targetTile);
                var motion = (TranslateTransform?)typeof(LauncherWindow).GetField("_dragGhostMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var grab = (Point)typeof(LauncherWindow).GetField("_grabPoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var pointer = DisplayService.GetCursorPixelPoint();
                var error = double.MaxValue;
                if (pointer is { } pixels && ghost is not null && motion is not null)
                {
                    var mouse = overlay.PointFromScreen(new Point(pixels.X, pixels.Y));
                    error = (new Point(Canvas.GetLeft(ghost) + motion.X, Canvas.GetTop(ghost) + motion.Y) - (mouse - (Vector)grab)).Length;
                }
                var translation = targetHost?.RenderTransform.Value ?? Matrix.Identity;
                Check("确认阶段目标无磁吸且拖动图标保持跟随鼠标", "FOLDER-11",
                    targetHost?.Opacity == 1 && Math.Abs(translation.OffsetX) < .01 && Math.Abs(translation.OffsetY) < .01
                        && error < 1 && overlay.Children.Cast<UIElement>().All(c => c is Button or SharedGlassLens),
                    "目标原图正常显示且无吸附位移、无浮动副本、鼠标偏差 < 1 DIP", $"opacity={targetHost?.Opacity:F1}, pointerError={error:F2}");
                Capture(window, Path.Combine(evidence, snapshot));
            }
            beforeDrop?.Invoke();
            if (!verifyLanding) { IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(420)); }
            else
            {
                var ghost = (Button)typeof(LauncherWindow).GetField("_dragGhost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var motion = (TranslateTransform)typeof(LauncherWindow).GetField("_dragGhostMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var overlay = (Canvas)window.FindName("DragOverlay");
                Point Position() => new(Canvas.GetLeft(ghost) + motion.X, Canvas.GetTop(ghost) + motion.Y);
                var released = Position(); var points = new List<Point>();
                EventHandler sample = (_, _) =>
                {
                    if (overlay.Children.Contains(ghost)) points.Add(Position());
                };
                CompositionTarget.Rendering += sample;
                try { IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(420)); }
                finally { CompositionTarget.Rendering -= sample; }
                var end = Tile((string)from.Tag).TranslatePoint(new Point(), overlay);
                var path = end - released;
                var maximumError = points.Count == 0 ? double.MaxValue : points.Max(p =>
                {
                    var t = path.LengthSquared < .01 ? 0 : Math.Clamp(Vector.Multiply(p - released, path) / path.LengthSquared, 0, 1);
                    return (p - (released + path * t)).Length;
                });
                Check("拖出后松手直接回到真实落点而不经过左上角", "FOLDER-17", points.Count >= 2 && maximumError < 4,
                    "动画沿释放点到实际格子的路径，误差 < 4 DIP", $"samples={points.Count}, deviation={maximumError:F1}");
                File.WriteAllText(Path.Combine(evidence, "member-landing-path.json"), JsonSerializer.Serialize(new
                { start = new { released.X, released.Y }, end = new { end.X, end.Y }, maximumError,
                    points = points.Select(p => new { p.X, p.Y }).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        List<LauncherSlot> Slots() => data.Layouts.Launcher.Pages.SelectMany(p => p.Slots).ToList();
        void Click(Button button)
        { Move(Center(button)); IosInput.Mouse(2); IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(650)); }
        void CheckHeldMember(string id, string caseId)
        {
            var placeholder = Tile(id);
            var overlay = (Canvas)window.FindName("DragOverlay");
            var ghosts = overlay.Children.OfType<Button>().Where(t => Equals(t.Tag, id) && t.Opacity > .01).Count();
            var closed = !((Grid)window.FindName("GroupPanel")).IsVisible;
            Check("拖出后持续按住至文件夹收起只显示一个拖动图标", caseId,
                closed && placeholder.Opacity == 0 && !placeholder.IsHitTestVisible && ghosts == 1,
                "面板已收起，原位隐藏，浮动图标只有一个",
                $"closed={closed}, sourceOpacity={placeholder.Opacity:F1}, sourceHit={placeholder.IsHitTestVisible}, ghosts={ghosts}");
            Capture(window, Path.Combine(evidence, $"member-held-{caseId}.png"));
        }
        try
        {
            Drag(Tile("wpf-00"), Center(Tile("wpf-01")), 100);
            Check("快速拖过仍排序而不创建文件夹", "FOLDER-01", Slots().All(s => s.Group is null)
                && Slots()[0].ItemId == "wpf-01", "排序且没有文件夹", string.Join(',', Slots().Take(3).Select(s => s.ItemId)));
            Drag(Tile("wpf-00"), Center(Tile("wpf-02")), 700, "fusion-preview-live.png", Tile("wpf-02"));
            var folder = Slots().FirstOrDefault(s => s.Group is not null)?.Group;
            Check("原生长按悬停松手创建文件夹", "FOLDER-02", folder?.ItemIds.SequenceEqual(["wpf-02", "wpf-00"]) == true,
                "02、00 合并且只出现一次", folder is null ? "没有创建文件夹" : string.Join(',', folder.ItemIds));
            if (folder is null) return;
            Capture(window, Path.Combine(evidence, "folder-created-live.png"));
            Drag(Tile("wpf-03"), Center(Tile(folder.Id)), 700);
            folder = Slots().First(s => s.Group is not null).Group!;
            Check("原生拖动进入已有文件夹", "FOLDER-03", folder.ItemIds.Count == 3,
                "3 个成员", folder.ItemIds.Count.ToString());
            Click(Tile(folder.Id));
            var items = (UniformGrid)window.FindName("GroupItems")!;
            Check("从原始位置展开且成员可交互", "FOLDER-04", items.Children.Count == 3 && ((Grid)window.FindName("GroupPanel")!).IsVisible,
                "3 个成员可见", items.Children.Count.ToString());
            Capture(window, Path.Combine(evidence, "folder-open-live.png"));
            var source = items.Children.OfType<Button>().First();
            Drag(source, Center(items.Children.OfType<Button>().Last()), 180);
            folder = Slots().First(s => s.Group is not null).Group!;
            Check("原生文件夹内部拖动重排", "FOLDER-05", folder.ItemIds.Last() == "wpf-02", "02 移到末尾", string.Join(',', folder.ItemIds));
            items = (UniformGrid)window.FindName("GroupItems")!;
            var outside = grid.PointToScreen(new Point(14, grid.ActualHeight - 14));
            Drag(items.Children.OfType<Button>().First(), outside, 1100, beforeDrop: () =>
            {
                CheckHeldMember("wpf-00", "FOLDER-13");
                var crossed = Tile("wpf-04");
                Move(Center(crossed)); Pump(TimeSpan.FromMilliseconds(120));
                Move(outside); Pump(TimeSpan.FromMilliseconds(300));
                var host = (FrameworkElement)crossed.GetType().GetProperty("IconHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(crossed)!;
                var transform = host.RenderTransform.Value;
                var displacement = new Vector(transform.OffsetX, transform.OffsetY).Length;
                var target = typeof(LauncherWindow).GetField("_folderHoverTile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Check("拖出后快速经过其他图标再移开不产生吸附", "FOLDER-16", target is null && host.Opacity == 1 && displacement < 1,
                    "悬停已解除，目标显示且没有吸附位移", $"hover={target is not null}, opacity={host.Opacity:F1}, displacement={displacement:F1}");
                Capture(window, Path.Combine(evidence, "folder-exit-crossed-live.png"));
            }, verifyLanding: true);
            Check("原生拖出恢复为顶层图标", "FOLDER-06", Slots().First(s => s.Group is not null).Group!.ItemIds.Count == 2
                && Slots().Any(s => s.ItemId == "wpf-00"), "文件夹剩 2 个成员且顶层有 00", string.Join(',', Slots().Where(s => s.ItemId is not null).Take(5).Select(s => s.ItemId)));
            folder = Slots().First(s => s.Group is not null).Group!;
            Click(Tile(folder.Id));
            IosInput.Key(0x1b, false); IosInput.Key(0x1b, true); Pump(TimeSpan.FromMilliseconds(700));
            Check("Esc 反向收回且启动台保持打开", "FOLDER-07", window.IsVisible && !((Grid)window.FindName("GroupPanel")!).IsVisible,
                "启动台打开、文件夹关闭", ((Grid)window.FindName("GroupPanel")!).Visibility.ToString());
            Click(Tile(folder.Id));
            Move(grid.PointToScreen(new Point(5, 5))); IosInput.Mouse(2); IosInput.Mouse(4); Pump(TimeSpan.FromMilliseconds(650));
            Check("外部点击收回且不打开后方应用", "FOLDER-08", !((Grid)window.FindName("GroupPanel")!).IsVisible && window.IsVisible,
                "文件夹已收回", ((Grid)window.FindName("GroupPanel")!).Visibility.ToString());
            Click(Tile(folder.Id));
            items = (UniformGrid)window.FindName("GroupItems")!;
            var extracted = items.Children.OfType<Button>().First();
            var extractedId = (string)extracted.Tag;
            Drag(extracted, outside, 1100, beforeDrop: () => CheckHeldMember(extractedId, "FOLDER-14"));
            Check("两成员文件夹拖出后自动解散且图标无重复丢失", "FOLDER-15",
                Slots().All(s => s.Group is null) && LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count() == 36
                    && LauncherLayoutRules.AppIds(data.Layouts.Launcher).Distinct().Count() == 36,
                "36 个独立应用且不再有文件夹", $"slots={Slots().Count}, groups={Slots().Count(s => s.Group is not null)}");
            var reloaded = new GlassDeskDataStore(data.Paths); reloaded.LoadAll();
            Check("原生整理后的布局重载一致", "FOLDER-09", LauncherLayoutRules.Equivalent(data.Layouts.Launcher, reloaded.Layouts.Launcher),
                "布局和文件夹成员一致", "逐项比较名称、位置、内容");
            Capture(window, Path.Combine(evidence, "folder-final-live.png"));
            var surfaces = FolderDescendants(window).OfType<LiquidGlassSurface>().Count();
            Check("文件夹复用单一采集材质", "FOLDER-10", surfaces == 1, "仍只有一个 LiquidGlassSurface", surfaces.ToString());
        }
        finally
        {
            IosInput.Mouse(4); CompositionTarget.Rendering -= measure;
            var ordered = stamps.Order().ToArray();
            File.WriteAllText(Path.Combine(evidence, "folder-render-cadence.json"), JsonSerializer.Serialize(new
            {
                callbacks = ordered.Length,
                medianMilliseconds = ordered.Length == 0 ? 0 : ordered[ordered.Length / 2],
                p95Milliseconds = ordered.Length == 0 ? 0 : ordered[(int)((ordered.Length - 1) * .95)],
                maxMilliseconds = ordered.Length == 0 ? 0 : ordered[^1],
                note = "Unique WPF RenderingTime intervals during native drags and folder animations; includes screenshot encoding and settle waits; not displayed GPU FPS."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static IEnumerable<DependencyObject> FolderDescendants(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in FolderDescendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
