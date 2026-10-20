using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>
/// 实测全屏检测的两条路径，并把它们的差异如实报告出来。
/// </summary>
/// <remarks>
/// 这是阶段 2 「全屏检测」的验收工具。检测本身不难写，难的是知道它在哪些窗口形态下
/// 会给出什么结论——所以这里除了报告当前状态，还会**真的造一个全屏窗口**，
/// 把两条路径在真实窗口上的表现测出来。
///
/// 之所以必须实测而不能靠推理：路径一（窗口矩形）与路径二（外壳通知状态）用的是
/// 两套独立的判断，它们在「无边框最大化」「独占全屏的游戏」这些形态上会不一致，
/// 而哪一个在什么情况下更可信，只能测出来。
/// </remarks>
internal static class FullscreenProbe
{
    /// <summary>耗时统计的采样次数。</summary>
    private const int TimingIterations = 50;

    /// <summary>廉价查询的采样次数（它比完整探测便宜，所以采得更多）。</summary>
    private const int CheapTimingIterations = 200;

    /// <summary>全屏窗口保持显示的时长，够事件钩子跑完一轮合并窗口。</summary>
    private const int FullscreenHoldMilliseconds = 500;

    public static int Run(StringBuilder report, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(displays);

        var culture = CultureInfo.InvariantCulture;
        var failures = 0;

        report.AppendLine("【全屏检测】");
        report.AppendLine("  路径一：前台窗口矩形与所在显示器边界比较（GetWindowRect，并用 DWM 可见边框修正）。");
        report.AppendLine("  路径二：SHQueryUserNotificationState，即 Windows 判断「要不要弹通知」时用的那套。");
        report.AppendLine(culture, $"  任务栏自动隐藏：{(FullscreenDetector.IsTaskbarAutoHidden() ? "是——此时最大化窗口与全屏在几何上无法区分" : "否")}");
        report.AppendLine();

        // ---- 当前实际前台窗口 ----
        var current = FullscreenDetector.Probe();
        AppendSnapshot(report, "当前实际前台窗口", current);
        report.AppendLine();

        // ---- 耗时 ----
        report.AppendLine("  · 查询耗时");

        var windowSamples = Sample(TimingIterations, static () => FullscreenDetector.Probe().WindowPath.Duration);
        var shellSamples = Sample(TimingIterations, static () => FullscreenDetector.Probe().ShellPath.Duration);
        var cheapWatch = Stopwatch.StartNew();
        for (var i = 0; i < CheapTimingIterations; i++)
        {
            _ = FullscreenDetector.IsFullscreenActive();
        }

        cheapWatch.Stop();

        report.AppendLine(culture, $"      路径一（完整探测，含取类名/标题/进程名）：{Describe(windowSamples)}");
        report.AppendLine(culture, $"      路径二（外壳调用）：{Describe(shellSamples)}");
        report.AppendLine(culture, $"      廉价查询 IsFullscreenActive（只走路径一，不做描述性查询）："
            + $"平均 {cheapWatch.Elapsed.TotalMicroseconds / CheapTimingIterations:F2} 微秒（{CheapTimingIterations} 次）");

        // 这条结论决定了触发路径该用哪个 API。判据不只是倍数，还有稳定性：
        // 实测外壳调用的耗时随状态变化极大（同一台机器上见过 1.9 微秒到 2.5 毫秒），
        // 而窗口几何那条路径稳定在十微秒以内。
        var cheapPerCall = cheapWatch.Elapsed.TotalMicroseconds / CheapTimingIterations;
        var shellMedian = shellSamples[shellSamples.Length / 2];

        report.AppendLine(culture, $"      → 外壳调用中位数为廉价查询的 {shellMedian / cheapPerCall:F1} 倍（最大 {shellSamples[^1]:F0} 微秒，且随状态波动）；因此触发路径只用路径一，外壳状态留给诊断与低频确认。");
        report.AppendLine("        另一条理由在下一段实测里：外壳那条路径看不到副屏上的全屏窗口。");

        report.AppendLine();

        // ---- 事件钩子与真全屏窗口 ----
        failures += RunLiveFullscreenTest(report, displays);

        report.AppendLine();
        AppendKnownBoundaries(report);

        report.AppendLine();
        return failures;
    }

