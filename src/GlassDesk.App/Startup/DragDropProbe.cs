using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>
/// 阶段 4 探针 C：拖放管线——同一个窗口句柄上，WPF 与原生 OLE 的注册归属。
/// </summary>
/// <remarks>
/// 结论先行（这条探针的职责就是证实它）：WPF 在 <see cref="System.Windows.UIElement.AllowDrop"/>
/// 置 true 时<strong>自己</strong>向窗口句柄注册了 OLE 的 <c>IDropTarget</c>，
/// 证据是 OLE 注册拖放目标时会在句柄属性表里留下 <c>OleDropTargetInterface</c>
/// 原子（<c>RegisterDragDrop</c> 的公开行为，经
/// <see cref="DropTargetRegistration.IsOleDropTargetRegistered"/> 探测）。因此：
///
/// <list type="number">
/// <item>外部拖入（资源管理器 → Dock）<strong>统一走 WPF 的 Drop 事件</strong>，
/// WPF 负责那一个 OLE 注册；产品代码绝不再对同一个句柄
/// <c>RegisterDragDrop</c>——两个注册方互相覆盖，行为不可预测。</item>
/// <item>拖入的文件列表经 WPF 的 <c>DataObject</c> 以 <c>FileDrop</c> 格式到达
/// （CF_HDROP 的托管形态），中文与空格路径由 WPF 负责正确传递。</item>
/// <item>跨进程真拖（从资源管理器按住拖进来）无法在无头探针里程序化模拟，
/// 属人工核对清单。</item>
/// </list>
///
/// 输出存档在 <c>docs/evidence/阶段4-探针实测输出.txt</c>。
/// </remarks>
internal static class DragDropProbe
{
    public static int Run(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new StringBuilder();
        report.AppendLine("阶段 4 探针 C：拖放管线（WPF / OLE 注册归属）");
        report.AppendLine(new string('=', 46));
        report.AppendLine(CultureInfo.InvariantCulture, $"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        var window = new Window
        {
            Width = 200,
            Height = 100,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowDrop = true,
            Content = new Border(),
        };

        try
        {
            window.Show();

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            report.AppendLine(CultureInfo.InvariantCulture, $"[C1] 测试窗口句柄 0x{handle:X}，AllowDrop = true。");

            var registered = DropTargetRegistration.IsOleDropTargetRegistered(handle);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"[C2] 句柄属性表里的 OleDropTargetInterface 原子：{(registered ? "存在" : "不存在")}");

            report.AppendLine();
            report.AppendLine(registered
                ? "结论：WPF 已自行完成 OLE IDropTarget 注册。Dock 的外部拖入走 WPF Drop 事件，"
                  + "产品代码不对同一句柄再做 RegisterDragDrop；拖入文件经 DataObject 的 FileDrop 格式到达。"
                : "结论：未探测到 OLE 注册原子——WPF 的注册时机与预期不符，需要人工核对后再定归属。");

            report.AppendLine("跨进程真拖（资源管理器 → Dock，中文与空格路径）列入人工核对清单。");
        }
        finally
        {
            window.Close();
        }

        ReportWriter.Write(report.ToString(), Path.Combine(paths.LogDirectory, "phase4-probe-dragpipe.log"));
        Console.Write(report.ToString());
        return 0;
    }
}
