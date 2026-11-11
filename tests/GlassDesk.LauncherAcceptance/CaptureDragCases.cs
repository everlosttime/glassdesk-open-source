using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Core.Models;
using GlassDesk.Launcher;

internal static partial class Program
{
    private static void VerifyCaptureDragInput(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        var origin = typeof(LauncherWindow).GetField("_dragOrigin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var moves = 0;
        MouseEventHandler moving = (_, _) => { if (origin.GetValue(window) is not null) moves++; };
        window.AddHandler(Mouse.PreviewMouseMoveEvent, moving, true);
        try { VerifyIosNativeInput(window, grid, data, evidence); }
        finally { window.RemoveHandler(Mouse.PreviewMouseMoveEvent, moving); }

        window.Activate();
        var before = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var overlay = (Canvas)window.FindName("DragOverlay")!;
        var starts = 0; var finishes = 0; var interrupted = false;
        EventHandler started = (_, _) => starts++;
        EventHandler finished = (_, _) => finishes++;
        window.InternalDragStarted += started;
        window.InternalDragFinished += finished;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            interrupted = overlay.Children.OfType<Button>().Count() == 1;
            Mouse.Capture(null);
        };
        var point = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        var target = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 10));
        IosInput.SetCursorPos((int)point.X, (int)point.Y);
        IosInput.Mouse(2);
        timer.Start();
        var script = Task.Run(async () =>
        {
            await Task.Delay(430);
            for (var i = 0; i < 8; i++)
            {
                IosInput.SetCursorPos((int)(i % 2 == 0 ? target.X : point.X), (int)point.Y);
                await Task.Delay(20);
            }
            IosInput.SetCursorPos((int)target.X, (int)target.Y);
            await Task.Delay(450); IosInput.Mouse(4);
        });
        moves = 0;
        window.AddHandler(Mouse.PreviewMouseMoveEvent, moving, true);
        try
        {
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(4));
            Pump(TimeSpan.FromMilliseconds(250));
            Check("连续本地移动仍派发普通鼠标输入", "regression/capture-input", moves >= 5,
                "8次移动中至少5次正常派发", moves.ToString());
            Check("捕获被打断后撤销草稿且不保存", "regression/capture-loss",
                interrupted && before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)) && overlay.Children.Count == 0,
                "排列文件不变，浮层清空", $"interrupted={interrupted},unchanged={before.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))},ghost={overlay.Children.Count}");
            Check("捕获中断只结束一次拖动会话", "regression/capture-loss", starts == 1 && finishes == 1,
                "start=1,finish=1", $"start={starts},finish={finishes}");
        }
        finally
        {
            timer.Stop(); IosInput.Mouse(4);
            window.RemoveHandler(Mouse.PreviewMouseMoveEvent, moving);
            window.InternalDragStarted -= started; window.InternalDragFinished -= finished;
        }
        VerifyCaptureWindowTransfer(window, grid, data);
        VerifyCaptureHide(window, grid, data);
        VerifyCaptureTrash(window, grid, data);
    }

    private static void VerifyCaptureWindowTransfer(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data)
    {
        var receiver = new Window { Width = 110, Height = 80, Left = window.Left + window.Width - 120,
            Top = window.Top + 70, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, AllowDrop = true, Content = new Border { Background = System.Windows.Media.Brushes.SteelBlue } };
        var overs = 0; string? dropped = null;
        receiver.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)) { overs++; e.Effects = DragDropEffects.Copy; }
            e.Handled = true;
        };
        receiver.Drop += (_, e) =>
        {
            dropped = e.Data.GetData(LauncherDragContract.SlotDataFormat) as string;
            e.Effects = DragDropEffects.Copy; e.Handled = true;
        };
        receiver.Show(); Pump(TimeSpan.FromMilliseconds(150)); window.Activate();
        var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        var local = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 10));
        var outside = receiver.PointToScreen(new Point(55, 40));
        var source = (string)((Button)grid.Children[0]).Tag;
        var recaptured = false;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) => { timer.Stop(); recaptured = ReferenceEquals(Mouse.Captured, window); };
        void Move(Point p) => IosInput.SetCursorPos((int)p.X, (int)p.Y);
        try
        {
            Move(start); IosInput.Mouse(2); timer.Start();
            var returnScript = Task.Run(async () =>
            {
                await Task.Delay(500); Move(outside);
                await Task.Delay(350); Move(start);
                await Task.Delay(350); Move(local);
                await Task.Delay(350); IosInput.Mouse(4);
            });
            PumpUntil(() => returnScript.IsCompleted, TimeSpan.FromSeconds(5)); Pump(TimeSpan.FromMilliseconds(300));
            Check("跨窗口拖动保留标准OLE数据载荷", "regression/capture-transfer", overs > 0,
                "目标窗口收到启动台SlotDataFormat", overs.ToString());
            Check("未松手返回启动台恢复捕获且正常保存", "regression/capture-transfer",
                recaptured && LauncherLayoutRules.AppIds(data.Layouts.Launcher)[3] == source,
                "返回后恢复本地捕获，最终落在第4格", $"recaptured={recaptured},slot3={LauncherLayoutRules.AppIds(data.Layouts.Launcher)[3]}");
            var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            source = (string)((Button)grid.Children[0]).Tag;
            Move(start); IosInput.Mouse(2);
            var dropScript = Task.Run(async () =>
            {
                await Task.Delay(500); Move(outside);
                await Task.Delay(400); IosInput.Mouse(4);
            });
            PumpUntil(() => dropScript.IsCompleted, TimeSpan.FromSeconds(5)); Pump(TimeSpan.FromMilliseconds(250));
            Check("外部窗口接收松手且启动台草稿不写盘", "regression/capture-transfer",
                dropped == source && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)) && Mouse.Captured is null,
                "外部窗口收到正确Id，启动台布局不变，捕获释放", $"payload={dropped},source={source},unchanged={bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))},capture={Mouse.Captured}");
        }
        finally { timer.Stop(); IosInput.Mouse(4); receiver.Close(); }
    }

    private static void VerifyCaptureHide(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data)
    {
        var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var starts = 0; var finishes = 0; var picked = false;
        EventHandler started = (_, _) => starts++;
        EventHandler finished = (_, _) => finishes++;
        window.InternalDragStarted += started; window.InternalDragFinished += finished;
        var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        var target = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 10));
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) => { timer.Stop(); picked = ((Canvas)window.FindName("DragOverlay")!).Children.OfType<Button>().Count() == 1; window.Hide(); };
        IosInput.SetCursorPos((int)start.X, (int)start.Y); IosInput.Mouse(2); timer.Start();
        var script = Task.Run(async () =>
        {
            await Task.Delay(500); IosInput.SetCursorPos((int)target.X, (int)target.Y);
            await Task.Delay(550); IosInput.Mouse(4);
        });
        try
        {
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(4));
            Check("窗口隐藏撤销草稿释放捕获并结束一次", "regression/capture-hide",
                picked && starts == 1 && finishes == 1 && Mouse.Captured is null
                && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))
                && ((Canvas)window.FindName("DragOverlay")!).Children.Count == 0,
                "捕获、浮层清空，布局不变，start/finish各一次", $"picked={picked},start={starts},finish={finishes},capture={Mouse.Captured},unchanged={bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))}");
        }
        finally
        {
            timer.Stop(); IosInput.Mouse(4);
            window.InternalDragStarted -= started; window.InternalDragFinished -= finished;
            window.Show(); window.Activate(); Pump(TimeSpan.FromMilliseconds(200));
        }
    }

    private static void VerifyCaptureTrash(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data)
    {
        var source = (string)((Button)grid.Children[0]).Tag;
        var start = grid.PointToScreen(new Point(grid.ActualWidth / 14, grid.ActualHeight / 10));
        Point target = default; var ready = false;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); var trash = (FrameworkElement)window.FindName("TrashDropTarget")!;
            target = trash.PointToScreen(new Point(trash.ActualWidth / 2, trash.ActualHeight / 2));
            Volatile.Write(ref ready, true);
        };
        IosInput.SetCursorPos((int)start.X, (int)start.Y); IosInput.Mouse(2); timer.Start();
        var script = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; !Volatile.Read(ref ready) && i < 40; i++) await Task.Delay(50);
                if (Volatile.Read(ref ready)) IosInput.SetCursorPos((int)target.X, (int)target.Y);
                await Task.Delay(300);
            }
            finally { IosInput.Mouse(4); }
        });
        try
        {
            PumpUntil(() => script.IsCompleted, TimeSpan.FromSeconds(4)); Pump(TimeSpan.FromMilliseconds(250));
            Check("捕获拖到移除区只移除启动台快捷方式", "regression/capture-trash",
                ready && !LauncherLayoutRules.AppIds(data.Layouts.Launcher).Contains(source)
                && data.Library.Find(source) is not null && Mouse.Captured is null,
                "启动台移除，应用库保留，捕获释放", $"ready={ready},layoutContains={LauncherLayoutRules.AppIds(data.Layouts.Launcher).Contains(source)},libraryContains={data.Library.Find(source) is not null},capture={Mouse.Captured}");
        }
        finally { timer.Stop(); IosInput.Mouse(4); }
    }
}