    /// <summary>
    /// 已知边界：哪些形态验证过、哪些不可靠、哪些没验证。
    /// </summary>
    /// <remarks>
    /// 这些结论来自 <c>tmp/fullscreen-probe</c> 的实测（本机、本版本），
    /// 写进诊断是为了让「检测到什么程度」这件事跟着构建产物走，
    /// 而不是只留在一次性的实验记录里。
    /// </remarks>
    private static void AppendKnownBoundaries(StringBuilder report)
    {
        report.AppendLine("  · 已知边界（本机实测，完整数据见 tmp/fullscreen-probe 的输出）");
        report.AppendLine("      前提：进程必须声明 PerMonitorV2。缺清单时 GetWindowRect 返回虚拟坐标、DWM 可见边框");
        report.AppendLine("            返回物理像素，两者混用会让副屏全屏被误判成非全屏（实测过）。");
        report.AppendLine("      ✓ 无边框铺满主屏：两条路径一致判为全屏");
        report.AppendLine("      ✓ 无边框 + 最大化：两条路径一致判为全屏");
        report.AppendLine("      ✓ 普通窗口最大化：可见边框停在工作区（1368 < 1440），两条路径一致判为非全屏");
        report.AppendLine("      ✓ 带边框窗口被放大到整屏：GetWindowRect 恰好等于整屏，可见边框 (9,0 2542×1431) 小一圈，");
        report.AppendLine("        判为非全屏。DWM 可见边框的修正正是为这一条而加——只看 GetWindowRect 会误判成全屏。");
        report.AppendLine("      ✓ 同一窗口内切换全屏（F11 式，前台窗口不变）：靠位置变化事件，两次翻转都收到");
        report.AppendLine("      ✓ 全屏窗口最小化：判定立刻解除");
        report.AppendLine("      ✗ 副屏上的全屏窗口：路径一判为全屏，路径二漏检（报非全屏），两者不一致");
        report.AppendLine("      ✗ 后台有全屏窗口、前台是别的小窗口：不抑制。检测只看前台窗口，这是取舍");
        report.AppendLine("      ⚠ 任务栏自动隐藏时：最大化窗口与全屏在几何上无法区分，任何基于矩形的判定都分不开");
        report.AppendLine("      ⚠ 独占全屏（D3D）的游戏：本机无法造出，未实测；路径二有专门状态可识别");
        report.AppendLine("      ⚠ 路径二自身不稳定：实测耗时随状态在 1.9 微秒到 2.5 毫秒之间波动，且见过「非全屏窗口在前台却报 Busy」");
    }

