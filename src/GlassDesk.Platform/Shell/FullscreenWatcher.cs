using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 事件驱动地跟踪「当前是否全屏」，供触发抑制使用。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是一条硬约束：**不做轮询**。基线报告里参考实现每 2500 毫秒启动一次
/// PowerShell 来问同一件事，那是它常驻 CPU 开销的主要来源之一。本实现改为监听
/// <c>EVENT_SYSTEM_FOREGROUND</c> 与 <c>EVENT_OBJECT_LOCATIONCHANGE</c> 两个事件，
/// 只在事件到达时重算一次判定。
/// </para>
/// <para>
/// 两个事件缺一不可：<c>EVENT_SYSTEM_FOREGROUND</c> 只在前台窗口**换人**时产生，
/// 而浏览器按 F11、播放器切全屏都是同一个窗口自己改变大小，不会触发它。
/// </para>
/// <para>
/// 钩子装在一条自带的线程上，那条线程只跑一个消息循环，不引用 WPF，
/// 因此本类型可以留在 <c>GlassDesk.Platform</c> 里（该工程刻意不引用 WPF）。
/// 回调在钩子线程上触发，需要更新界面的调用方要自行切回自己的线程。
/// </para>
/// <para>
/// 事件合并：拖动窗口时位置变化事件每秒可达数百条。重算被合并到一个 120 毫秒的延时上，
/// 也就是说判定最多滞后 120 毫秒。这个定时器**只由事件安排、只触发一次**，不是周期轮询。
/// </para>
/// <para>
/// 刻意**不加** <c>WINEVENT_SKIPOWNPROCESS</c>：本产品自己的窗口成为前台窗口时，
/// 判定结论也会从「全屏」变回「非全屏」（自身窗口被排除），那条事件同样必须收到，
/// 否则判定会停在过期值上，抑制就再也解不开了。自身窗口的事件由回调内部的
/// 「是不是前台窗口」判断挡掉大部分。
/// </para>
/// </remarks>
public sealed class FullscreenWatcher : IDisposable
{
    /// <summary>事件合并窗口（毫秒）。拖动时的事件风暴被压缩成一次重算。</summary>
    private const int CoalesceMilliseconds = 120;

    /// <summary>等待钩子线程报告安装结果的上限。</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>等待钩子线程退出的上限。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly FullscreenOptions _options;
    private readonly Action<bool>? _onChanged;
    private readonly Action? _onStateInvalidated;
    private readonly Func<long>? _getStateGeneration;
    private readonly Action<long, bool>? _onStateRefreshed;
    private readonly Action<PerformanceFullscreenObservation>? _onPerformanceObservation;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _hookThread;
    private readonly List<nint> _hooks = [];
    private readonly Lock _sync = new();

    /// <summary>回调委托必须由字段持有：被回收的委托会让原生侧调用到已释放的存根。</summary>
    private readonly User32.WinEventProc _callback;

    private Timer? _coalesceTimer;
    private uint _threadId;
    private volatile bool _hooksInstalled;
    private bool _isFullscreen;
    private volatile bool _disposed;

    private FullscreenWatcher(Action<bool>? onChanged, FullscreenOptions options,
        Action? onStateInvalidated, Func<long>? getStateGeneration,
        Action<long, bool>? onStateRefreshed, Action<PerformanceFullscreenObservation>? onPerformanceObservation)
    {
        _onChanged = onChanged;
        _options = options;
        _onStateInvalidated = onStateInvalidated;
        _getStateGeneration = getStateGeneration;
        _onStateRefreshed = onStateRefreshed;
        _onPerformanceObservation = onPerformanceObservation;
        _callback = OnWinEvent;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "GlassDesk 全屏事件钩子",
        };

