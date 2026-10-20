using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Platform.Displays;

namespace GlassDesk.App.Startup;

/// <summary>
/// 实测把窗口摆到指定显示器上的两种做法，判断哪一种可以承载多屏定位。
/// </summary>
/// <remarks>
/// 这是阶段 2 的关键实验，而它**不能靠推理确定**。
///
/// 需要回答的问题：要把窗口放到副屏的某个物理位置，应该怎么算？
/// 直觉做法是「用目标显示器的 DPI 把物理像素换算成 DIP，再设 WPF 的 Left/Top」。
/// 实测发现这个直觉在混合 DPI 环境里是错的：WPF 的 Left/Top 用的是**窗口当前所在
/// 显示器**的 DPI，于是从主屏（150%）往副屏（125%）摆时 X 偏了 640 物理像素——
/// 尺寸正确、位置错误，不报错，只是出现在不对的地方。
///
/// 因此这里同时实测两条路径：
///   A：WPF 的 Left/Top（按目标显示器 DPI 换算）
///   B：Win32 的 SetWindowPos（物理像素）
/// 每次实验前都把窗口复位到主屏，保证两种方式的起点一致。
/// </remarks>
internal static class WindowPlacementProbe
{
    private const int ProbeWidth = 320;
    private const int ProbeHeight = 80;

    private readonly record struct PlacementResult(PixelRect Expected, PixelRect Actual)
    {
        public int DeltaX => Actual.X - Expected.X;

        public int DeltaY => Actual.Y - Expected.Y;

        public bool IsExact => DeltaX == 0 && DeltaY == 0;

        public string Describe() =>
            $"期望 {Expected}，实际 {Actual}，偏差 ({DeltaX},{DeltaY})";
    }

    public static int Run(StringBuilder report, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(displays);

        var culture = CultureInfo.InvariantCulture;

        report.AppendLine("【多屏定位实测】");
        report.AppendLine("  目标位置取各屏工作区底部居中，也就是 Dock 将要去的地方。");
        report.AppendLine("  A = WPF 的 Left/Top（按目标屏 DPI 换算）；B = Win32 SetWindowPos（物理像素）。");
        report.AppendLine();

        if (displays.Count == 0)
        {
            report.AppendLine("✗ 没有显示器可用于摆放实验。");
            return 1;
        }

        var primary = displays.FirstOrDefault(static d => d.IsPrimary) ?? displays[0];

        var probeWindow = new Window
        {
            Width = 200,
            Height = 60,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Opacity = 0.01,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        var methodAFailures = 0;
        var methodBFailures = 0;

        try
        {
            probeWindow.Show();

            var handle = new WindowInteropHelper(probeWindow).Handle;
            if (handle == 0)
            {
                report.AppendLine("✗ 探测窗口没有取得句柄。");
                return 1;
            }

            foreach (var display in displays)
            {
                var target = BottomCentreOfWorkArea(display);

                report.AppendLine(culture, $"· {display.DeviceName}（{display.ScaleFactor:P0}）");

                // ---- 方式 A：WPF 的 Left/Top ----
                ResetToPrimary(probeWindow, handle, primary);
                var resultA = PlaceWithWpfCoordinates(probeWindow, handle, display, target);
                report.AppendLine(culture, $"    A  WPF Left/Top：{resultA.Describe()}");

                if (resultA.IsExact)
                {
                    report.AppendLine("       ✓ 落点精确");
                }
                else
                {
                    methodAFailures++;
                    report.AppendLine("       ✗ 有偏移");
                }

                // ---- 方式 B：Win32 物理像素 ----
                ResetToPrimary(probeWindow, handle, primary);
                var resultB = PlaceWithPixelRect(handle, target);
                report.AppendLine(culture, $"    B  SetWindowPos：{resultB.Describe()}");

                if (resultB.IsExact)
                {
                    report.AppendLine("       ✓ 落点精确");
                }
                else
                {
                    methodBFailures++;
                    report.AppendLine("       ✗ 有偏移");
                }

                report.AppendLine();
            }

            report.AppendLine("  小结：");
            report.AppendLine(culture, $"    方式 A（WPF DIP 换算）：{displays.Count - methodAFailures}/{displays.Count} 台显示器精确");
            report.AppendLine(culture, $"    方式 B（Win32 物理像素）：{displays.Count - methodBFailures}/{displays.Count} 台显示器精确");
        }
        finally
        {
            probeWindow.Close();
        }

        if (methodAFailures > 0 && methodBFailures == 0)
        {
            report.AppendLine("    → 采用方式 B。跨屏定位不得依赖 WPF 的 Left/Top 换算，");
            report.AppendLine("      否则混合 DPI 下窗口会出现在错误位置且不报错。");
        }
        else if (methodAFailures > 0 && methodBFailures > 0)
        {
            report.AppendLine("    → 两种方式都有偏移，需要继续排查。");
        }

        report.AppendLine();

        // 只有方式 B 偏移才算真正待解决的问题：它就是准备采用的那条路径。
        return methodBFailures;
    }

    /// <summary>目标位置：该屏工作区底部居中，留 8 DIP 边距。</summary>
    private static PixelRect BottomCentreOfWorkArea(DisplayInfo display)
    {
        var work = display.WorkArea;
        return new PixelRect(
            work.X + ((work.Width - ProbeWidth) / 2),
            work.Bottom - ProbeHeight - display.ToPixels(8),
            ProbeWidth,
            ProbeHeight);
    }

    /// <summary>把窗口复位到主屏，保证两种方式的起点一致。</summary>
    private static void ResetToPrimary(Window window, nint handle, DisplayInfo primary)
    {
        // 复位也走物理像素，避免复位本身引入偏差。
        DisplayService.MoveWindowToPixelRect(
            handle,
            new PixelRect(primary.WorkArea.X + 40, primary.WorkArea.Y + 40, ProbeWidth, ProbeHeight));

        window.UpdateLayout();
    }

    /// <summary>方式 A：用目标显示器的 DPI 把物理坐标换算成 DIP，设给 WPF。</summary>
    private static PlacementResult PlaceWithWpfCoordinates(
        Window window,
        nint handle,
        DisplayInfo display,
        PixelRect target)
    {
        window.Width = display.ToDips(target.Width);
        window.Height = display.ToDips(target.Height);
        window.Left = display.ToDips(target.X);
        window.Top = display.ToDips(target.Y);
        window.UpdateLayout();

        var actual = DisplayService.GetWindowPixelBounds(handle) ?? default;
        return new PlacementResult(target, actual);
    }

    /// <summary>方式 B：直接按物理像素摆放。</summary>
    private static PlacementResult PlaceWithPixelRect(nint handle, PixelRect target)
    {
        // 只调用一次。两次摆放与中间的 DPI 时序已经封在公共方法内部——
        // 探测程序也调用一次，正是为了验证那层封装真的生效；
        // 如果这里还需要自己补第二次，就说明封装没做到位。
        DisplayService.MoveWindowToPixelRect(handle, target);

        var actual = DisplayService.GetWindowPixelBounds(handle) ?? default;
        return new PlacementResult(target, actual);
    }
}