    /// <summary>
    /// 造一个真的全屏窗口，验证两条路径与事件钩子在真实窗口上的表现。
    /// </summary>
    private static int RunLiveFullscreenTest(StringBuilder report, IReadOnlyList<DisplayInfo> displays)
    {
        var culture = CultureInfo.InvariantCulture;
        var failures = 0;

        var primary = displays.FirstOrDefault(static d => d.IsPrimary)
            ?? (displays.Count > 0 ? displays[0] : null);

        if (primary is null)
        {
            report.AppendLine("  · 实测全屏窗口：跳过——没有可用于铺满的显示器。");
            return 1;
        }

        report.AppendLine("  · 实测全屏窗口（无边框、置顶、铺满主屏；会短暂全屏显示，属预期）");

        // 关掉自身排除的监视器：用来验证事件链路（钩子 → 合并 → 重算 → 回调）确实是通的。
        var counting = new ChangeCounter();
        using var countingWatcher = FullscreenWatcher.TryStart(
            counting.Record,
            new FullscreenOptions { ExcludeCurrentProcess = false });

        // 产品默认设置的监视器：它应当对这一变化**无动于衷**，
        // 这正是「Dock 不会把自己的窗口当成全屏应用」的保证。
        var selfIgnoring = new ChangeCounter();
        using var selfIgnoringWatcher = FullscreenWatcher.TryStart(selfIgnoring.Record);

        report.AppendLine(culture, $"      事件钩子：{(countingWatcher is null ? "✗ 安装失败（SetWinEventHook 返回 0）" : "✓ 已安装（EVENT_SYSTEM_FOREGROUND + EVENT_OBJECT_LOCATIONCHANGE）")}");

        if (countingWatcher is null)
        {
            // 钩子装不上不等于检测不可用：调用方会退回按需查询。
            report.AppendLine("        钩子不可用时按需查询仍然有效，因此不计为失败。");
        }

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x18)),
            Content = new TextBlock
            {
                Text = "GlassDesk 全屏检测实测中（约 1 秒后自动退出）",
                Foreground = Brushes.White,
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        try
        {
            window.Show();

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0)
            {
                report.AppendLine("      ✗ 探测窗口没有取得句柄，未能实测。");
                return 1;
            }

            // 铺满主屏的**全部**边界（含任务栏所占的条带），这才是真正的全屏窗口；
            // 只铺满工作区的窗口是「最大化」，不是全屏。
            // 用物理像素摆放：WPF 的 Left/Top 在混合 DPI 下会偏，见多屏定位实测。
            DisplayService.MoveWindowToPixelRect(handle, primary.Bounds);
            PumpFor(60);
            DisplayService.MoveWindowToPixelRect(handle, primary.Bounds);

            window.Activate();
            _ = WindowActivation.TryBringToForeground(handle);
            PumpFor(FullscreenHoldMilliseconds);

            var actual = DisplayService.GetWindowPixelBounds(handle);
            var selfProbe = FullscreenDetector.Probe();
            var testProbe = FullscreenDetector.Probe(new FullscreenOptions { ExcludeCurrentProcess = false });
            var isForeground = selfProbe.Foreground.Handle == handle;

            report.AppendLine(culture, $"      探测窗口：句柄 0x{handle:X}，摆放结果 {actual}（目标主屏 {primary.Bounds}）");

            // 阳性用例是否成立。不成立时下面的检查全都无从谈起——它们需要「本进程有一个
            // 真正处于前台的铺满窗口」这个前提。
            //
            // 这一点必须与「检测坏了」严格分开：从脚本或后台启动时 Windows 会拒绝
            // 前台切换，那是**用例构造失败**，不是产品缺陷。早先这里照旧计入失败，
            // 于是诊断会报出「三项失败」而实际上什么都没验证——那比不报更糟，
            // 会把人引去查一个并不存在的问题。
            var positiveCaseValid = isForeground;

            if (!positiveCaseValid)
            {
                report.AppendLine(culture, $"      ⚠ 没能把探测窗口切到前台（当前前台是「{selfProbe.Foreground.ProcessName}」）。");
                report.AppendLine("        **本次全屏检测的阳性用例未生效，以下三项标记为「未验证」，不计入失败。**");
                report.AppendLine("        它们只说明代码没有崩，不能作为检测正确性的证据。");
                report.AppendLine("        从脚本或后台启动时 Windows 会拒绝前台切换，属可预期的情形；");
                report.AppendLine("        要以人工方式复测，请在交互式会话里直接运行 --diagnose。");
            }

            var unverified = 0;

            report.AppendLine(culture, $"      排除自身时：路径一 = {DescribeVerdict(selfProbe.WindowPath.Verdict)}（期望「已排除」），路径二 = {selfProbe.ShellPath}");

            if (!positiveCaseValid)
            {
                unverified++;
                report.AppendLine("      ○ 未验证：自身窗口排除（需要探测窗口真正在前台）");
            }
            else if (selfProbe.WindowPath.Verdict != FullscreenVerdict.Excluded)
            {
                failures++;
                report.AppendLine("      ✗ 本产品自己的全屏窗口没有被排除。这会让 Dock 把自己判成全屏应用，从此不再响应唤出。");
            }
            else
            {
                report.AppendLine("      ✓ 自身窗口被正确排除：Dock 不会因为自己的窗口而自我抑制。");
            }

            report.AppendLine(culture, $"      不排除自身时：路径一 = {DescribeVerdict(testProbe.WindowPath.Verdict)}（期望「全屏」），路径二 = {testProbe.ShellPath}");

            if (!positiveCaseValid)
            {
                unverified++;
                report.AppendLine("      ○ 未验证：路径一把铺满窗口判为全屏");
            }
            else if (testProbe.WindowPath.Verdict != FullscreenVerdict.Fullscreen)
            {
                failures++;
                report.AppendLine("      ✗ 路径一没有把这个窗口判成全屏，说明铺满判定有问题。");
            }
            else if (!testProbe.ShellPath.IsFullscreen)
            {
                report.AppendLine("      ⚠ 路径二没有报告全屏（它不把「无边框窗口铺满整屏」算作全屏）。这是两条路径的已知差异，");
                report.AppendLine("        产品对此取「或」，因此不会漏掉这种形态。副屏上的全屏窗口路径二也看不到——");
                report.AppendLine("        见 tmp/fullscreen-probe 的跨进程实测：路径一说全屏、路径二说非全屏。");
            }
            else
            {
                report.AppendLine("      ✓ 两条路径都判为全屏。");
            }

            // ---- 事件钩子 ----
            var recorded = counting.Snapshot();
            var selfRecorded = selfIgnoring.Snapshot();

            if (countingWatcher is not null)
            {
                var trail = recorded.Length == 0
                    ? string.Empty
                    : $"（{string.Join(" → ", recorded.Select(static v => v ? "全屏" : "非全屏"))}）";

                report.AppendLine(culture, $"      事件钩子收到判定变化：{recorded.Length} 次{trail}");

                if (recorded.Length == 0 && !positiveCaseValid)
                {
                    unverified++;
                    report.AppendLine("      ○ 未验证：事件钩子收到判定变化（窗口没能成为前台，本就不该有变化）");
                }
                else if (recorded.Length == 0)
                {
                    failures++;
                    report.AppendLine("      ✗ 钩子装了但没收到任何变化，事件链路有问题（判定最多滞后 120 毫秒的事件合并窗口）。");
                }
                else if (recorded[0])
                {
                    report.AppendLine("      ✓ 事件驱动链路有效：没有轮询，全靠 SetWinEventHook 触发的重算。");
                }
            }

            report.AppendLine(culture, $"      排除自身的监视器收到变化：{selfRecorded.Length} 次（期望 0——自身窗口不该改变判定）");

            if (selfRecorded.Length != 0)
            {
                // 这一条与前台无关：无论窗口是否成为前台，自身窗口都不该改变判定。
                failures++;
                report.AppendLine("      ✗ 自身窗口触发了判定变化，与「排除自身」的约定矛盾。");
            }

            if (unverified > 0)
            {
                report.AppendLine(culture, $"      —— 本次有 {unverified} 项未验证（详见上方「未生效」说明），失败计数不含它们。");
            }
        }
        finally
        {
            window.Close();
        }

        // 窗口关掉之后再确认一次恢复：抑制必须能自己解除，否则一次误判就会永久生效。
        PumpFor(FullscreenHoldMilliseconds);
        var afterClose = FullscreenDetector.Probe();

        report.AppendLine(culture, $"      关闭后：{afterClose.Describe()}；结论：{(afterClose.ShouldSuppressTriggers ? "仍抑制触发" : "不再抑制")}");

        if (afterClose.WindowPath.Verdict == FullscreenVerdict.Fullscreen)
        {
            failures++;
            report.AppendLine("      ✗ 全屏窗口已关闭，路径一仍判为全屏。");
        }

        return failures;
    }

    private static void AppendSnapshot(StringBuilder report, string title, FullscreenSnapshot snapshot)
    {
        var culture = CultureInfo.InvariantCulture;

        report.AppendLine(culture, $"  · {title}");
        report.AppendLine(culture, $"      前台窗口：{snapshot.Foreground.ProcessName}（进程 {snapshot.Foreground.ProcessId}）"
            + $"  类名 {snapshot.Foreground.ClassName}{(string.IsNullOrEmpty(snapshot.Foreground.Title) ? string.Empty : $"  标题「{snapshot.Foreground.Title}」")}");
        report.AppendLine(culture, $"      路径一：{DescribeVerdict(snapshot.WindowPath.Verdict)}——{snapshot.WindowPath.Reason}");
        report.AppendLine(culture, $"        GetWindowRect = {Describe(snapshot.WindowPath.WindowRect)}");
        report.AppendLine(culture, $"        DWM 可见边框 = {Describe(snapshot.WindowPath.VisibleFrame)}");
        report.AppendLine(culture, $"        所在显示器 = {snapshot.WindowPath.MonitorDeviceName} {Describe(snapshot.WindowPath.MonitorBounds)}");
        report.AppendLine(culture, $"      路径二：{snapshot.ShellPath.State}（{(snapshot.ShellPath.IsFullscreen ? "全屏" : "非全屏")}）"
            + $"{(snapshot.ShellPath.Error is null ? string.Empty : $"——{snapshot.ShellPath.Error}")}");
        report.AppendLine(culture, $"      两路一致：{(snapshot.PathsAgree ? "是" : "否")}；抑制触发：{(snapshot.ShouldSuppressTriggers ? "是" : "否")}");
    }

    /// <summary>采集若干次耗时样本，返回升序排列的微秒值。</summary>
    private static double[] Sample(int iterations, Func<TimeSpan> measure)
    {
        var samples = new double[iterations];

        for (var i = 0; i < iterations; i++)
        {
            samples[i] = measure().TotalMicroseconds;
        }

        Array.Sort(samples);
        return samples;
    }

    private static string Describe(double[] sorted) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"最小 {sorted[0]:F2} 微秒，中位 {sorted[sorted.Length / 2]:F2} 微秒，最大 {sorted[^1]:F2} 微秒");

    private static string Describe(PixelRect? rect) => rect?.ToString() ?? "（不可用）";

    private static string DescribeVerdict(FullscreenVerdict verdict) => verdict switch
    {
        FullscreenVerdict.Fullscreen => "全屏",
        FullscreenVerdict.NotFullscreen => "非全屏",
        FullscreenVerdict.Excluded => "已排除",
        _ => "无法判定",
    };

    /// <summary>
    /// 推进消息循环一段时间。
    /// </summary>
    /// <remarks>
    /// 只用于诊断里的等待：窗口激活、DPI 变更、事件钩子的重算都要靠消息循环才能结算。
    /// 这里用 <c>DispatcherFrame</c> 真的把消息泵起来，而不是「睡一会儿再查一次」——
    /// 后者会让激活消息一直排着，等多久都不会生效。
    /// </remarks>
    private static void PumpFor(int milliseconds)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();

        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            dispatcher);

        timer.Start();

        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// 从钩子线程收集判定变化。回调不在界面线程上，所以写入必须加锁。
    /// </summary>
    private sealed class ChangeCounter
    {
        private readonly List<bool> _values = [];

        public void Record(bool value)
        {
            lock (_values)
            {
                _values.Add(value);
            }
        }

        public bool[] Snapshot()
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }
}