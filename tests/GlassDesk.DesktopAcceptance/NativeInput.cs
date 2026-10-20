using System.Runtime.InteropServices;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 系统拒绝了注入的输入。
/// </summary>
/// <remarks>
/// 单独立一个异常类型，是因为它代表<strong>环境不满足</strong>而不是产品有缺陷：
/// <c>SendInput</c> 被 UIPI（完整性级别隔离）挡住时返回 0，且官方文档明确说
/// 返回值和 <c>GetLastError</c> 都不会告诉你原因就是 UIPI。
/// 这种情况必须记成「阻塞」，不能记成「失败」。
/// </remarks>
internal sealed class InputBlockedException : Exception
{
    internal InputBlockedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 通过 <c>SendInput</c> 注入真实的鼠标与键盘输入。
/// </summary>
/// <remarks>
/// 这一段是「真实桌面输入」这条证据链的根：它和用户手动操作走的是同一条
/// 系统输入队列，因此 <c>DoDragDrop</c> 的模态拖拽循环、原生文件对话框的消息泵
/// 都会像对待真人一样对待这些事件。WPF 路由事件做不到这一点。
///
/// 注入是异步的：<c>SendInput</c> 返回时事件只是进了队列，所以每个动作后面
/// 都要留出观察时间，而「通过」的判据仍然是可观察的结果，不是调用成功。
/// </remarks>
internal static class NativeInput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint MouseWheel = 0x0800;

    private const uint KeyboardKeyUp = 0x0002;
    private const uint KeyboardUnicode = 0x0004;

    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyA = 0x41;
    private const ushort VirtualKeyEscape = 0x1B;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyHome = 0x24;
    private const ushort VirtualKeyDown = 0x28;

    private static readonly object Gate = new();

