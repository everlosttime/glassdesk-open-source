using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;

internal static partial class Program
{
    private static void VerifyIosDrag(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        var prepare = typeof(LauncherWindow).GetMethod("PrepareIconDrag", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("实时拖拽会话可拾起图标", "IOS-01", prepare is not null, "支持实时拖拽", prepare is null ? "缺少实现" : "已实现");
        if (prepare is null) return;
        var preview = typeof(LauncherWindow).GetMethod("PreviewIconPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var finish = typeof(LauncherWindow).GetMethod("FinishIconDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var tile = (Button)grid.Children[0];
        var reused = (Button)grid.Children[1];
        prepare.Invoke(window, [tile, new Point(tile.ActualWidth / 2, tile.ActualHeight / 2)]);
        var target = new Point(grid.ActualWidth / 7 * 3.5, grid.ActualHeight / 5 * .5);
        preview.Invoke(window, [target, false]);
        Check("拖动预览不写盘", "IOS-03", original.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)), "文件未变化", "核对字节");
        var displacementStart = reused.TranslatePoint(new Point(0, 0), grid);
        Pump(TimeSpan.FromMilliseconds(70));
        var displacementMiddle = reused.TranslatePoint(new Point(0, 0), grid);
        Check("让位具有真实位移动画", "IOS-04", (displacementMiddle - displacementStart).Length > 1,
            "图标逐帧移动超过1 DIP", (displacementMiddle - displacementStart).Length.ToString());
        Pump(TimeSpan.FromMilliseconds(280));
        var visibleOrder = VisualTileOrder(grid);
        Check("松手前图标已经依次让位", "IOS-02", Equals(visibleOrder[0].Tag, "wpf-01")
            && Equals(visibleOrder[3].Tag, "wpf-00") && ReferenceEquals(visibleOrder[0], reused),
            "01、02、03、00，复用现有图标", string.Join(",", visibleOrder.Take(4).Select(b => b.Tag)));
        Capture(window, Path.Combine(evidence, "live-insertion.png"));
        window.NavigatePage(1);
        Pump(TimeSpan.FromMilliseconds(280));
        preview.Invoke(window, [new Point(grid.ActualWidth / 14, grid.ActualHeight / 10), true]);
        Check("快捷翻页保留拖拽并实时交换", "IOS-05", Equals(((Button)grid.Children[0]).Tag, "wpf-00"), "第二页首格为被拖动图标", ((Button)grid.Children[0]).Tag?.ToString() ?? "空");
        var payload = new DataObject("GlassDesk.Launcher.Slot", "wpf-00");
        var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [payload, DragDropKeyStates.AltKey, DragDropEffects.Move, grid, new Point(1, 1)], culture: null)!;
        drop.RoutedEvent = DragDrop.DropEvent;
        grid.RaiseEvent(drop);
        finish.Invoke(window, null);
        Pump(TimeSpan.FromMilliseconds(280));
        var saved = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
        Check("跨页交换松手后保存正确顺序", "IOS-06", saved[0] == "wpf-35" && saved[35] == "wpf-00" && saved.Distinct().Count() == 36,
            "35 与 00 交换，其余图标保持位置", $"首项 {saved[0]}，末项 {saved[35]}");
        Check("松手后清除浮动图标并显示吸附图标", "IOS-07", ((Canvas)window.FindName("DragOverlay")!).Children.Count == 0
            && ((Button)grid.Children[0]).Opacity == 1, "浮层清空且图标可见", $"浮层 {((Canvas)window.FindName("DragOverlay")!).Children.Count}");
        var savedBytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        window.NavigatePage(-1);
        Pump(TimeSpan.FromMilliseconds(280));
        prepare.Invoke(window, [(Button)grid.Children[0], new Point(20, 20)]);
        preview.Invoke(window, [target, false]);
        finish.Invoke(window, null);
        Pump(TimeSpan.FromMilliseconds(280));
        Check("取消恢复原顺序和布局文件", "IOS-08", Equals(((Button)grid.Children[0]).Tag, "wpf-35")
            && savedBytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)), "原顺序且文件不变", ((Button)grid.Children[0]).Tag?.ToString() ?? "空");
        var reloaded = new GlassDeskDataStore(data.Paths);
        reloaded.LoadAll();
        Check("重开布局保持交换结果", "IOS-09", LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher).SequenceEqual(saved), "与已保存顺序一致", "核对所有 Id");
        prepare.Invoke(window, [(Button)grid.Children[0], new Point(20, 20)]);
        window.NavigatePage(1);
        var duringSlide = false;
        try
        {
            typeof(LauncherWindow).GetMethod("PlaceIconAtPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new Point(20, 20), true, true]);
            duringSlide = Equals(((Button)grid.Children[0]).Tag, "wpf-35");
        }
        catch (TargetInvocationException) { }
        Check("翻页动画尚未结束就松手仍可吸附", "IOS-10", duringSlide, "新页出现被拖动图标", duringSlide.ToString());
        finish.Invoke(window, null);
        Capture(window, Path.Combine(evidence, "settled-grid.png"));
    }
}
