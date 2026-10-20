using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.App.Startup;

/// <summary>
/// 输出本机的显示、坐标空间与全屏检测实测数据。
/// </summary>
/// <remarks>
/// 这是阶段 2 的核心工具：多屏定位、命中区域、托盘菜单位置全都建立在坐标系是否正确之上，
/// 而坐标系错了不会报错，只会让东西出现在错误的地方。所以先把事实测出来、写下来，
/// 再在上面写代码。
///
/// 重点是把**同一台机器**的两种坐标视图并排显示：Win32 的物理像素，与 WPF 的 DIP。
/// 基线报告里那个副屏热角失效的缺陷，本质就是这两个视图被混在了一起。
///
/// 全屏检测用同一套办法：它决定 Dock 边缘唤出、热角、鼠标侧键要不要被抑制，
/// 而它同样不会报错——判错了只会表现为「Dock 不弹」或「看视频时弹出来」。
/// 因此这一节也会真的造一个全屏窗口去测。
/// </remarks>
internal static class DiagnosticRunner
{
    public static int Run(AppPaths paths, Stream? acceptanceReport = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        var failures = 0;

        report.AppendLine("GlassDesk 显示与坐标空间诊断");
        report.AppendLine(new string('=', 52));
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine(CultureInfo.InvariantCulture, $"操作系统：Windows {WindowsCapabilities.OsVersion}（Build {WindowsCapabilities.BuildNumber}）");
        report.AppendLine();

        // ---- DPI 感知 ----
        var awareness = DisplayService.ProbeDpiAwareness();

        report.AppendLine("【DPI 感知】");
        report.AppendLine(CultureInfo.InvariantCulture, $"线程上下文：{awareness.RawContext}");
        report.AppendLine(CultureInfo.InvariantCulture, $"感知等级：{awareness.Describe()}");
        report.AppendLine(CultureInfo.InvariantCulture, $"系统 DPI：{awareness.SystemDpi}（{awareness.SystemDpi / 96.0:P0}）");

        if (awareness.Level != DpiAwarenessLevel.PerMonitorAware || !awareness.IsPerMonitorV2)
        {
            failures++;
            report.AppendLine("✗ 不是 PerMonitorV2。窗口跨屏时不会按新显示器的 DPI 重新缩放，");
            report.AppendLine("  物理像素与 DIP 的换算也会失去唯一依据。见下方坐标对比。");
        }
        else
        {
            report.AppendLine("✓ 已具备每屏 DPI 感知 V2，坐标可按显示器各自换算。");
        }

        report.AppendLine();

        // ---- 显示器（物理像素） ----
        var displays = DisplayService.Enumerate();

        report.AppendLine("【显示器：Win32 物理像素】");
        if (displays.Count == 0)
        {
            failures++;
            report.AppendLine("✗ 未枚举到任何显示器。");
        }

        foreach (var display in displays)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"· {display}");
        }

        report.AppendLine();

        // ---- WPF 视图（DIP） ----
        report.AppendLine("【WPF 视图：DIP】");
        report.AppendLine(CultureInfo.InvariantCulture, $"主屏工作区：{SystemParameters.WorkArea.Width:F0}×{SystemParameters.WorkArea.Height:F0} DIP，原点 ({SystemParameters.WorkArea.Left:F0},{SystemParameters.WorkArea.Top:F0})");
        report.AppendLine(CultureInfo.InvariantCulture, $"虚拟屏幕：{SystemParameters.VirtualScreenWidth:F0}×{SystemParameters.VirtualScreenHeight:F0} DIP，原点 ({SystemParameters.VirtualScreenLeft:F0},{SystemParameters.VirtualScreenTop:F0})");

        report.AppendLine();
        report.AppendLine("【两个坐标空间的对照（关键）】");
        report.AppendLine("  物理像素来自 Win32，DIP 来自 WPF。二者只在 100% 缩放且原点重合时才相等。");
        report.AppendLine();

        foreach (var display in displays)
        {
            var physical = display.Bounds;
            var dipWidth = display.ToDips(physical.Width);
            var dipHeight = display.ToDips(physical.Height);

            report.AppendLine(CultureInfo.InvariantCulture, $"· {display.DeviceName}{(display.IsPrimary ? "（主屏）" : string.Empty)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    物理像素：原点 ({physical.X},{physical.Y})  尺寸 {physical.Width}×{physical.Height}");
            report.AppendLine(CultureInfo.InvariantCulture, $"    对应 DIP：尺寸约 {dipWidth:F0}×{dipHeight:F0}（按本屏 {display.ScaleFactor:P0} 缩放）");
            report.AppendLine(CultureInfo.InvariantCulture, $"    工作区：物理 {display.WorkArea}  →  DIP 约 {display.ToDips(display.WorkArea.Width):F0}×{display.ToDips(display.WorkArea.Height):F0}");
        }

        report.AppendLine();

        // ---- 一致性检查 ----
        report.AppendLine("【一致性检查】");

        var primary = displays.FirstOrDefault(static d => d.IsPrimary);
        if (primary is not null)
        {
            var expectedDipWidth = primary.ToDips(primary.WorkArea.Width);
            var actualDipWidth = SystemParameters.WorkArea.Width;
            var delta = Math.Abs(expectedDipWidth - actualDipWidth);

            report.AppendLine(CultureInfo.InvariantCulture, $"主屏工作区宽度：物理换算 {expectedDipWidth:F0} DIP vs WPF 报告 {actualDipWidth:F0} DIP（差 {delta:F0}）");

            // 允许 2 DIP 误差：Win32 工作区与 WPF 工作区取整方式略有不同。
            if (delta > 2)
            {
                failures++;
                report.AppendLine("✗ 两者不一致。说明换算基准与 WPF 实际使用的基准不同，");
                report.AppendLine("  任何「物理像素 ↔ DIP」的手工换算都会偏。这是需要先解决的问题。");
            }
            else
            {
                report.AppendLine("✓ 换算基准与 WPF 一致，可以按显示器 DPI 做像素↔DIP 换算。");
            }
        }

        // 混合 DPI 是坐标问题最容易暴露的场景，值得单独指出。
        var distinctScales = displays.Select(static d => d.Dpi).Distinct().Count();
        report.AppendLine(CultureInfo.InvariantCulture, $"显示器数量：{displays.Count}，不同 DPI 档位：{distinctScales}");
        if (distinctScales > 1)
        {
            report.AppendLine("· 本机是混合 DPI 环境：跨屏坐标必须在各自 DPI 下换算，不能用单一缩放系数。");
        }

        if (displays.Any(static d => d.Bounds.X < 0 || d.Bounds.Y < 0))
        {
            report.AppendLine("· 存在负坐标显示器（主屏左侧或上方）：定位计算必须支持负原点，不能假设从 0 开始。");
        }

        // 摆放实验必须用真实窗口，因为要验证的正是 WPF 与 Win32 之间的实际映射，
        // 而不是我们以为的映射。
        failures += WindowPlacementProbe.Run(report, displays);

        report.AppendLine();

        // 全屏检测同样必须用真实窗口验证：它能被触发的形态（无边框全屏、最大化、
        // 副屏全屏、独占全屏的游戏）之间差别很大，靠读代码判断不了。
        failures += FullscreenProbe.Run(report, displays);

        report.AppendLine();
        report.AppendLine(new string('=', 52));
        report.AppendLine(failures == 0 ? "结论：坐标空间与全屏检测均可用于后续实现。" : $"结论：有 {failures} 项需要先解决。");

        var logPath = Path.Combine(paths.LogDirectory, "diagnose.txt");
        var text = report.ToString();
        if (acceptanceReport is null) ReportWriter.Write(text, logPath);
        else
        {
            AcceptanceEntryPoint.WriteText(acceptanceReport, text);
            ReportWriter.Write(text, null);
        }

        return failures == 0 ? 0 : 1;
    }
}
