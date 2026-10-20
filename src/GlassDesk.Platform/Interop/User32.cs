using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// user32 中的窗口、消息与图标入口点。
/// </summary>
/// <remarks>
/// 类上标记 <c>unsafe</c> 是为了让固定缓冲区走指针而不是托管封送：
/// 源生成的封送只接受 blittable 参数，这样每次取类名/标题都不产生
/// 中间数组，也就不必为此退回 <c>DllImport</c>。
/// </remarks>
internal static unsafe partial class User32
{
    /// <summary><c>GetWindowLong</c> 的样式索引。</summary>
    internal const int GwlStyle = -16;

    internal const int GwlExStyle = -20;

    /// <summary>窗口当前处于最大化状态。</summary>
    internal const int WsMaximize = 0x01000000;

    /// <summary>
    /// 点击窗口不会把它调到前台。程序坞的主栏靠它做到
    /// 「点了不抢焦点」；需要自己取焦点的弹出物必须显式
    /// 经 <c>WindowActivation</c> 调前台。
    /// </summary>
    internal const int WsExNoactivate = 0x08000000;

    /// <summary>
    /// 类名缓冲区的容量。Win32 约定类名不超过 256 个字符。
    /// </summary>
    private const int ClassNameCapacity = 256;

    /// <summary>标题缓冲区容量。够诊断报告用，超长会被系统截断。</summary>
    private const int WindowTextCapacity = 256;

    // ---- 事件钩子 ----

    /// <summary>前台窗口**换人**时产生。</summary>
    /// <remarks>
    /// 它只在换人时产生：同一个窗口自己改变大小（浏览器按 F11 全屏、
    /// 视频播放器切全屏都是这样）**不会**触发它。所以必须同时监听位置变化。
    /// </remarks>
    internal const uint EventSystemForeground = 0x0003;

    /// <summary>窗口被创建（<c>OBJID_WINDOW</c>）。</summary>
    internal const uint EventObjectCreate = 0x8000;

    /// <summary>窗口被销毁。</summary>
    internal const uint EventObjectDestroy = 0x8001;

    /// <summary>窗口被显示（含从隐藏恢复、从最小化还原时的可见性变化）。</summary>
    internal const uint EventObjectShow = 0x8002;

    /// <summary>窗口被隐藏。</summary>
    internal const uint EventObjectHide = 0x8003;

    /// <summary>窗口进入最小化。</summary>
    internal const uint EventSystemMinimizeStart = 0x0016;

    /// <summary>窗口离开最小化。</summary>
    internal const uint EventSystemMinimizeEnd = 0x0017;

    /// <summary>窗口位置或大小变化时产生。</summary>
    internal const uint EventObjectLocationChange = 0x800B;

    /// <summary><c>OBJID_WINDOW</c>：只关心顶层窗口自身，忽略子控件。</summary>
    internal const int ObjIdWindow = 0;

    /// <summary>在钩子安装线程的消息队列上回调，不需要把回调注入目标进程。</summary>
    internal const uint WineventOutOfContext = 0x0000;

    /// <summary>
    /// 跳过本进程产生的窗口事件。
    /// </summary>
    /// <remarks>
    /// 刻意没有使用它：本产品自己的窗口成为前台窗口时，判定同样需要更新
    /// （结论是从「全屏」变回「非全屏」），跳过自身事件会让判定停在过期值上。
    /// 见 <c>FullscreenWatcher</c> 的说明。
    /// </remarks>
    internal const uint WineventSkipOwnProcess = 0x0002;

    // ---- 消息循环 ----

    internal const uint WmQuit = 0x0012;

    internal const uint PmRemove = 0x0001;

    internal const uint PmNoRemove = 0x0000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    /// <summary>当前前台窗口；没有前台窗口时返回 0。</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    /// <summary>外壳的桌面窗口（<c>Progman</c>）。</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    /// <summary>最底层的桌面窗口。</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    /// <summary>窗口是否已最小化。最小化的窗口不可能铺满屏幕。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint window, int index);

    /// <summary>
    /// 写回窗口的样式值，返回改动前的值。
    /// </summary>
    /// <remarks>
    /// 只用于 <see cref="GwlStyle"/>、<see cref="GwlExStyle"/> 这类 **32 位**样式值。
    /// 指针长度的 <c>GWLP_*</c> 索引在 64 位进程上必须走
    /// <c>SetWindowLongPtrW</c>，不能复用这个入口——那是另一类值。
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(nint window, int index, int value);

    /// <summary>取窗口所属进程与线程的 id。</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassNameCore(nint window, char* buffer, int capacity);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowTextCore(nint window, char* buffer, int capacity);

    /// <summary>
    /// 把窗口类名写进调用方提供的缓冲区，返回写入的字符数；失败返回 0。
    /// </summary>
    /// <remarks>
    /// 提供 span 版本而不是只提供 string 版本，是为了让「这个窗口是不是外壳窗口」
    /// 这类判断不必为了比较几个字面量而分配一个字符串——它会被放在每次探测的路径上。
    /// </remarks>
    internal static int GetClassName(nint window, Span<char> destination)
    {
        if (window == 0 || destination.IsEmpty)
        {
            return 0;
        }

        fixed (char* buffer = destination)
        {
            return GetClassNameCore(window, buffer, destination.Length);
        }
    }

