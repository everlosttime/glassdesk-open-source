using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace GlassDesk.DesktopAcceptance;

/// <summary>一次真实点击的结果，连「为什么没点成」一起带回来。</summary>
internal sealed record ClickOutcome(bool Clicked, string Detail);

/// <summary>
/// 用 Windows UI Automation 定位窗口与控件。
/// </summary>
/// <remarks>
/// 定位<strong>只按身份</strong>：进程、窗口类名、控件类型与自动化标识。
/// 驱动里没有任何「把坐标往某个矩形里套」的代码——上一轮失败的根因正是
/// 用找到的小窗口去套原生对话框的坐标，点到了窗口外面
/// （详见 <c>docs/阶段4-自动交互验收报告.md</c>）。
///
/// 管托 UIA 客户端没有超时参数，跨进程调用卡住就会把整个驱动挂死，
/// 所以每一次可能阻塞的查找都套了 <see cref="Guarded{T}"/> 的看门线程。
/// 超时只是放弃这次调用，看门线程本身无法取消——这是托管客户端的已知限制，
/// 因此驱动只在小范围（顶层窗口与单个对话框子树）里查找，不做全桌面遍历。
/// </remarks>
internal static class Uia
{
    /// <summary>在本进程之外等一个顶层窗口出现。</summary>
    internal static AutomationElement? WaitForTopWindow(
        int processId,
        string? className,
        string? nameContains,
        int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var found = FindTopWindow(processId, className, nameContains);
            if (found is not null)
            {
                return found;
            }

            Thread.Sleep(200);
        }

