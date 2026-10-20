using System.Collections.Concurrent;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>
/// 跟踪「哪些记录此刻有已运行的窗口」，供运行状态指示点（D-07）使用。
/// </summary>
/// <remarks>
/// <para>
/// 结构是<strong>初始枚举 + 事件触发 + 调和</strong>三件套，刻意不把
/// WinEvent 事件当成状态的唯一来源：
/// </para>
/// <list type="number">
/// <item><b>初始枚举</b>：<see cref="Start"/> 时立即枚举一遍窗口——Dock 启动之前
/// 就在运行的应用（占绝大多数）只有这样才能被看见；</item>
/// <item><b>事件</b>：<see cref="WindowEventWatcher"/> 只当触发信号，回调只入队，
/// 合并后的重算不发生在钩子线程上（官方文档明确提醒回调有重入风险）；</item>
/// <item><b>调和</b>：收到通知后<strong>重新枚举</strong>窗口集合，以枚举结果为准。
/// 单靠 CREATE/DESTROY 无法覆盖「隐藏后重新显示」「由其他宿主代开窗口」这类情形，
/// 枚举天然覆盖。</item>
/// </list>
///
/// <para>
/// 「运行中」的定义：存在至少一个匹配的候选顶层窗口（可见或有标题的最小化窗口）。
/// 只缩在通知区、没有任何顶层窗口的应用不算——这与 macOS 的做法一致：
/// 状态点表达的是「有窗口在」，不是「进程在」。
/// </para>
/// </remarks>
public sealed class RunningAppTracker : IDisposable
{
    /// <summary>事件合并后仍然要调和的最小间隔。事件风暴被 WindowEventWatcher 压平，这里不再重复。</summary>
    private static readonly TimeSpan MinimumReconcileInterval = TimeSpan.FromMilliseconds(150);

    private readonly AppLibraryStore _library;
    private readonly ConcurrentDictionary<string, string?> _identityCache = new();
    private readonly Lock _sync = new();

    private WindowEventWatcher? _watcher;
    private IReadOnlyList<WindowIdentityInfo> _windows = [];
    private DateTime _lastReconcileUtc = DateTime.MinValue;
    private volatile bool _disposed;
    private bool _suspended, _dirty;
    private long _runtimeGeneration, _reconcileCount;

    /// <summary>Completed enumeration count, useful for resource-lifecycle diagnostics.</summary>
    public long ReconcileCount => Interlocked.Read(ref _reconcileCount);
    public bool IsSuspended { get { lock (_sync) return _suspended; } }

    public void SetSuspended(bool suspended)
    {
        bool reconcile;
        lock (_sync)
        {
            if (_disposed || _suspended == suspended) return;
            _suspended = suspended;
            _runtimeGeneration++;
            reconcile = !suspended && _dirty;
            if (reconcile) _lastReconcileUtc = DateTime.MinValue;
        }
        if (reconcile) Reconcile();
    }

    public RunningAppTracker(AppLibraryStore library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>
    /// 运行状态有变化（可能）时触发。在线程池线程上触发，
    /// 更新界面的监听方自行切回自己的线程。
    /// </summary>
    /// <remarks>
    /// 事件是「可能变了」的信号：调和被合并与跳过时不会发。
    /// 监听方应当在收到后<strong>重新读取</strong>状态，而不是假设知道哪里变了——
    /// 这与「以枚举为准」是同一条原则在通知侧的体现。
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>最近一次调和得到的候选窗口集合。诊断与测试用。</summary>
    public IReadOnlyList<WindowIdentityInfo> LastWindows
    {
        get
        {
            lock (_sync)
            {
                return _windows;
            }
        }
    }

    /// <summary>
    /// 启动跟踪：先做初始枚举，再装事件钩子。
    /// </summary>
    /// <remarks>
    /// 钩子装不上（理论上只发生在入口点缺失的异常环境）不算失败：
    /// 状态退化为「只有初始枚举时的快照」，调用方仍然可以按需
    /// 调 <see cref="Reconcile"/> 刷新。
    /// </remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Reconcile();

        _watcher = WindowEventWatcher.TryStart(onChanged: () => _ = Reconcile());
    }

    /// <summary>立即重新枚举并刷新状态；返回本次枚举到的窗口数。</summary>
    public int Reconcile()
    {
        if (_disposed)
        {
            return 0;
        }

        long generation;
        lock (_sync)
        {
            if (_suspended) { _dirty = true; return _windows.Count; }
            // 与上次调和贴得太近就跳过：事件合并（120 毫秒）之后仍可能
            // 有成串的通知，重复的全量枚举没有意义。
            if (DateTime.UtcNow - _lastReconcileUtc < MinimumReconcileInterval)
            {
                return _windows.Count;
            }

            _lastReconcileUtc = DateTime.UtcNow;
            generation = _runtimeGeneration;
        }

        var windows = WindowLocator.EnumerateTopLevelWindows();
        bool changed;

        lock (_sync)
        {
            if (_disposed || _suspended || generation != _runtimeGeneration)
            { _dirty = true; return _windows.Count; }
            _dirty = false;
            _reconcileCount++;
            changed = !SameWindowSet(_windows, windows);
            _windows = windows;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return windows.Count;
    }

    /// <summary>这条记录此刻是否有已运行的窗口。匹配对最近一次调和的快照做，不再枚举。</summary>
    public bool IsRunning(AppEntry entry)
        => FindWindows(entry).Count > 0;

    /// <summary>这条记录此刻匹配的已运行窗口（对最近一次调和的快照匹配）。规则见 <see cref="WindowMatcher"/>。</summary>
    public IReadOnlyList<WindowIdentityInfo> FindWindows(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return WindowMatcher.MatchWindows(entry, LastWindows, WindowMatcher.ResolveAumid(entry, _identityCache));
    }

    /// <summary>
    /// 给定候选里此刻有窗口在运行的记录 id。
    /// </summary>
    /// <remarks>
    /// 以调用方给出的候选集合为准（Dock 只关心它显示的那些条目），
    /// 不自行扫描全库——500 条里绝大多数不在 Dock 上，为它们逐条
    /// 判定是在浪费每次调和的时间。
    /// </remarks>
    public IReadOnlyList<string> FindRunningIds(IEnumerable<AppEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        lock (_sync)
        {
            var running = new List<string>();

            foreach (var entry in candidates)
            {
                var aumid = WindowMatcher.ResolveAumid(entry, _identityCache);

                if (WindowMatcher.MatchWindows(entry, _windows, aumid).Count > 0)
                {
                    running.Add(entry.Id);
                }
            }

            return running;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// 两组窗口是否等价。只比较句柄集合：窗口的存在与消失才改变
    /// 「有没有在运行」，同一窗口改标题或挪位置不算。
    /// </summary>
    private static bool SameWindowSet(IReadOnlyList<WindowIdentityInfo> left, IReadOnlyList<WindowIdentityInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var handles = new HashSet<nint>(left.Select(window => window.Handle));

        foreach (var window in right)
        {
            if (!handles.Remove(window.Handle))
            {
                return false;
            }
        }

        return handles.Count == 0;
    }
}