        // 初值是「永不触发」：只有事件到达时才会被安排一次。
        _coalesceTimer = new Timer(OnCoalesceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>最近一次判定：当前是否有全屏应用在前台。</summary>
    public bool IsFullscreen
    {
        get
        {
            lock (_sync)
            {
                return _isFullscreen;
            }
        }
    }

    /// <summary>两个事件钩子与消息线程都仍可用时才返回 true。</summary>
    public bool IsAvailable => !_disposed && _hooksInstalled && _hookThread.IsAlive;

    /// <summary>
    /// 启动监视。任一事件钩子装不上时返回 null；要求持续抑制的被动输入功能
    /// 应拒绝启用，仍可独立按需查询的功能可以自行查询。
    /// </summary>
    /// <param name="onFullscreenChanged">
    /// 判定发生变化时回调，参数为新的判定值。在钩子线程上触发。
    /// </param>
    public static FullscreenWatcher? TryStart(Action<bool>? onFullscreenChanged = null,
        FullscreenOptions? options = null, Action? onStateInvalidated = null,
        Func<long>? getStateGeneration = null, Action<long, bool>? onStateRefreshed = null,
        Action<PerformanceFullscreenObservation>? onPerformanceObservation = null)
    {
        var watcher = new FullscreenWatcher(onFullscreenChanged, options ?? FullscreenOptions.Default,
            onStateInvalidated, getStateGeneration, onStateRefreshed, onPerformanceObservation);

        if (watcher.Start())
        {
            return watcher;
        }

        watcher.Dispose();
        return null;
    }

    /// <summary>
    /// 立即重算一次并返回最新判定。
    /// </summary>
    /// <remarks>
    /// 供调用方在真正要唤出 Dock 之前做最后一次确认，或在不关心事件的情况下按需刷新。
    /// </remarks>
    public bool Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Recompute();
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
                    User32.PostThreadMessage(_threadId, User32.WmQuit, 0, 0);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    // 卸不掉钩子，但线程是后台线程，进程退出时会自然结束。
                }
            }

            _hookThread.Join(ShutdownTimeout);
        }

        _ready.Dispose();
    }

    private bool Start()
    {
        _hookThread.Start();

        if (!_ready.Wait(StartupTimeout))
        {
            return false;
        }

        if (!_hooksInstalled)
        {
            return false;
        }

        // 初始判定必须在钩子装好之后算，否则「装钩子」与「第一次事件」之间
        // 发生的状态变化会被漏掉。
        Recompute();
        return true;
    }

    private void HookThreadMain()
    {
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();

            // 先逼出本线程的消息队列：PostThreadMessage 要求目标线程已有队列，
            // 而队列是在第一次 PeekMessage/GetMessage 时才建立的。
            User32.PeekMessage(out _, 0, 0, 0, User32.PmNoRemove);

            var foregroundHook = User32.SetWinEventHook(
                User32.EventSystemForeground,
                User32.EventSystemForeground,
                0,
                _callback,
                0,
                0,
                User32.WineventOutOfContext);
            if (foregroundHook != 0) _hooks.Add(foregroundHook);

            var locationHook = User32.SetWinEventHook(
                User32.EventObjectLocationChange,
                User32.EventObjectLocationChange,
                0,
                _callback,
                0,
                0,
                User32.WineventOutOfContext);
            if (locationHook != 0) _hooks.Add(locationHook);
            _hooksInstalled = foregroundHook != 0 && locationHook != 0;
        }
        catch (Exception)
        {
            // API 不可用或初始化失败时不宣称监视可用。启动调用方据此拒绝
            // 依赖全屏抑制的被动输入设置。
            _hooksInstalled = false;
        }
        finally
        {
            // 无论成败都要放行 Start，否则它会一直等到超时。
            _ready.Set();
        }

        if (_hooksInstalled)
        {
            try
            {
                var result = 1;
                try
                {
                    while ((result = User32.GetMessage(out var message, 0, 0, 0)) > 0)
                    {
                        User32.TranslateMessage(in message);
                        User32.DispatchMessage(in message);
                    }
                }
                catch (Exception)
                {
                    // 消息循环的任何运行时异常都使缓存失效，不能继续使用旧值。
                    result = -1;
                }

                if (result <= 0 && !_disposed) MarkMonitorUnavailable();
            }
            catch (Exception)
            {
                if (!_disposed) MarkMonitorUnavailable();
            }
        }

        _hooksInstalled = false;
        foreach (var hook in _hooks)
        {
            try { User32.UnhookWinEvent(hook); }
            catch (Exception) { /* 线程已退出；缓存保持不可用。 */ }
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

        try
        {
            if (!_hooksInstalled) return;

            if (eventType == User32.EventObjectLocationChange)
            {
                // 只关心顶层窗口自身，且只关心前台窗口：后台窗口的拖动与我们无关，
                // 这条判断把事件风暴挡在重算之外。
                if (objectId != User32.ObjIdWindow || childId != 0) return;

                if (window != User32.GetForegroundWindow()) return;
            }

            NotifyStateInvalidated();
            ScheduleRecompute();
        }
        catch (Exception) { MarkMonitorUnavailable(); }
    }

    private void ScheduleRecompute()
    {
        var timer = _coalesceTimer;

        if (timer is null)
        {
            return;
        }

        try
        {
            // 只安排一次；期间重复到达的事件只是把这个时刻往后推，
            // 不会累积成多次重算。
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

        if (!IsAvailable)
        {
            MarkMonitorUnavailable();
            return;
        }

        try
        {
            Recompute();
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 竞争。忽略即可。
        }
        catch (Exception)
        {
            MarkMonitorUnavailable();
        }
    }

    /// <summary>重算判定；每次成功都发布状态，变化回调仍只在值改变时触发。</summary>
    private bool Recompute()
    {
        var generation = _getStateGeneration?.Invoke() ?? 0;
        var observation = _onPerformanceObservation is not null ? FullscreenDetector.ReadPerformanceObservation() : default;
        var current = _onPerformanceObservation is not null ? observation.IsFullscreen ?? IsFullscreen
            : FullscreenDetector.IsFullscreenActive(_options);
        if (_getStateGeneration is not null && _getStateGeneration() != generation) return current;

        Action<bool>? callback = null;

        lock (_sync)
        {
            if (current != _isFullscreen)
            {
                _isFullscreen = current;
                callback = _onChanged;
            }
        }

        // 在锁外回调：调用方如果在回调里再调用本类型（很常见），
        // 在锁内回调会立刻死锁。
        try { _onStateRefreshed?.Invoke(generation, current); }
        catch (Exception) { /* 输入侧仍停留在 Unknown，保持安全关闭。 */ }
        try { callback?.Invoke(current); }
        catch (Exception) { /* 变化通知不应终止监视线程。 */ }
        try { _onPerformanceObservation?.Invoke(observation); }
        catch (Exception) { /* Performance notifications never escape the native callback path. */ }

        return current;
    }

    private void NotifyStateInvalidated()
    {
        try { _onStateInvalidated?.Invoke(); }
        catch (Exception) { /* 输入侧不会把旧值重新视为有效。 */ }
    }

    private void MarkMonitorUnavailable()
    {
        _hooksInstalled = false;
        try { _coalesceTimer?.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        if (!_disposed) NotifyStateInvalidated();
    }
}