        return null;
    }

    /// <summary>找一次，不等待。</summary>
    internal static AutomationElement? FindTopWindow(int processId, string? className, string? nameContains)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);

        var matches = Guarded(
            () => AutomationElement.RootElement
                .FindAll(TreeScope.Children, condition)
                .Cast<AutomationElement>()
                .ToList(),
            8000);

        if (matches.Value is null)
        {
            return null;
        }

        foreach (var window in matches.Value)
        {
            if (className is not null && !string.Equals(Class(window), className, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (nameContains is not null && !Name(window).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return window;
        }

        return null;
    }

    /// <summary>
    /// 在全部顶层窗口里等一个符合条件的窗口（不限定进程）。
    /// </summary>
    /// <remarks>
    /// 资源管理器不是我们启动的进程，pid 事先不知道，只能按窗口类名
    /// （<c>CabinetWClass</c>）加上「标题里含本次唯一的文件夹名」来认人。
    /// 唯一文件夹名是本轮自己造的，因此不会认错到用户已经打开的窗口上。
    /// </remarks>
    internal static AutomationElement? WaitForWindow(
        string? className,
        string? nameContains,
        int timeoutMs,
        Func<AutomationElement, bool>? accept = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var windows = Guarded(
                () => AutomationElement.RootElement
                    .FindAll(TreeScope.Children, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .ToList(),
                8000);

            foreach (var window in windows.Value ?? [])
            {
                if (className is not null && !string.Equals(Class(window), className, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (nameContains is not null && !Name(window).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (accept is not null && !accept(window))
                {
                    continue;
                }

                return window;
            }

            Thread.Sleep(250);
        }

        return null;
    }

    /// <summary>控件视图里的父元素。用来读「这个编辑框属于哪个标签」。</summary>
    internal static AutomationElement? Parent(AutomationElement element)
    {
        if (element is null)
        {
            return null;
        }

        var parent = Guarded(() => TreeWalker.ControlViewWalker.GetParent(element), 4000);
        return parent.Value;
    }

    /// <summary>某个进程的全部顶层窗口元素。</summary>
    internal static IReadOnlyList<AutomationElement> TopWindowsOf(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);

        var found = Guarded(
            () => AutomationElement.RootElement
                .FindAll(TreeScope.Children, condition)
                .Cast<AutomationElement>()
                .ToList(),
            8000);

        return found.Value ?? [];
    }

    /// <summary>
    /// 等一个属于该进程的原生对话框（窗口类 <c>#32770</c>）。
    /// </summary>
    /// <remarks>
    /// <b>不能只查桌面的直接子窗口。</b>实测（见
    /// <c>docs/evidence/阶段4-桌面驱动校准记录.md</c>）：文件选择器是被宿主窗口拥有的
    /// 顶层窗口，在 UIA 的控件视图里<strong>挂在宿主窗口下面</strong>，不是桌面的直接子节点。
    /// 只查直接子节点会得到「产品没有打开选择器」这个错误结论。
    /// 因此两条路一起走：先查直接子节点，再在宿主窗口的子树里找。
    /// </remarks>
    internal static AutomationElement? WaitForDialog(int processId, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var dialog = FindDialog(processId);
            if (dialog is not null)
            {
                return dialog;
            }

            Thread.Sleep(200);
        }

        return FindDialog(processId);
    }

    internal static AutomationElement? FindDialog(int processId)
    {
        var direct = FindTopWindow(processId, "#32770", null);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var window in TopWindowsOf(processId))
        {
            var nested = Descendants(window, ControlType.Window)
                .FirstOrDefault(candidate => string.Equals(Class(candidate), "#32770", StringComparison.OrdinalIgnoreCase));

            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>按控件类型取后代。</summary>
    internal static IReadOnlyList<AutomationElement> Descendants(AutomationElement root, ControlType type)
    {
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);

        var found = Guarded(
            () => root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().ToList(),
            8000);

        return found.Value ?? [];
    }

    internal static IReadOnlyList<AutomationElement> Descendants(AutomationElement root)
    {
        var found = Guarded(
            () => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList(),
            12000);

        return found.Value ?? [];
    }

    /// <summary>直接子元素。用它读窗口结构比 descendants 便宜得多。</summary>
    internal static IReadOnlyList<AutomationElement> Children(AutomationElement root)
    {
        var found = Guarded(
            () => root.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToList(),
            6000);

        return found.Value ?? [];
    }

    internal static string Name(AutomationElement element) => SafeOr(() => element.Current.Name, string.Empty);

    internal static string Class(AutomationElement element) => SafeOr(() => element.Current.ClassName, string.Empty);

    internal static string AutomationId(AutomationElement element) => SafeOr(() => element.Current.AutomationId, string.Empty);

    internal static string ControlKind(AutomationElement element) =>
        SafeOr(() => element.Current.ControlType?.ProgrammaticName ?? "?", "?");

    internal static bool IsEnabled(AutomationElement element) => SafeOr(() => element.Current.IsEnabled, false);

    /// <remarks>读不到时按「看不见」处理：看不见的控件不能当作可点击的候选。</remarks>
    internal static bool IsOffscreen(AutomationElement element) => SafeOr(() => element.Current.IsOffscreen, true);

    internal static nint Handle(AutomationElement element) => SafeOr(() => element.Current.NativeWindowHandle, 0);

    internal static int ProcessIdOf(AutomationElement element) => SafeOr(() => element.Current.ProcessId, 0);

    internal static ScreenRect RectOf(AutomationElement element)
    {
        var rect = SafeOr<System.Windows.Rect?>(() => element.Current.BoundingRectangle, null);

        if (rect is not { } value || value.IsEmpty || double.IsNaN(value.Width) || double.IsNaN(value.Height))
        {
            return default;
        }

        return new ScreenRect(
            (int)Math.Round(value.Left),
            (int)Math.Round(value.Top),
            (int)Math.Round(value.Right),
            (int)Math.Round(value.Bottom));
    }

    internal static string Describe(AutomationElement element) =>
        $"类型 {ControlKind(element)}｜名称「{Name(element)}」｜自动化标识「{AutomationId(element)}」"
        + $"｜类 {Class(element)}｜矩形 {RectOf(element).Describe()}｜可见 {!IsOffscreen(element)}｜可用 {IsEnabled(element)}";

    /// <summary>读 Edit 控件的文本。用于「产品到底收到了什么」的当场核对。</summary>
    internal static string? ValueOf(AutomationElement element)
    {
        return SafeOr<string?>(
            () =>
            {
                if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    return null;
                }

                return ((ValuePattern)pattern).Current.Value;
            },
            null);
    }

    /// <summary>
    /// 真实点击控件中心。
    /// </summary>
    /// <remarks>
    /// 四个前置检查缺一不可，它们把「点没点中」和「产品有没有响应」分开：
    /// 矩形非空、点在虚拟桌面内、移动后点仍在控件矩形内、点上的顶层窗口属于该控件
    /// 所在进程。任何一条不满足都返回没点成，调用方记「阻塞」而不是记产品失败。
    /// </remarks>
    internal static ClickOutcome Click(AutomationElement element, string label, int settleMs = 150)
    {
        ArgumentNullException.ThrowIfNull(element);

        var rect = RectOf(element);
        if (rect.IsEmpty)
        {
            return new ClickOutcome(false, $"{label}：控件没有可点的矩形（可能尚未呈现）。{Describe(element)}");
        }

        var target = new ScreenPoint(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));
        var elementProcess = ProcessIdOf(element);
        var area = Win32.VirtualScreen();

        if (!area.Contains(target))
        {
            return new ClickOutcome(false, $"{label}：控件中心 {target.X},{target.Y} 不在虚拟桌面 {area.Describe()} 内。");
        }

        // Windows 的通知横幅之类的瞬时弹窗会盖住控件。实测在客户机里就撞上过：一条「新通知」
        // 正好压住文件对话框的文件名框与打开按钮。这种情况下盲点一下等于点了别的东西，
        // 所以这里对「点上的窗口属不属于这个控件」做几次重试，等弹窗自己消失；
        // 等不到才记阻塞，并把挡着的那个窗口写清楚。
        ClickOutcome? lastRefusal = null;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            NativeInput.MoveTo(target);
            Thread.Sleep(settleMs);

            var moved = RectOf(element);
            if (!moved.IsEmpty && !moved.Contains(target))
            {
                return new ClickOutcome(false, $"{label}：光标移入后控件矩形变成 {moved.Describe()}，点 {target.X},{target.Y} 已不在控件上。");
            }

            var top = Win32.WindowAt(target);
            var root = top == 0 ? 0 : Win32.RootAncestor(top);

            if (root != 0 && Win32.ProcessOf(root) == elementProcess)
            {
                NativeInput.LeftDown();
                Thread.Sleep(40);
                NativeInput.LeftUp();

                var note = attempt == 0 ? string.Empty : $"（等了 {attempt * 1500} ms 才轮到它）";
                return new ClickOutcome(true, $"{label}：在 {target.X},{target.Y} 完成真实按下与松开{note}。");
            }

            var owner = Win32.Describe(root);
            lastRefusal = new ClickOutcome(
                false,
                $"{label}：点 {target.X},{target.Y} 上方的顶层窗口不属于该控件的进程（控件 pid {elementProcess}），"
                + $"实际是 {owner?.Describe() ?? "（没有窗口）"}。有窗口挡在上面，这次点击无法作为证据。");

            Thread.Sleep(1500);
        }

        return lastRefusal ?? new ClickOutcome(false, $"{label}：未能完成点击。");
    }

    /// <summary>
    /// 用键盘焦点而不是鼠标点击把焦点放到控件上。
    /// </summary>
    /// <remarks>
    /// 给「点被持久弹窗挡住」的情况留的后路：通知横幅不会自己消失，而鼠标点必须落在
    /// 控件真实可见的地方。UIA 的 SetFocus 走的是键盘焦点，不依赖那一点上有什么窗口，
    /// 之后用真实击键输入仍然是真实输入，不是绕过产品。
    /// </remarks>
    internal static bool TryFocus(AutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var focused = SafeOr(
            () =>
            {
                element.SetFocus();
                return element.Current.HasKeyboardFocus;
            },
            false);

        return focused;
    }

    /// <summary>轮询等到条件成立。</summary>
    internal static bool Until(Func<bool> predicate, int timeoutMs, int pollMs = 150)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var deadline = Environment.TickCount64 + timeoutMs;

        do
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(pollMs);
        }
        while (Environment.TickCount64 < deadline);

        return predicate();
    }

    /// <summary>把一棵子树打印出来，供「第一次在目标机上跑」时核对控件身份。</summary>
    internal static string Dump(AutomationElement root, int maxElements = 240, int maxDepth = 12)
    {
        var text = new StringBuilder();
        var visited = 0;

        Walk(root, 0);
        return text.ToString();

        void Walk(AutomationElement element, int depth)
        {
            if (visited >= maxElements || depth > maxDepth)
            {
                return;
            }

            visited++;
            text.Append(new string(' ', depth * 2))
                .Append("- ")
                .AppendLine(Describe(element));

            foreach (var child in Children(element))
            {
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// 在带超时的看门线程上执行一次 UIA 调用。超时或异常都返回 null，
    /// 由调用方决定这算「找不到」还是「查不动」。
    /// </summary>
    private static (T? Value, string? Error) Guarded<T>(Func<T> work, int timeoutMs)
    {
        T? value = default;
        string? error = null;
        using var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                value = work();
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or TimeoutException)
            {
                error = ex.Message;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "uia-guard",
        };

        thread.Start();

        if (!done.Wait(timeoutMs))
        {
            return (default, $"UIA 调用超过 {timeoutMs} ms 未返回。");
        }

        return (value, error);
    }

    /// <summary>
    /// 读一个属性；元素已经消失时返回给定的兜底值而不是抛异常。
    /// </summary>
    /// <remarks>
    /// 刻意不做成「返回可空 T」：无约束的 <c>T?</c> 对值类型就是 <c>T</c> 本身，
    /// 读不到时会悄悄变成 0 或 false，调用方根本分不出来。所以兜底值必须由调用方写明。
    /// </remarks>
    private static T SafeOr<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return fallback;
        }
    }
}