    /// <summary>取窗口类名；失败时返回空串。</summary>
    internal static string ReadClassName(nint window)
    {
        if (window == 0)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[ClassNameCapacity];
        var length = GetClassName(window, buffer);
        return length <= 0 ? string.Empty : new string(buffer[..length]);
    }

    /// <summary>
    /// 取窗口标题；失败或没有标题时返回空串。
    /// </summary>
    /// <remarks>
    /// 用 <c>GetWindowText</c> 而不是 <c>WM_GETTEXT</c>：对别的进程的窗口它读的是
    /// 系统缓存的标题，不会因为目标进程卡住而让我们的调用也卡住。
    /// </remarks>
    internal static string ReadWindowText(nint window)
    {
        if (window == 0)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[WindowTextCapacity];
        int length;

        fixed (char* pointer = buffer)
        {
            length = GetWindowTextCore(window, pointer, buffer.Length);
        }

        return length <= 0 ? string.Empty : new string(buffer[..length]);
    }

    // ---- 事件钩子与消息循环 ----

    internal delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);

    /// <summary>
    /// 安装事件钩子。返回 0 表示失败（例如已达系统允许的钩子数量上限）。
    /// </summary>
    /// <remarks>
    /// 这里用 <c>DllImport</c> 而不是源生成封送，与 <c>Display32.EnumDisplayMonitors</c>
    /// 同理：回调委托无法由源生成器封送。
    /// 卸载必须在安装钩子的那个线程上进行，否则已排队但尚未派发的事件会失去归宿。
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hookModule, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    /// <summary>取一条消息；收到 <c>WM_QUIT</c> 时返回 0，出错时返回 -1。</summary>
    /// <remarks>
    /// 入口点必须写 <c>GetMessageW</c>：<c>GetMessage</c> 在头文件里是个宏，
    /// DLL 里没有这个名字。同一族的 <c>PeekMessage</c> / <c>DispatchMessage</c> /
    /// <c>PostThreadMessage</c> 也一样。
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static partial int GetMessage(out Msg message, nint window, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessage(out Msg message, nint window, uint min, uint max, uint remove);

    /// <summary><c>TranslateMessage</c> 是真实导出，没有 A/W 变体。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Msg message);

    /// <summary>向指定线程投递消息。目标线程必须已有消息队列。</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    // ---- 窗口枚举与激活（阶段 3） ----

    /// <summary>取某个窗口的属主窗口；没有属主时返回 0。</summary>
    internal const int GwOwner = 4;

    /// <summary>最小化窗口。</summary>
    internal const int SwMinimize = 6;

    /// <summary>显示窗口。</summary>
    internal const int SwShow = 5;

    /// <summary>隐藏窗口（不是最小化：隐藏的窗口从屏幕与任务栏一起消失）。</summary>
    internal const int SwHide = 0;

    /// <summary>请求窗口关闭（走它的关闭流程，比强杀进程温和）。</summary>
    internal const uint WmClose = 0x0010;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    /// <summary>
    /// 读窗口属性表里的一个原子值。OLE 注册拖放目标时用 <c>OleDropTargetInterface</c>
    /// 这个名字留标记，拖放注册归属的探测靠它。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetProp(nint window, string atom);

    /// <summary>鼠标主键的虚拟键码。</summary>
    internal const int VirtualKeyLeftButton = 0x01;

    /// <summary>
    /// 键的即时物理状态（高序位 = 此刻按下）。
    /// </summary>
    /// <remarks>
    /// 与 <c>GetKeyState</c> 的区别：后者读的是调用线程消息队列里的键状态，
    /// 而拖放模态循环期间界面线程不派发消息，那时它给的是过期值。
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    /// <summary>还原一个最小化的窗口，并把它激活。</summary>
    internal const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint window, uint command);

    /// <summary>把窗口带到 z 序顶部。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint window);

    /// <summary>
    /// 把当前线程的输入队列临时接到另一个线程上。
    /// </summary>
    /// <remarks>
    /// 用于绕过前台锁定：<c>SetForegroundWindow</c> 只允许「当前拥有前台权限」的进程调用，
    /// 而前台权限属于整个输入队列。把自己接到前台窗口所属线程之后，
    /// 这次调用就落在同一个队列里，系统不再拒绝。
    /// 必须成对调用（<c>true</c> 之后再 <c>false</c>），否则两个线程的输入会一直绑在一起。
    /// </remarks>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attachFlag);

    internal delegate int EnumWindowsProc(nint window, nint parameter);

    /// <summary>
    /// 自上而下（z 序）枚举顶层窗口。
    /// </summary>
    /// <remarks>
    /// 回调委托无法由源生成器封送，因此这里用 <c>DllImport</c>，
    /// 与 <see cref="SetWinEventHook"/> 同理。
    /// </remarks>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    // ---- 图标的像素读取（阶段 3） ----

    /// <summary>把一个 <c>HICON</c> 拆成位图句柄，以便读出像素。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetIconInfo(nint icon, out IconInfo iconInfo);
}

/// <summary>
/// Win32 <c>MSG</c>。只用到 <see cref="Message"/>，其余字段是为了让布局与原生一致。
/// </summary>
/// <remarks>
/// 字段布局在 x64 上是 48 字节，与原生 <c>MSG</c> 相同；<c>GetMessage</c> 会写入整块结构，
/// 所以托管侧的结构体不能比原生的小。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    internal nint Window;
    internal uint Message;
    internal nuint WParam;
    internal nint LParam;
    internal uint Time;
    internal int PointX;
    internal int PointY;
}