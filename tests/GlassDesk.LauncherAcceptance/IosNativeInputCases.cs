using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;

internal static partial class Program
{
    private static void VerifyIosNativeInput(LauncherWindow window, UniformGrid grid, GlassDeskDataStore data, string evidence)
    {
        window.Activate();
        Check("真实输入测试开始时修饰键处于松开状态", "IOS-INPUT", System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None,
            "无修饰键", System.Windows.Input.Keyboard.Modifiers.ToString());
        var canvas = (Canvas)window.FindName("DragOverlay")!;
        var bytes = File.ReadAllBytes(data.Layouts.LauncherFilePath);
        var timers = new List<DispatcherTimer>();
        var cellPoints = Enumerable.Range(0, 35).Select(index => grid.PointToScreen(new Point(
            grid.ActualWidth / 7 * ((index % 7) + .5), grid.ActualHeight / 5 * ((index / 7) + .5)))).ToArray();
        var sortingPoints = Enumerable.Range(0, 35).Select(index => grid.PointToScreen(new Point(
            grid.ActualWidth / 7 * ((index % 7) + .5), grid.ActualHeight / 5 * ((index / 7) + .85)))).ToArray();
        void At(int ms, Action action)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); action(); };
            timers.Add(timer);
            timer.Start();
        }
        void MoveToCell(int index)
        {
            var p = cellPoints[index];
            IosInput.SetCursorPos((int)p.X, (int)p.Y);
        }
        void MoveToSortingArea(int index)
        {
            // Convert the complete DIP position, including the edge offset, to
            // physical pixels. Mixing a DIP offset into screen pixels lands in
            // the folder center instead on a scaled Windows desktop.
            var p = sortingPoints[index];
            IosInput.SetCursorPos((int)p.X, (int)p.Y);
        }
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        var hotkeys = 0;
        HwndSourceHook hook = (nint _, int message, nint wParam, nint _, ref bool handled) =>
        {
            if (message == 0x0312 && wParam.ToInt32() == 0x7717)
            { hotkeys++; window.NavigatePage(1); handled = true; }
            return 0;
        };
        source.AddHook(hook);
        var registered = IosInput.RegisterHotKey(hwnd, 0x7717, 0x4000 | 2 | 4, 0x7e); // Ctrl+Shift+F15
        Check("拖动测试快捷键注册", "IOS-INPUT", registered, "成功注册", registered.ToString());
        var done = false;
        try
        {
            MoveToCell(0);
            IosInput.Mouse(2);
            var inputScript = Task.Run(async () =>
            {
                await Task.Delay(550); MoveToSortingArea(3);
                await Task.Delay(550); // Include the sorting dwell and its settle animation.
                IosInput.Key(0x11, false); IosInput.Key(0x10, false); IosInput.Key(0x7e, false);
                IosInput.Key(0x7e, true); IosInput.Key(0x10, true); IosInput.Key(0x11, true);
                await Task.Delay(400); IosInput.Key(0x12, false); MoveToCell(0);
                await Task.Delay(450); IosInput.Mouse(4);
                await Task.Delay(250); IosInput.Key(0x12, true);
            });
            At(200, () => Check("短按阶段不提前拖动", "IOS-INPUT", canvas.Children.Count == 0, "尚未拾起", canvas.Children.Count.ToString()));
            At(470, () => Check("真实鼠标长按拾起浮动图标", "IOS-INPUT", canvas.Children.Count == 1, "浮动图标一项", canvas.Children.Count.ToString()));
            At(1030, () =>
            {
                var visibleOrder = VisualTileOrder(grid);
                Check("真实拖动在图标边缘停留后实时让位且不写盘", "IOS-INPUT", Equals(visibleOrder[3].Tag, "wpf-00")
                    && Equals(visibleOrder[0].Tag, "wpf-01") && bytes.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                    "01、02、03、00，文件未变化", string.Join(",", visibleOrder.Take(4).Select(b => b.Tag)) + "; modifiers=" + System.Windows.Input.Keyboard.Modifiers);
                Capture(window, Path.Combine(evidence, "native-live-insertion.png"));
            });
            At(1650, () =>
            {
                Check("真实快捷键在拖动中翻页并交换", "IOS-INPUT", hotkeys == 1 && grid.Children.Count == 1
                    && Equals(((Button)grid.Children[0]).Tag, "wpf-00"), "第二页一项，被拖动图标，快捷键触发一次", $"快捷键 {hotkeys}；图标 {grid.Children.Count}");
                Capture(window, Path.Combine(evidence, "native-cross-page-preview.png"));
            });
            At(2350, () => done = true);
            PumpUntil(() => done, TimeSpan.FromSeconds(5));
            inputScript.GetAwaiter().GetResult();
            var order = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("真实松手交换保存且图标吸附", "IOS-INPUT", order[0] == "wpf-35" && order[35] == "wpf-00"
                && canvas.Children.Count == 0 && grid.Children.OfType<Button>().All(b => b.Opacity == 1),
                "35 和 00 交换，浮层清空", $"首项 {order[0]}；末项 {order[35]}；浮层 {canvas.Children.Count}");
            Capture(window, Path.Combine(evidence, "native-snapped.png"));

            // Escape must discard the draft while the primary button is still held.
            window.NavigatePage(-1);
            Pump(TimeSpan.FromMilliseconds(300));
            var beforeCancel = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            done = false;
            MoveToCell(0); IosInput.Mouse(2);
            var cancelScript = Task.Run(async () =>
            {
                await Task.Delay(500); MoveToCell(4);
                await Task.Delay(350); IosInput.Key(0x1b, false); IosInput.Key(0x1b, true);
                await Task.Delay(250); IosInput.Mouse(4);
            });
            At(1300, () => done = true);
            PumpUntil(() => done, TimeSpan.FromSeconds(4));
            cancelScript.GetAwaiter().GetResult();
            Check("真实 Esc 取消拖动恢复排列", "IOS-INPUT", beforeCancel.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))
                && Equals(((Button)grid.Children[0]).Tag, "wpf-35") && canvas.Children.Count == 0,
                "原排列和文件恢复", ((Button)grid.Children[0]).Tag?.ToString() ?? "空");
        }
        finally
        {
            foreach (var timer in timers) timer.Stop();
            IosInput.Mouse(4);
            foreach (var key in new[] { 0x11, 0x10, 0x12, 0x7e, 0x1b }) IosInput.Key((ushort)key, true);
            if (registered) IosInput.UnregisterHotKey(hwnd, 0x7717);
            source.RemoveHook(hook);
        }
    }

    private static class IosInput
    {
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort VirtualKey;
            [FieldOffset(12)] public uint KeyFlags;
            [FieldOffset(8)] public int MouseX;
            [FieldOffset(12)] public int MouseY;
            [FieldOffset(20)] public uint MouseFlags;
        }
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);
        public static void MovePacket(Point p)
        {
            var x = GetSystemMetrics(76); var y = GetSystemMetrics(77);
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            var input = new Input { MouseX = (int)((p.X - x) * 65535 / (width - 1)),
                MouseY = (int)((p.Y - y) * 65535 / (height - 1)), MouseFlags = 0x8000 | 0x4000 | 0x2000 | 1 };
            if (SendInput(1, [input], 40) != 1) throw new InvalidOperationException("SendInput movement packet was rejected.");
        }
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
        public static void Mouse(uint flags) => SendInput(1, [new Input { MouseFlags = flags }], 40);
        public static void Key(ushort key, bool up) => SendInput(1, [new Input { Type = 1, VirtualKey = key, KeyFlags = up ? 2u : 0 }], 40);
    }
}
