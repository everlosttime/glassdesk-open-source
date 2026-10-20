using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 窗口出现 / 消失 / 可见性 / 前台变化的事件源。
/// </summary>
/// <remarks>
/// 运行状态（D-07）的信号来源，结构与 <see cref="FullscreenWatcher"/> 同构：
/// 钩子装在一条自带消息循环的线程上，回调<strong>只入队</strong>——
/// 把重算合并到一个 120 毫秒的一次性定时器上，真正的窗口枚举与一切
/// 界面更新都不发生在钩子回调里。WinEvent 回调的重入与「回调线程必须泵消息」
/// 是官方文档明确提醒的两个坑，入队把前者隔断，自带线程把后者解决。
///
/// <para>
/// 监听的事件组（探针 B 实测其覆盖面，见 <c>docs/evidence/阶段4-探针实测输出.txt</c>）：
/// OBJECT_CREATE / OBJECT_DESTROY（窗口生死）、OBJECT_SHOW / OBJECT_HIDE（可见性）、
/// SYSTEM_MINIMIZESTART / SYSTEM_MINIMIZEEND（最小化）、SYSTEM_FOREGROUND（前台换人）。
/// 事件只是<strong>触发信号</strong>：通知到达后调用方必须重新枚举窗口集合
/// （<see cref="WindowLocator.EnumerateTopLevelWindows"/>），以枚举结果为准，
/// 而不是按事件语义去猜状态——单靠 CREATE/DESTROY 无法覆盖
/// 「Dock 启动前已存在的窗口」与「隐藏后重新显示」这两类情形。
/// </para>
///
/// <para>
/// 刻意<strong>不加</strong> <c>WINEVENT_SKIPOWNPROCESS</c>：本产品自己的弹出物
/// 出现/消失同样要推动状态更新，理由与 <see cref="FullscreenWatcher"/> 相同。
/// </para>
/// </remarks>
public sealed class WindowEventWatcher : IDisposable
{
    /// <summary>事件合并窗口（毫秒）。与全屏监视同一量级：判定滞后感知不到，风暴被压平。</summary>
    private const int CoalesceMilliseconds = 120;

    private static readonly uint[] WatchedEvents =
    [
        User32.EventObjectCreate,
        User32.EventObjectDestroy,
        User32.EventObjectShow,
        User32.EventObjectHide,
        User32.EventSystemMinimizeStart,
        User32.EventSystemMinimizeEnd,
        User32.EventSystemForeground,
    ];

    private readonly Action? _onChanged;
    private readonly Action<uint, nint, int, int>? _onRawEvent;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _hookThread;
    private readonly List<nint> _hooks = [];
    private readonly Lock _sync = new();

    /// <summary>回调委托必须由字段持有：被回收的委托会让原生侧调用到已释放的存根。</summary>
    private readonly User32.WinEventProc _callback;

    private Timer? _coalesceTimer;
    private uint _threadId;
    private bool _hooksInstalled;
    private volatile bool _disposed;

    private WindowEventWatcher(Action? onChanged, Action<uint, nint, int, int>? onRawEvent)
    {
        _onChanged = onChanged;
        _onRawEvent = onRawEvent;
        _callback = OnWinEvent;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "GlassDesk 窗口事件源",
        };

        _coalesceTimer = new Timer(OnCoalesceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>钩子所在线程的系统级 id。诊断用。</summary>
    public uint HookThreadId => _threadId;

    /// <summary>
    /// 启动事件源。钩子装不上时返回 null——调用方应退回按需枚举，
    /// 而不是把「装不上钩子」当成错误。
    /// </summary>
    /// <param name="onChanged">合并后的「有变化」通知，在线程池线程上触发。</param>
    /// <param name="onRawEvent">
    /// 诊断旁路：每条原始事件直接回调
    /// （事件类型、窗口句柄、OBJID、子 id）。在<strong>钩子线程</strong>上触发，
    /// 实现必须轻（记录一条日志的量级），绝不能在这里做查询或碰界面。
    /// </param>
    public static WindowEventWatcher? TryStart(Action? onChanged, Action<uint, nint, int, int>? onRawEvent = null)
    {
        var watcher = new WindowEventWatcher(onChanged, onRawEvent);

        if (watcher.Start())
        {
            return watcher;
        }

        watcher.Dispose();
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _coalesceTimer?.Dispose();
        _coalesceTimer = null;

        if (_hookThread.IsAlive)
        {
            // 让消息循环退出。卸载钩子必须发生在钩子自己的线程上：
            // 别的线程卸载时，已排队但尚未派发的事件会落到已释放的对象上。
            if (_threadId != 0)
            {
                try
                {
                    _ = User32.PostThreadMessage(_threadId, User32.WmQuit, 0, 0);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    // 卸不掉钩子，但线程是后台线程，进程退出时会自然结束。
                }
            }

            _hookThread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
    }

    private bool Start()
    {
        _hookThread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        return _hooksInstalled;
    }

    private void HookThreadMain()
    {
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();

            // 先逼出本线程的消息队列：PostThreadMessage 要求目标线程已有队列。
            _ = User32.PeekMessage(out _, 0, 0, 0, User32.PmNoRemove);

            foreach (var eventType in WatchedEvents)
            {
                var hook = User32.SetWinEventHook(
                    eventType,
                    eventType,
                    0,
                    _callback,
                    0,
                    0,
                    User32.WineventOutOfContext);

                if (hook != 0)
                {
                    _hooks.Add(hook);
                }
            }

            _hooksInstalled = _hooks.Count > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 装不上钩子只意味着「事件源不可用」，绝不能让整条线程带着
            // 未处理异常死掉——那会直接终止进程。
            _hooksInstalled = false;
        }
        finally
        {
            _ready.Set();
        }

        if (!_hooksInstalled)
        {
            return;
        }

        try
        {
            while (User32.GetMessage(out var message, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(in message);
                User32.DispatchMessage(in message);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 同上：只让监视失效。
        }

        foreach (var hook in _hooks)
        {
            User32.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    /// <summary>
    /// 事件回调。运行在钩子线程上，**绝不能让异常穿回原生栈**——那会直接终止进程。
    /// </summary>
    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed)
        {
            return;
        }

        if (objectId != User32.ObjIdWindow || childId != 0 || window == 0)
        {
            return;
        }

        // 诊断旁路在钩子线程上直呼——调用方契约要求它轻。
        try
        {
            _onRawEvent?.Invoke(eventType, window, objectId, childId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
        }

        ScheduleReconcile();
    }

    private void ScheduleReconcile()
    {
        var timer = _coalesceTimer;

        if (timer is null)
        {
            return;
        }

        try
        {
            // 只安排一次；期间重复到达的事件只是把这个时刻往后推。
            timer.Change(CoalesceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 竞争。忽略即可。
        }
    }

    private void OnCoalesceElapsed(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _onChanged?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 调用方的重算失败不拖垮事件源本身。
            _ = ex;
        }
    }
}