    /// <summary>把光标移到虚拟桌面坐标处的像素上。</summary>
    internal static void MoveTo(ScreenPoint point)
    {
        var area = Win32.VirtualScreen();

        // 绝对坐标是 0..65535 的归一化值：分母用「边长 - 1」，
        // 否则最右/最下一列像素永远够不到。
        var normalX = Normalize(point.X - area.Left, area.Width);
        var normalY = Normalize(point.Y - area.Top, area.Height);

        Send([new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    X = normalX,
                    Y = normalY,
                    Flags = MouseMove | MouseAbsolute | MouseVirtualDesk,
                },
            },
        }], "移动光标");
    }

    internal static void LeftDown() => Send([Mouse(MouseLeftDown)], "按下左键");

    internal static void LeftUp() => Send([Mouse(MouseLeftUp)], "松开左键");

    internal static void RightClick(ScreenPoint point)
    {
        MoveTo(point); Thread.Sleep(150);
        Send([Mouse(0x0008), Mouse(0x0010)], "右键点击");
    }

    /// <summary>
    /// 一次完整的拖拽：按下、沿路径分步移动、稍作悬停后松开。
    /// </summary>
    /// <remarks>
    /// 按下与松开之间<strong>不插入任何其它操作</strong>，整段序列在原子的锁里发完——
    /// OLE 拖拽要求按下状态与移动连续，中间插一个别的进程的输入就可能被当成两次操作。
    /// 悬停是为了让目标窗口先收到 <c>DragOver</c>（WPF 的接受判定在那里发生）。
    /// </remarks>
    internal static void DragPath(IReadOnlyList<ScreenPoint> path, int stepDelayMs = 25, int hoverBeforeReleaseMs = 250)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(path.Count, 2);

        lock (Gate)
        {
            MoveTo(path[0]);
            Thread.Sleep(150);

            LeftDown();
            Thread.Sleep(120);

            for (var index = 1; index < path.Count; index++)
            {
                MoveTo(path[index]);
                Thread.Sleep(stepDelayMs);
            }

            Thread.Sleep(hoverBeforeReleaseMs);
            LeftUp();
        }
    }

    /// <summary>按下 Ctrl+A（清空对话框里预填的内容）。</summary>
    internal static void SelectAll()
    {
        lock (Gate)
        {
            TypeKey(VirtualKeyControl, up: false);
            TypeKey(VirtualKeyA, up: false);
            TypeKey(VirtualKeyA, up: true);
            TypeKey(VirtualKeyControl, up: true);
        }
    }

    internal static void PressEscape() => Send([Key(VirtualKeyEscape, up: false), Key(VirtualKeyEscape, up: true)], "按下 Esc");

    /// <summary>按下回车。对话框里在文件名框上回车即等于「打开」。</summary>
    internal static void PressEnter() => Send([Key(VirtualKeyReturn, up: false), Key(VirtualKeyReturn, up: true)], "按下回车");
    internal static void PressHome() => Send([Key(VirtualKeyHome, up: false), Key(VirtualKeyHome, up: true)], "按下 Home");
    internal static void PressDown() => Send([Key(VirtualKeyDown, up: false), Key(VirtualKeyDown, up: true)], "按下方向下键");

    internal static void WheelDown(int steps = 1)
    {
        for (var i = 0; i < steps; i++)
            Send([new Input { Type = InputMouse, Data = new InputUnion {
                Mouse = new MouseInput { MouseData = unchecked((uint)-120), Flags = MouseWheel } } }], "向下滚轮");
    }

    /// <summary>
    /// 逐字符输入文本。<c>KEYEVENTF_UNICODE</c> 直接投递 UTF-16 码元，
    /// 因此中文与空格的路径不经过任何输入法或键盘布局转换。
    /// </summary>
    internal static void TypeText(string text, int characterDelayMs = 12)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (Gate)
        {
            foreach (var character in text)
            {
                Send(
                [
                    new Input
                    {
                        Type = InputKeyboard,
                        Data = new InputUnion
                        {
                            Keyboard = new KeyboardInput { VirtualKey = 0, ScanCode = character, Flags = KeyboardUnicode },
                        },
                    },
                    new Input
                    {
                        Type = InputKeyboard,
                        Data = new InputUnion
                        {
                            Keyboard = new KeyboardInput { VirtualKey = 0, ScanCode = character, Flags = KeyboardUnicode | KeyboardKeyUp },
                        },
                    },
                ], "输入字符");

                Thread.Sleep(characterDelayMs);
            }
        }
    }

    /// <summary>把一条直线插值成若干步，供拖拽使用。</summary>
    internal static IReadOnlyList<ScreenPoint> Interpolate(ScreenPoint from, ScreenPoint to, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);

        var points = new List<ScreenPoint>(steps + 1) { from };

        for (var index = 1; index <= steps; index++)
        {
            var ratio = index / (double)steps;
            points.Add(new ScreenPoint(
                (int)Math.Round(from.X + ((to.X - from.X) * ratio)),
                (int)Math.Round(from.Y + ((to.Y - from.Y) * ratio))));
        }

        return points;
    }

    private static int Normalize(int offset, int extent)
    {
        if (extent <= 1)
        {
            return 0;
        }

        var clamped = Math.Clamp(offset, 0, extent - 1);
        return (int)Math.Round(clamped * 65535.0 / (extent - 1));
    }

    private static Input Mouse(uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = flags } },
    };

    private static Input Key(ushort virtualKey, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = up ? KeyboardKeyUp : 0 },
        },
    };

    private static void TypeKey(ushort virtualKey, bool up)
    {
        Send([Key(virtualKey, up)], "按键");
    }

    /// <summary>
    /// 发送一批事件。返回数量与请求数量不符即视为被拒绝（最可能是 UIPI），
    /// 抛出的是「阻塞」而不是「失败」。
    /// </summary>
    private static void Send(Input[] inputs, string action)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

        if (sent == inputs.Length)
        {
            return;
        }

        throw new InputBlockedException(
            $"{action}：请求注入 {inputs.Length} 个事件，系统只接受了 {sent} 个。"
            + "这通常意味着目标进程的完整性级别高于本驱动（UIPI 权限隔离）。"
            + "产品没有被验证过，不能记为失败。");
    }

    // ---- 结构布局 ----

    /// <remarks>
    /// 布局必须与 Win32 的 <c>INPUT</c> 逐字节一致。x64 上是 40 字节
    /// （4 字节类型 + 4 字节对齐 + 32 字节联合体）；对不上时会静默发错事件，
    /// 所以构造时显式核对一次。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    internal static void VerifyLayout()
    {
        var size = Marshal.SizeOf<Input>();
        var expected = IntPtr.Size == 8 ? 40 : 28;

        if (size != expected)
        {
            throw new InvalidOperationException(
                $"INPUT 结构体大小是 {size} 字节，{IntPtr.Size * 8} 位进程上应为 {expected} 字节；"
                + "发送注入输入会得到错位的事件，因此拒绝继续。");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
