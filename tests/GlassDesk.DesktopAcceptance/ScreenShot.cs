using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 存整屏截图，作为失败证据与「当时屏幕上是什么样」的记录。
/// </summary>
/// <remarks>
/// 这里存的是<strong>真实屏幕像素</strong>，不是某个窗口的 WPF 渲染图：
/// 桌面驱动力气花在真实输入上，证据也必须落在同一层。像素从 GDI 取，
/// 只借 WPF 的编码器写 PNG，因此不需要任何图像库依赖。
/// </remarks>
internal static class ScreenShot
{
    internal static string? TryCapture(string path)
    {
        try
        {
            var area = Win32.VirtualScreen();
            if (area.IsEmpty)
            {
                return null;
            }

            var pixels = Win32.CaptureBgra(area, out var stride);
            var source = BitmapSource.Create(
                area.Width,
                area.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            encoder.Save(stream);

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 截图只是证据，拍不到不能把用例判成失败。
            return null;
        }
    }
}